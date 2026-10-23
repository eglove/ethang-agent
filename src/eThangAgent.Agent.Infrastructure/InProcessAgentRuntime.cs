using System.Collections.Concurrent;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure;

/// <summary>In-process actor runtime: every accepted child runs to completion on one background task
/// while the caller continues. A strict concurrency cap is enforced with a zero-timeout slot wait;
/// an at-capacity start is QUEUED, never rejected and never silently dropped (issue #238): the
/// queued child owns a settle source and a mailbox from the moment it is parked, so agent.wait
/// blocks on it instead of failing NotFound, agent.send reaches it, and agent.cancel removes it
/// with a well-formed Failed(Interrupted) outcome. A settled run hands its slot DIRECTLY to the
/// queue head under the admission gate — never releasing it to a fresh Start first — so the queue
/// drains in priority-then-FIFO order with no starvation and no over-capacity race. An admission
/// fault (the run could not begin) terminalizes the record and settles its waiters: a parked
/// spawn can never leave a Running row with no run behind it.
/// Each active run owns a CancellationTokenSource registered here, so <see cref="Interrupt"/> can
/// cancel one or all runs; runners observe the token and persist well-formed terminal outcomes.</summary>
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
// Named decision: the runtime is a process-lifetime singleton owned by the composition
// root; disposing the semaphore on teardown adds no value.
public sealed class InProcessAgentRuntime : IAgentRuntime
{
  private readonly IAgentRunner _runner;
  private readonly IAgentStore _store;
  private readonly int _capacity;
  private readonly ConcurrentDictionary<AgentId, CancellationTokenSource> _active = [];

  // One completion source per child id: WhenSettledAsync awaits it; a watchdog same-id
  // retry REUSES the source so existing waiters survive the retry (FR-L3).
  private readonly ConcurrentDictionary<AgentId, TaskCompletionSource<AgentRunOutcome>> _settling = [];

  // Per-child mailbox registry: Deliver from any sender; Drain by the owner loop at safe
  // points. Between-turn durability rides IMailboxStore (FR-C5).
  private readonly ConcurrentDictionary<AgentId, BoundedAgentMailbox> _mailboxes = [];
  private readonly ChildMailboxRegistry? _registry;

  // Per-agent preemption grant (D1): whether THIS agent's contract grants Urgent
  // preemption. Consulted by Deliver when the sender is a granted agent.
  private readonly ConcurrentDictionary<Guid, bool> _preemptGrants = [];

  // Concurrency-boundary admission (FR-B6, issue #238): ONE gate owns both the slot count
  // and the waiter list, so a settled run hands its slot DIRECTLY to the head waiter
  // inside the same critical section. Releasing the slot first and letting a fresh Start
  // race the queue head is what starved parked spawns and let the runtime run over cap.
  private readonly Lock _admissionGate = new();
  private readonly List<SpawnWaiter> _waiters = [];
  private int _running;

  private sealed record SpawnWaiter(AgentRecord Record, int Priority, DateTimeOffset EnqueuedAt);
  private readonly IMailboxStore? _mailboxStore;
  private readonly IAgentEvents? _events;
  private readonly ChildSupervisorRegistry? _supervisors;

