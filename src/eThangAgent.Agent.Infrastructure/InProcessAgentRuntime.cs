using System.Collections.Concurrent;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure;

/// <summary>In-process actor runtime: every accepted child runs to completion on one background task
/// while the caller continues. A strict concurrency cap is enforced with a zero-timeout slot wait —
/// at-capacity starts fail with <see cref="RuntimeErrors.CapReached"/> and produce no side effects.
/// Each active run owns a CancellationTokenSource registered here, so <see cref="Interrupt"/> can
/// cancel one or all runs; runners observe the token and persist well-formed terminal outcomes. a CancellationTokenSource registered here, so <see cref="Interrupt"/> can
/// cancel one or all runs; runners observe the token and persist well-formed terminal outcomes.</summary>
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
// Named decision: the runtime is a process-lifetime singleton owned by the composition
// root; disposing the semaphore on teardown adds no value.
public sealed class InProcessAgentRuntime : IAgentRuntime
{
  private readonly IAgentRunner _runner;
  private readonly IAgentStore _store;
  private readonly SemaphoreSlim _slots;
  private readonly ConcurrentDictionary<AgentId, CancellationTokenSource> _active = [];

  // One completion source per child id: WhenSettledAsync awaits it; a watchdog same-id
  // retry REUSES the source so existing waiters survive the retry (FR-L3).
  private readonly ConcurrentDictionary<AgentId, TaskCompletionSource<AgentRunOutcome>> _settling = [];

  // Per-child mailbox registry: Deliver from any sender; Drain by the owner loop at safe
  // points. Between-turn durability rides IMailboxStore (FR-C5).
  private readonly ConcurrentDictionary<AgentId, BoundedAgentMailbox> _mailboxes = [];

  // Per-agent preemption grant (D1): whether THIS agent's contract grants Urgent
  // preemption. Consulted by Deliver when the sender is a granted agent.
  private readonly ConcurrentDictionary<Guid, bool> _preemptGrants = [];
  private readonly IMailboxStore? _mailboxStore;
  private readonly IAgentEvents? _events;
  private readonly ChildSupervisorRegistry? _supervisors;

  public InProcessAgentRuntime(IAgentRunner runner, IAgentStore store, int maxConcurrentAgents,
      IMailboxStore? mailboxStore = null, IAgentEvents? events = null,
      ChildSupervisorRegistry? supervisors = null)
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
    _slots = new SemaphoreSlim(maxConcurrentAgents, maxConcurrentAgents);
  }

  public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    if (!_slots.Wait(0, CancellationToken.None))
    {
      return Task.FromResult(Result.Failure<AgentId>(CapError()));
    }

    // Named decision (CA2000): ownership of the CTS transfers to _active; it is disposed
    // in RunToCompletionAsync's finally when the run settles.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    CancellationTokenSource cts = new();
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
    _active[record.Id] = cts;
    BoundedAgentMailbox mailbox = new();
    _mailboxes[record.Id] = mailbox;
    _ = RehydrateAsync(record.Id, mailbox);
    if (_supervisors is not null)
    {
      ChildSupervisor supervisor = new(record.Id, _events ?? NullAgentEvents.Instance, TimeProvider.System, ceilings: null);
      supervisor.OnStart(record.Attempts);
      _supervisors.Register(record.Id, supervisor);
    }

    _preemptGrants[record.Id.Value] = record.Contract is { } contractJson && SpawnContract.Decode(contractJson).PreemptGrant;
    _ = _settling.AddOrUpdate(record.Id,
        static _ => NewSettleSource(),
        static (_, existing) => existing.Task.IsCompleted ? NewSettleSource() : existing);
    _ = Task.Run(() => RunToCompletionAsync(record, cts), CancellationToken.None);
    return Task.FromResult(Result.Success(record.Id));
  }

  public void Interrupt(AgentId? childId = null)
  {
    if (childId is { } id)
    {
      // Named decision (CA2000): the CTS is cancelled here; disposal belongs to the
      // run's finally, which owns its lifetime.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      if (_active.TryRemove(id, out CancellationTokenSource? cts))
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
      {
        cts.Cancel();
      }

      return;
    }
    foreach (AgentId runningId in _active.Keys)
    {
      Interrupt(runningId);
    }
  }

  private async Task RunToCompletionAsync(AgentRecord record, CancellationTokenSource cts)
  {
    try
    {
      AgentRunOutcome outcome = await _runner.RunAsync(record, cts.Token).ConfigureAwait(false);
      // Named decision (S8949): CancellationToken.None — Interrupt() cancels cts while
      // the run is settling; the terminal-outcome write must not itself be cancellable,
      // or the record would stay 'running' with no retrievable outcome.
      _ = await _store.UpdateAsync(record with
      {
        Status = outcome.Status,
        FailureReason = outcome.Reason,
        CompletedAt = DateTimeOffset.UtcNow,
        FinalReport = outcome.Report,
      }, CancellationToken.None).ConfigureAwait(false);
      CloseMailbox(record.Id, outcome);
      _ = Settle(record.Id, outcome);
    }
    // Named decision (CA1031): the runtime is an actor boundary - ANY runner fault must
    // become a well-formed Failed outcome for agent.result retrieval, never a crash.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Runner faults are terminal child outcomes, not crashes: persist them so the parent
      // can retrieve a well-formed failure via agent.result.
      // Named decision (S8949): CancellationToken.None — same contract as the success
      // path above; the failure record must land even if cts was cancelled.
      _ = await _store.UpdateAsync(record with
      {
        Status = AgentStatus.Failed,
        FailureReason = AgentFailureReason.ProviderError,
        CompletedAt = DateTimeOffset.UtcNow,
        FinalReport = "Error [ProviderError]: " + ex.Message,
      }, CancellationToken.None).ConfigureAwait(false);
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

      _ = _slots.Release();
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
  internal bool TryGetMailbox(AgentId id, out BoundedAgentMailbox? mailbox)
      => _mailboxes.TryGetValue(id, out mailbox);

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
  /// <summary>Translates the canonical CapReached string into an <see cref="DomainError"/> without duplicating
  /// its text: "Error [Code]: message" becomes Error(Code, message), so downstream pass-through rendering
  /// reproduces <see cref="RuntimeErrors.CapReached"/> byte-for-byte.</summary>
  private static DomainError CapError()
  {
    string canonical = RuntimeErrors.CapReached;
    int codeStart = canonical.IndexOf('[', StringComparison.Ordinal) + 1;
    int codeEnd = canonical.IndexOf(']', codeStart);
    return new DomainError(canonical[codeStart..codeEnd], canonical[(codeEnd + 3)..]);
  }
}

#pragma warning restore CA1001 // Types that own disposable fields should be disposable