  public InProcessAgentRuntime(IAgentRunner runner, IAgentStore store, int maxConcurrentAgents,
      IMailboxStore? mailboxStore = null, IAgentEvents? events = null,
      ChildSupervisorRegistry? supervisors = null, ChildMailboxRegistry? mailboxes = null)
  {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(store);
    if (maxConcurrentAgents < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentAgents), maxConcurrentAgents,
          "MaxConcurrentAgents must be at least 1.");
    }

    _runner = runner;
    _store = store;
    _mailboxStore = mailboxStore;
    _events = events;
    _supervisors = supervisors;
    _registry = mailboxes;
    _capacity = maxConcurrentAgents;
  }

  public async Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    // Every accepted start owns its settle source and mailbox BEFORE admission, so a
    // parked spawn is awaitable and steerable from the moment the caller is told it
    // started (issue #238). Ownership is what makes the queued state observable instead
    // of a NotFound from agent.wait.
    CancellationTokenSource cts = new();
    _active[record.Id] = cts;
    BoundedAgentMailbox mailbox = new();
    _mailboxes[record.Id] = mailbox;
    _registry?.Register(record.Id, mailbox);
    _ = RehydrateAsync(record.Id, mailbox);
    _ = _settling.AddOrUpdate(record.Id,
        static _ => NewSettleSource(),
        static (_, existing) => existing.Task.IsCompleted ? NewSettleSource() : existing);

    bool admitted;
    lock (_admissionGate)
    {
      admitted = _running < _capacity;
      if (admitted)
      {
        _running++;
      }
      else
      {
        QueueLocked(record);
      }
    }

    if (admitted)
    {
      await AdmitAsync(record, cts, ct).ConfigureAwait(false);
    }

    return Result.Success(record.Id);
  }

  /// <summary>The one admission path: persist the Queued -> Running transition, then
  ///     dispatch the run. The flip is AWAITED before the run starts, so a caller reading
  ///     the row right after Start returns (the spawn receipt does exactly that) sees the
  ///     state the child is really in, and the flip can never land after a terminal write
  ///     and resurrect a settled row. A record already Running (plain start, resume,
  ///     watchdog retry) is not rewritten — the legacy write shape is untouched.</summary>
  private async Task AdmitAsync(AgentRecord record, CancellationTokenSource cts, CancellationToken ct)
  {
    if (record.Status is AgentStatus.Queued)
    {
      _ = await _store.UpdateAsync(record with { Status = AgentStatus.Running }, ct).ConfigureAwait(false);
    }

    _ = BeginRun(record, cts);
  }

  /// <summary>Parks a start at the concurrency boundary, ordered by priority then FIFO
  ///     (FR-B6). The waiter is visible and cancellable — cancelling interrupts (removes)
  ///     it without starting anything. Caller holds <see cref="_admissionGate"/>.
  ///     Unknown-contract records default priority 0 (SpawnRequest's default flows into
  ///     the record via the spawn command; children of children re-spawn with their own).</summary>
  private void QueueLocked(AgentRecord record)
  {
    SpawnWaiter waiter = new(record, PriorityOf(record), DateTimeOffset.UtcNow);
    _waiters.Add(waiter);
    _waiters.Sort((a, b) =>
    {
      int byPriority = b.Priority.CompareTo(a.Priority);
      return byPriority != 0 ? byPriority : a.EnqueuedAt.CompareTo(b.EnqueuedAt);
    });
  }

  /// <summary>Priority fact for the admission order. A corrupt contract column is an
  ///     infrastructure fault the ADMISSION path must survive: an undecodable agreement
  ///     cannot grant preemption, so the record takes the default priority and the fault
  ///     surfaces where it can be reported (BeginRun terminalizes it) instead of throwing
  ///     out of a queue insertion.</summary>
  private static int PriorityOf(AgentRecord record)
  {
    if (record.Contract is not { } contractJson)
    {
      return 0;
    }

    try
    {
      return SpawnContract.Decode(contractJson).PreemptGrant ? 1 : 0;
    }
    // Named decision (CA1031): priority is an admission-ordering hint, never a gate on
    // the spawn; a corrupt contract is terminalized by BeginRun with a well-formed
    // failure rather than silently killing the queue.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return 0;
    }
  }

  /// <summary>Hands a released slot to the highest-priority waiter INSIDE the admission
  ///     gate, so a fresh Start can never consume the slot the queue head is owed
  ///     (starvation) and two runs can never admit past the cap. Runs on the
  ///     slot-release path: queue entry stays strictly push-driven.</summary>
  private void ReleaseSlotAndAdmitNext()
  {
    SpawnWaiter? head;
    CancellationTokenSource? cts = null;
    lock (_admissionGate)
    {
      _running--;
      if (_waiters.Count == 0)
      {
        return;
      }

      head = _waiters[0];
      _waiters.RemoveAt(0);
      // The slot transfers to the head: _running is decremented then re-incremented in
      // one critical section, so the count never dips below what is actually running.
      _running++;
      if (_active.TryGetValue(head.Record.Id, out CancellationTokenSource? owned))
      {
        cts = owned;
      }
    }

    // An interrupted queued spawn has no CTS left: it was terminalized and settled by
    // Interrupt, so admission must not resurrect it. The slot is handed on instead.
    if (cts is null)
    {
      ReleaseSlotAndAdmitNext();
      return;
    }

    // Same admission path a fresh Start takes: the queued row flips to Running before
    // the run is dispatched, so a reader never sees a running child's row say queued.
    _ = AdmitAsync(head.Record, cts, CancellationToken.None);
  }

  /// <summary>Starts the run for an ADMITTED record. The settle source, mailbox, and
  ///     cancellation token were created at Start (a queued spawn owns them while it
  ///     waits), so this method only registers the run's supervision facts and dispatches.
  ///     A record still marked Queued is flipped to Running first: admission is the
  ///     transition, and a reader must never see a running child's row say queued.
  ///     <para>An admission FAULT (anything thrown while registering the run) is
  ///     terminalized here — the record becomes Failed(ProviderError) with the cause in
  ///     its report, its waiters settle with that outcome, and the slot is handed on.
  ///     Without this, a queued spawn whose start failed would keep a Running row with no
  ///     run behind it: exactly the silent loss issue #238 reports.</para></summary>
  private Result<AgentId> BeginRun(AgentRecord record, CancellationTokenSource cts)
  {
    try
    {
      if (_supervisors is not null)
      {
        ChildSupervisor supervisor = new(record.Id, _events ?? NullAgentEvents.Instance, TimeProvider.System, ceilings: null);
        supervisor.OnStart(record.Attempts);
        _supervisors.Register(record.Id, supervisor);
      }

      // Admission needs the REAL agreement (queue ordering may tolerate a corrupt
      // contract as a priority hint, but the run cannot enact a grant set it cannot read):
      // a decode fault here is the admission fault the catch below terminalizes.
      _preemptGrants[record.Id.Value] = record.Contract is { } contractJson
          && SpawnContract.Decode(contractJson).PreemptGrant;
      _ = Task.Run(() => RunToCompletionAsync(record, cts), CancellationToken.None);
      return Result.Success(record.Id);
    }
    // Named decision (CA1031): an admission fault must become a well-formed terminal
    // outcome for the child's waiters, never an escaping exception that strands the row.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      string report = "Error [ProviderError]: the child run could not be admitted (admission fault): " + ex.Message;
      AgentRunOutcome outcome = new(record.Id, AgentStatus.Failed, AgentFailureReason.ProviderError,
          report, record.ModelUsed, record.Depth);
      _ = Task.Run(async () =>
      {
        // The run never began: it must not stay in the active set (orphan repair trusts
        // that set as ownership) and its token must not leak.
        if (_active.TryRemove(record.Id, out CancellationTokenSource? abandoned))
        {
          abandoned.Dispose();
        }

        await TerminalUpdateAsync(record, AgentStatus.Failed, AgentFailureReason.ProviderError, report)
            .ConfigureAwait(false);
        CloseMailbox(record.Id, outcome);
        _ = Settle(record.Id, outcome);
        ReleaseSlotAndAdmitNext();
      }, CancellationToken.None);
      return Result.Failure<AgentId>(new DomainError("ProviderError", report));
    }
  }



  /// <summary>Ids of runs currently executing in THIS runtime — the in-process owner
  ///     set for exact orphan repair (FR-L8). Queued (not yet started) ids are NOT
  ///     included: they have never run, so a session restart legitimately orphans them.</summary>
  public IReadOnlyCollection<Guid> ActiveChildren
      => [.. _active.Keys.Select(id => id.Value)];

  public void Interrupt(AgentId? childId = null)
  {
    if (childId is { } id)
    {
      InterruptOne(id);
      return;
    }

    // Snapshot: InterruptOne mutates the active set as it goes.
    foreach (AgentId runningId in _active.Keys.ToArray())
    {
      InterruptOne(runningId);
    }
  }

  /// <summary>Interrupts one owned id. A QUEUED spawn (parked, never started) is removed
  ///     from the queue and TERMINALIZED — Failed(Interrupted) persisted, its waiters
  ///     settled with that outcome, its mailbox closed — instead of merely vanishing from
  ///     the queue list (issue #238: the old RemoveAll left a Running row no caller could
  ///     ever retrieve, so a blanket interrupt silently destroyed parked spawns). A
  ///     RUNNING spawn keeps the legacy path: the CTS is cancelled and the run's own
  ///     finally persists the outcome.</summary>
  private void InterruptOne(AgentId id)
  {
    AgentRecord? queuedRecord = null;
    lock (_admissionGate)
    {
      int index = _waiters.FindIndex(w => w.Record.Id == id);
      if (index >= 0)
      {
        queuedRecord = _waiters[index].Record;
        _waiters.RemoveAt(index);
      }
    }

    // Named decision (CA2000): the CTS is cancelled here; disposal belongs to the
    // run's finally, which owns its lifetime.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    bool owned = _active.TryRemove(id, out CancellationTokenSource? cts);
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
    if (owned)
    {
      cts!.Cancel();
    }

    if (queuedRecord is { } record)
    {
      // A parked spawn holds no slot, so nothing is released here; the run never began.
      _ = Task.Run(() => TerminalizeQueuedAsync(record), CancellationToken.None);
    }
  }

  /// <summary>Terminalizes a parked spawn that will never run: the record becomes
  ///     Failed(Interrupted), the mailbox closes, and every waiter settles with that
  ///     well-formed outcome. Runs off the caller's thread (Interrupt is synchronous by
  ///     contract) and never throws.</summary>
  private async Task TerminalizeQueuedAsync(AgentRecord record)
  {
    const string report = "Error [Interrupted]: the queued spawn was interrupted before its run began.";
    try
    {
      await TerminalUpdateAsync(record, AgentStatus.Failed, AgentFailureReason.Interrupted, report)
          .ConfigureAwait(false);
    }
    // Named decision (CA1031): the queue-drop path must still settle its waiters when the
    // terminal write faults - an unsettled waiter is the silent loss this fix removes.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // Swallowed deliberately: see the named decision above.
    }

    AgentRunOutcome outcome = new(record.Id, AgentStatus.Failed, AgentFailureReason.Interrupted,
        report, record.ModelUsed, record.Depth);
    CloseMailbox(record.Id, outcome);
    _ = Settle(record.Id, outcome);
  }

  private async Task RunToCompletionAsync(AgentRecord record, CancellationTokenSource cts)
  {
    try
    {
      AgentRunOutcome outcome = await _runner.RunAsync(record, cts.Token).ConfigureAwait(false);
      // Named decision (S8949): CancellationToken.None — Interrupt() cancels cts while
      // the run is settling; the terminal-outcome write must not itself be cancellable,
      // or the record would stay 'running' with no retrievable outcome.
      await TerminalUpdateAsync(record, outcome.Status, outcome.Reason, outcome.Report).ConfigureAwait(false);
      CloseMailbox(record.Id, outcome);
      _ = Settle(record.Id, outcome);
    }
    // Named decision (CA1031): the runtime is an actor boundary - ANY runner fault must
    // become a well-formed Failed outcome for agent.result retrieval, never a crash.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (TranscriptPersistException ex)
    {
      // Honest classification: the RUN succeeded or failed normally, but its terminal
      // record could not be persisted. ProviderError with the persist-fault text keeps
      // the well-formed outcome contract while never reporting 'A task was canceled.'
      // for a persistence problem.
      await TerminalUpdateAsync(record, AgentStatus.Failed, AgentFailureReason.ProviderError,
          "Error [ProviderError]: failed to persist child transcript: " + ex.Message).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      // Runner faults are terminal child outcomes, not crashes: persist them so the parent
      // can retrieve a well-formed failure via agent.result.
      // Named decision (S8949): CancellationToken.None — same contract as the success
      // path above; the failure record must land even if cts was cancelled.
      await TerminalUpdateAsync(record, AgentStatus.Failed, AgentFailureReason.ProviderError,
          "Error [ProviderError]: " + ex.Message).ConfigureAwait(false);
      CloseMailbox(record.Id, new AgentRunOutcome(record.Id, AgentStatus.Failed, AgentFailureReason.ProviderError,
          "Error [ProviderError]: " + ex.Message, record.ModelUsed, record.Depth));
      _ = Settle(record.Id, new AgentRunOutcome(record.Id, AgentStatus.Failed, AgentFailureReason.ProviderError,
          "Error [ProviderError]: " + ex.Message, record.ModelUsed, record.Depth));
    }
    finally
    {
      if (_active.TryRemove(record.Id, out CancellationTokenSource? removed))
      {
        removed.Dispose();
      }

      // The slot goes to the queue head inside the admission gate, never back into a
      // general pool a fresh Start could win (issue #238: that race starved parked
      // spawns and let the runtime briefly run over cap).
      ReleaseSlotAndAdmitNext();
    }
  }

  /// <inheritdoc cref="IAgentRuntime.WhenSettledAsync"/>
  public async Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
  {
    if (!_settling.TryGetValue(id, out TaskCompletionSource<AgentRunOutcome>? source))
    {
      return Result.Failure<AgentRunOutcome>(
          new DomainError("NotFound", $"agent '{id}' has no live or settled run owned by this runtime."));
    }

    Task<AgentRunOutcome> settled = source.Task;
    try
    {
      AgentRunOutcome outcome = ct.CanBeCanceled
          ? await settled.WaitAsync(ct).ConfigureAwait(false)
          : await settled.ConfigureAwait(false);
      return Result.Success(outcome);
    }
    catch (OperationCanceledException)
    {
      // The WAITER's token fired, not the child: the child's outcome stays available to
      // other (and later) waiters because the TCS is untouched.
      return Result.Failure<AgentRunOutcome>(new DomainError("Cancelled", "the wait was cancelled."));
    }
  }

  /// <inheritdoc cref="IAgentRuntime.Resume"/>
  public async Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(message))
    {
      return Result.Failure<AgentId>(new DomainError("InvalidMessage",
          "the resume message must be a non-empty string."));
    }

    if (_active.ContainsKey(id))
    {
      return Result.Failure<AgentId>(new DomainError("NotRunning",
          $"agent '{id}' is still running — steer it with agent.send, not agent.resume."));
    }

    Result<AgentRecord> loaded = await _store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return Result.Failure<AgentId>(new DomainError("NotRunning",
          $"agent '{id}' is not a settled child known to this runtime."));
    }

    AgentRecord record = loaded.Value;
    if (record.Status is AgentStatus.Running)
    {
      return Result.Failure<AgentId>(new DomainError("NotRunning",
          $"agent '{id}' is still running — steer it with agent.send, not agent.resume."));
    }

    SpawnContract contract = record.Contract is { } json ? SpawnContract.Decode(json) : new SpawnContract();
    AgentRecord resumed = record with
    {
      Status = AgentStatus.Running,
      FailureReason = null,
      CompletedAt = null,
      FinalReport = null,
      Phase = ChildPhase.ModelCall,
      Attempts = record.Attempts + 1,
      Contract = SpawnContract.Encode(contract.WithResumeMessage(message)),
    };
    Result<string> updated = await _store.UpdateAsync(resumed, ct).ConfigureAwait(false);
    return updated.IsSuccess ? await Start(resumed, ct).ConfigureAwait(false) : Result.Failure<AgentId>(updated.Error);
  }

  /// <summary>The single terminal-persist path: writes the terminal columns AND clears
  ///     the one-shot resume carrier — the run consumed it, so a watchdog retry of this
  ///     child reverts to the wrap-up nudge and the same resume can never re-deliver.
  ///     <para>A CORRUPT contract column never blocks the terminal write: an undecodable
  ///     agreement leaves the column untouched (there is no carrier to clear in a payload
  ///     nobody can read) instead of throwing out of the one path every settle depends on
  ///     — which is exactly how an admission fault used to strand its own record.</para></summary>
  private async Task TerminalUpdateAsync(AgentRecord record, AgentStatus status,
      AgentFailureReason? reason, string? report)
  {
    string? contractJson = record.Contract;
    if (contractJson is not null && TryClearResumeCarrier(contractJson) is { } cleared)
    {
      contractJson = cleared;
    }

    _ = await _store.UpdateAsync(record with
    {
      Status = status,
      FailureReason = reason,
      CompletedAt = DateTimeOffset.UtcNow,
      FinalReport = report,
      Contract = contractJson,
    }, CancellationToken.None).ConfigureAwait(false);
  }

  /// <summary>The cleared contract payload, the original payload when it carries no
  ///     resume carrier, or null when the column cannot be decoded at all (the caller
  ///     then leaves it byte-identical).</summary>
  private static string? TryClearResumeCarrier(string contractJson)
  {
    try
    {
      SpawnContract decoded = SpawnContract.Decode(contractJson);
      return decoded.ResumeMessage is null ? contractJson : SpawnContract.Encode(decoded.WithoutResumeMessage());
    }
    // Named decision (CA1031): the terminal write is the one path every settle depends
    // on; an unreadable contract is a data fault, never a reason to lose the outcome.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return null;
    }
  }

  /// <summary>Completes every waiter for one child. Called after the terminal record write
  ///     so waiters always observe persisted state.</summary>
  private bool Settle(AgentId id, AgentRunOutcome outcome)
  {
    while (true)
    {
      if (_settling.TryGetValue(id, out TaskCompletionSource<AgentRunOutcome>? source))
      {
        if (source.TrySetResult(outcome))
        {
          return true;
        }

        // Stale completed source from an older run: late waiters must observe the
        // LATEST outcome, so swap in a fresh already-completed source and retry on
        // add/update races.
        TaskCompletionSource<AgentRunOutcome> replacement = NewSettleSource();
        _ = replacement.TrySetResult(outcome);
        if (_settling.TryUpdate(id, replacement, source))
        {
          return true;
        }
      }
      else
      {
        // Settle for an id this runtime never started (defensive): record the outcome
        // so a late waiter still reads a well-formed result instead of NotFound.
        TaskCompletionSource<AgentRunOutcome> fresh = NewSettleSource();
        _ = fresh.TrySetResult(outcome);
        return _settling.TryAdd(id, fresh);
      }
    }
  }

  private static TaskCompletionSource<AgentRunOutcome> NewSettleSource()
      => new(TaskCreationOptions.RunContinuationsAsynchronously);
  /// <inheritdoc cref="IAgentRuntime.InterruptSubtree"/>
  public void InterruptSubtree(AgentId rootOfSubtree)
  {
    // Only ids this runtime actively owns can be cancelled; edges come from the store.
    Dictionary<Guid, Guid?> edges = [];
    List<AgentId> deepestFirst = [];
    foreach (AgentId activeId in _active.Keys)
    {
      Result<AgentRecord> record = _store.GetAsync(activeId).GetAwaiter().GetResult();
      if (record.IsSuccess)
      {
        edges[activeId.Value] = record.Value.ParentId?.Value;
      }
    }

    bool IsDescendantOf(Guid candidate, Guid root)
    {
      Guid? cursor = candidate;
      while (cursor is { } current)
      {
        if (current == root)
        {
          return true;
        }

        cursor = edges.GetValueOrDefault(current);
      }

      return false;
    }

    foreach (AgentId activeId in _active.Keys)
    {
      if (activeId.Value == rootOfSubtree.Value || IsDescendantOf(activeId.Value, rootOfSubtree.Value))
      {
        deepestFirst.Add(activeId);
      }
    }

    // Depth order: deepest (longest parent chain among active ids) first.
    deepestFirst.Sort((a, b) => DepthOf(b, edges).CompareTo(DepthOf(a, edges)));
    foreach (AgentId id in deepestFirst)
    {
      Interrupt(id);
    }
  }

  private static int DepthOf(AgentId id, Dictionary<Guid, Guid?> edges)
  {
    int depth = 0;
    Guid? cursor = id.Value;
    while (cursor is { } current && edges.TryGetValue(current, out Guid? parent) && parent is not null)
    {
      depth++;
      cursor = parent;
    }

    return depth;
  }
  /// <summary>The mailbox for an id, or null when the runtime owns none (never started
  ///     or already retired). The Agent loop obtains its inbox through the runner's
  ///     SubAgentServices; senders go through the runtime's Deliver.</summary>
  /// <summary>The owning loop's inbox via the shared registry, or null when this
  ///     runtime owns no live run for the id (never started, settled, or foreign).</summary>
  public IAgentInbox? InboxFor(AgentId id) => _registry?.InboxFor(id);


  /// <summary>Push-delivers into a child's mailbox. Fails NotRunning when the runtime
  ///     owns no live mailbox for the id (FR-C2's NotRunning contract).</summary>
  public Result<bool> Deliver(AgentId id, PendingMessage message)
  {
    ArgumentNullException.ThrowIfNull(message);
    if (!_mailboxes.TryGetValue(id, out BoundedAgentMailbox? mailbox))
    {
      return Result.Failure<bool>(new DomainError(MailboxErrors.NotRunning,
          $"agent '{id}' is not running; the message was refused."));
    }

    Result<bool> delivered = mailbox.Deliver(message);
    if (delivered.IsSuccess)
    {
      _events?.Publish(new MessageDeliveredEvent(id, DateTimeOffset.UtcNow, "inbound",
          (int)message.Urgency, System.Text.Encoding.UTF8.GetByteCount(message.Text)));

      // Urgent + sender-granted + receiver running => audited preemption (approved D1).
      // The interrupted turn repairs via the cancelled-turn path; the urgent message
      // drains at the repaired turn's start.
      bool senderGranted = message.Sender.StartsWith("agent:", StringComparison.Ordinal)
          && Guid.TryParse(message.Sender[6..], out Guid senderId)
          && _preemptGrants.TryGetValue(senderId, out bool granted)
          && granted;
      if (PreemptionPolicy.Decide(senderGranted, message.Urgency, receiverRunning: true)
          is PreemptionDecision.PreemptGranted)
      {
        _events?.Publish(new PreemptedEvent(id, DateTimeOffset.UtcNow, message.Sender,
            (int)message.Urgency));
        Interrupt(id);
      }
    }

    return delivered;
  }

  /// <summary>Rehydrates persisted undelivered messages at start (FR-C5); fires on a
  ///     background flow — the loop's first drain races benignly with enqueue.
  ///     Errors are swallowed by design: rehydration is best-effort recovery, and the
  ///     messages remain persisted for the next start.</summary>
  private async Task RehydrateAsync(AgentId id, BoundedAgentMailbox mailbox)
  {
    if (_mailboxStore is null)
    {
      return;
    }

    Result<IReadOnlyList<PendingMessage>> loaded = await _mailboxStore.LoadUndeliveredAsync(id).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return;
    }

    foreach (PendingMessage message in loaded.Value)
    {
      _ = mailbox.Deliver(message);
    }

    if (loaded.Value.Count > 0)
    {
      _ = await _mailboxStore.ClearAsync(id).ConfigureAwait(false);
    }
  }

  /// <summary>Closes the delivery side and persists any still-undelivered remainder
  ///     (FR-C5) — called exactly once per run, before waiters observe settlement.</summary>
  private void CloseMailbox(AgentId id, AgentRunOutcome outcome)
  {
    if (_mailboxes.TryRemove(id, out BoundedAgentMailbox? mailbox))
    {
      _registry?.Unregister(id);
      _supervisors?.Unregister(id);
      mailbox.Close();
      IReadOnlyList<PendingMessage> remainder = mailbox.Drain();
      if (_mailboxStore is not null && remainder.Count > 0)
      {
        _ = PersistRemainderAsync(id, remainder);
      }

      _events?.Publish(new ChildSettledEvent(id, DateTimeOffset.UtcNow, outcome.Status,
          outcome.Reason, System.Text.Encoding.UTF8.GetByteCount(outcome.Report)));
    }
  }

  private async Task PersistRemainderAsync(AgentId id, IReadOnlyList<PendingMessage> remainder)
  {
    if (_mailboxStore is null)
    {
      return;
    }

    // A failed remainder write is survivable (messages were undelivered steering, not
    // protocol state); the result is intentionally ignored — never crash the settle path.
    _ = await _mailboxStore.PersistUndeliveredAsync(id, remainder).ConfigureAwait(false);
  }
}

#pragma warning restore CA1001 // Types that own disposable fields should be disposable
