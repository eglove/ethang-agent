using System.Collections.Concurrent;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL;

/// <summary>The app-side runtime for out-of-process children (FR-X1/X2/X3): implements the
///     domain's IAgentRuntime by translating actor operations to wire envelopes over the
///     named-pipe transport. Delivery semantics: at-least-once on the wire with host acks —
///     presented to the domain as the same surface the in-process runtime exposes (D11).
///     OwnedChildren records which ids are remote so orphan repair can be exact (FR-L8).</summary>
public sealed class RemoteAgentRuntime : IAgentRuntime
{
  private NamedPipeChildTransport? _transport;

  /// <summary>Creates a DISCONNECTED runtime (composition, remote mode): AttachAsync
  ///     owns connecting, starting the pump, and reading the host's declared set.</summary>
  public RemoteAgentRuntime()
  {
  }

  /// <summary>Creates a runtime over an already-connected transport (tests, replays).</summary>
  public RemoteAgentRuntime(NamedPipeChildTransport? transport)
      => _transport = transport;

  /// <summary>Creates a runtime with an explicit root-turn budget (tests). The budget is
  ///     the upper bound on how long ONE turn may wait for its settle before the caller is
  ///     told the turn was unaddressable — the guard that keeps a silent host from
  ///     stranding a caller forever.</summary>
  public RemoteAgentRuntime(NamedPipeChildTransport? transport, TimeSpan rootTurnBudget)
  {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rootTurnBudget, TimeSpan.Zero);
    _transport = transport;
    RootTurnBudget = rootTurnBudget;
  }

  /// <summary>How long one root turn may wait for its settle. A host that takes a turn and
  ///     never answers it cannot strand the caller: past this bound the turn fails
  ///     <c>TurnUnaddressable</c>. Ten minutes is generous for a real turn and finite for
  ///     a broken one.</summary>
  public TimeSpan RootTurnBudget { get; } = TimeSpan.FromMinutes(10);

  private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentRunOutcome>> _settling = [];
  private readonly ConcurrentDictionary<Guid, byte> _owned = [];

  // Root turns in flight (spec D6): one waiter per turn id, keyed by the id the app
  // minted. The pump routes 'root-turn-event' envelopes here; connection loss and
  // cancellation complete every pending waiter so no caller can ever hang.
  private readonly ConcurrentDictionary<string, RootTurnWaiter> _rootTurns = new(StringComparer.Ordinal);

  /// <summary>Set once the connection is known gone: a turn sent afterwards refuses
  ///     immediately with the declared failure instead of waiting for a host that will
  ///     never answer.</summary>
  private volatile bool _hostLost;
  private Guid[] _declaredLive = [];
  private long _sequence;

  /// <summary>The host's most recently declared live child set (R3.2's exact orphan
  ///     resolution input). Refreshed by the pump on every declare envelope — the
  ///     host sends it on connect and after each start/settle — so a snapshot read
  ///     after re-attach reflects children the host kept running.</summary>
  public IReadOnlyCollection<Guid> DeclaredLiveChildren => _declaredLive;

  /// <summary>Ids this runtime has started remotely (exact ownership for orphan repair).</summary>
  public IReadOnlyCollection<Guid> OwnedChildren
  {
    get
    {
      lock (_owned)
      {
        return [.. _owned.Keys];
      }
    }
  }

  /// <summary>Re-attach (R3.1): swaps in the transport for the app's NEW connection.
  ///     The previous pump has ended (its transport closed with the dead connection);
  ///     the caller starts a fresh receive loop over the new transport. Waiters from
  ///     before the detach keep their sources — a re-emitted settle completes them.</summary>
  public void ReplaceTransport(NamedPipeChildTransport fresh)
  {
    ArgumentNullException.ThrowIfNull(fresh);
    NamedPipeChildTransport? stale = Interlocked.Exchange(ref _transport, fresh);
    // A fresh connection is live again: clear the lost flag so root turns are accepted
    // (the flag exists to refuse turns on a KNOWN-dead connection, not permanently).
    _hostLost = false;
    if (stale is not null)
    {
      _ = Task.Run(async () => await stale.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }
  }

  /// <summary>Starts the host-side receive loop. Call once after connecting; every settle
  ///     envelope completes the matching WhenSettled await; connection loss completes every
  ///     waiter with a declared failure (FR-X3), never a hang.</summary>
  public Task RunReceiveLoopAsync(CancellationToken ct) => PumpAsync(ct);

  /// <inheritdoc cref="IAgentRuntime.Start"/>
  public async Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    TaskCompletionSource<AgentRunOutcome> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    _settling[record.Id.Value] = source;
    lock (_owned)
    {
      _owned[record.Id.Value] = 1;
    }

    StartCommand command = new(record.Id.Value, MaxConcurrent: 8, record.ModelUsed);
    return await SendStartAsync(command, record.Id, ct).ConfigureAwait(false);
  }

  /// <summary>Synchronous-by-contract delivery (the domain seam is sync): the underlying
  ///     wire send blocks only until the host's always-running pump consumes the frame,
  ///     which the pump model guarantees. Named decision: GetAwaiter().GetResult() over
  ///     .Wait() to avoid SyncCtx deadlocks in this ACL.</summary>
  public Result<bool> Deliver(AgentId id, PendingMessage message)
  {
    ArgumentNullException.ThrowIfNull(message);
    DeliverCommand command = new(id.Value, message.Text, (int)message.Urgency, message.Sender);
    bool sent = SendEnvelopeAsync("deliver", JsonSerializer.Serialize(command), id, CancellationToken.None)
        .GetAwaiter().GetResult();
    return sent
        ? Result.Success(true)
        : Result.Failure<bool>(new DomainError("HostUnavailable", "the child host refused the delivery."));
  }

  /// <inheritdoc cref="IAgentRuntime.WhenSettledAsync"/>
  /// <remarks>Remote waits do NOT wake on events (user steering, child events, stream
  ///     stalls, state writes) in this build — wire forwarding is a planned follow-up;
  ///     the wait blocks until settlement or cancellation exactly as before.</remarks>
  public async Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
  {
    if (!_settling.TryGetValue(id.Value, out TaskCompletionSource<AgentRunOutcome>? source))
    {
      return Result.Failure<AgentRunOutcome>(new DomainError("NotFound",
          $"agent '{id}' has no live remote run."));
    }

    try
    {
      AgentRunOutcome outcome = ct.CanBeCanceled
          ? await source.Task.WaitAsync(ct).ConfigureAwait(false)
          : await source.Task.ConfigureAwait(false);
      return Result.Success(outcome);
    }
    catch (OperationCanceledException)
    {
      return Result.Failure<AgentRunOutcome>(new DomainError("Cancelled", "the wait was cancelled."));
    }
  }

  /// <inheritdoc cref="IAgentRuntime.Resume"/>
  public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentId>(new DomainError(
          "ResumeUnsupported",
          "resume-after-settle is not yet wired through the child host (named follow-up); only in-process children can be resumed.")));

  /// <inheritdoc cref="IAgentRuntime.InterruptSubtree"/>
  public void InterruptSubtree(AgentId rootOfSubtree)
      => _ = SendEnvelopeAsync("interrupt", JsonSerializer.Serialize(new InterruptCommand(rootOfSubtree.Value)), rootOfSubtree, CancellationToken.None);

  /// <inheritdoc cref="IAgentRuntime.Interrupt"/>
  public void Interrupt(AgentId? childId = null)
      => _ = SendEnvelopeAsync("interrupt", JsonSerializer.Serialize(new InterruptCommand(childId?.Value)), childId ?? new AgentId(Guid.NewGuid()), CancellationToken.None);

  /// <summary>Sends one root turn to the conversation host and streams its events back
  ///     (spec D6). The app runs no agent work: it forwards the user's input and renders
  ///     what the host streams. The returned Result carries the settled payload when the
  ///     host settles the turn.
  /// <para>EVERY path settles the caller — this method never hangs. The failures are
  /// typed and distinct: <c>InvalidTurnId</c> when the id is empty or already in flight,
  /// <c>HostUnavailable</c> when there is no live connection, <c>TurnCancelled</c> when
  /// the caller cancelled, and <c>TurnUnaddressable</c> when the host took the turn but
  /// never settled it within <see cref="RootTurnBudget"/> (a host that cannot address the
  /// request — e.g. one that dropped an unrecoverable turn id — would otherwise strand the
  /// caller forever).</para></summary>
  public async Task<Result<string>> SendRootTurnAsync(
      RootTurnRequest request, Action<RootTurnEvent> onEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    // The codec refuses an empty turn id on PARSE; the send path must match, or the waiter
    // would be registered under a key the host can never address (review finding 1).
    if (string.IsNullOrWhiteSpace(request.TurnId))
    {
      return Result.Failure<string>(new DomainError("InvalidTurnId",
          "a root turn needs a non-empty turn id."));
    }

    NamedPipeChildTransport? transport = _transport;
    if (transport is null || _hostLost)
    {
      return HostUnavailable("the conversation host is not connected.");
    }

    TaskCompletionSource<Result<string>> settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // TryAdd, never indexer assignment: two turns sharing a TurnId would otherwise OVERWRITE
    // the first waiter, which then hangs forever while the second completes, and the
    // finally would remove the second's entry so a late settle for the first is dropped too
    // (review finding 1). A collision is refused instead of silently orphaning a caller.
    if (!_rootTurns.TryAdd(request.TurnId, new RootTurnWaiter(onEvent, settled)))
    {
      return Result.Failure<string>(new DomainError("InvalidTurnId",
          "a root turn with id '" + request.TurnId + "' is already in flight."));
    }

    try
    {
      await transport.SendAsync(new TransportEnvelope("root-turn",
          RootTurnProtocol.SerializeRequest(request), Interlocked.Increment(ref _sequence)), ct)
          .ConfigureAwait(false);
      return await settled.Task.WaitAsync(RootTurnBudget, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      return Result.Failure<string>(new DomainError("TurnCancelled", "the turn was stopped."));
    }
    catch (TimeoutException)
    {
      // The host accepted the frame but never settled the turn: the request was
      // unaddressable on its side. Bounded, typed, never a hang (review finding 2).
      return Result.Failure<string>(new DomainError("TurnUnaddressable",
          "the conversation host did not settle the turn within " + RootTurnBudget + "."));
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): the send path is the transport's fault boundary — ANY
      // failure to put the frame on the wire is the declared HostUnavailable outcome.
      return HostUnavailable("the root turn could not be put on the wire: " + ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      _ = _rootTurns.TryRemove(request.TurnId, out _);
    }
  }

  private static Result<string> HostUnavailable(string message)
      => Result.Failure<string>(new DomainError("HostUnavailable", message));

  /// <summary>Routes one streamed root-turn event to its waiter: every event reaches the
  ///     caller's callback in arrival order, and a 'settled' event completes the turn with
  ///     its payload. Events for a turn this runtime is not running (a replay after a
  ///     disconnect) are dropped — there is no waiter to inform.</summary>
  private void HandleRootTurnEvent(string json)
  {
    RootTurnEvent evt = RootTurnProtocol.ParseEvent(json);
    if (!_rootTurns.TryGetValue(evt.TurnId, out RootTurnWaiter? waiter))
    {
      return;
    }

    // The callback is USER-supplied code running on the pump thread: a throw must not
    // fault the pump and take every other child's connection down with it (review
    // minor c). The event is still routed and the turn still settles.
    try
    {
      waiter.OnEvent(evt);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): a caller's callback fault is the caller's problem, not
      // the wire's — it is contained here so the connection survives.
    }
#pragma warning restore CA1031 // Do not catch general exception types
    if (string.Equals(evt.Kind, "settled", StringComparison.Ordinal))
    {
      _ = waiter.Settled.TrySetResult(Result.Success(evt.Payload));
    }
  }

  /// <summary>Completes every pending root-turn waiter with the declared failure. Called
  ///     from the pump's fault path: connection loss must never leave a caller waiting.</summary>
  private void FailPendingRootTurns()
  {
    _hostLost = true;
    foreach (RootTurnWaiter waiter in _rootTurns.Values)
    {
      _ = waiter.Settled.TrySetResult(HostUnavailable("the conversation host connection was lost."));
    }

    _rootTurns.Clear();
  }

  private async Task PumpAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        NamedPipeChildTransport transport = _transport ?? throw new TransportClosedException("the runtime is not attached to a host pipe.");
        TransportEnvelope envelope = await transport.ReceiveAsync(ct).ConfigureAwait(false);
        DispatchEnvelope(envelope);

        await _transport.SendAsync(new TransportEnvelope("ack", "\"" + envelope.Sequence + "\"", envelope.Sequence), ct).ConfigureAwait(false);
      }
    }
    // Named decision (CA1031): the pump is the connection's fault boundary. Cancellation
    // ends it normally; any other failure completes every pending waiter with a declared
    // ProviderError — a well-formed outcome, never a hang (FR-X3).
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      foreach (TaskCompletionSource<AgentRunOutcome> source in _settling.Values)
      {
        _ = source.TrySetResult(new AgentRunOutcome(new AgentId(Guid.NewGuid()), AgentStatus.Failed,
            AgentFailureReason.ProviderError, "Error [ProviderError]: child host connection lost.", "remote", 0));
      }

      _settling.Clear();
      // Root turns in flight are the app's user-facing work: they must fail with the
      // typed HostUnavailable outcome, never hang on a host that is gone (spec D6).
      FailPendingRootTurns();
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Routes one received envelope by kind: declare refreshes the live-id
  /// set, error completes one child's waiters with a host fault, settle completes
  /// them with the child's outcome. Unknown kinds are ignored (ack still sent).</summary>
  private void DispatchEnvelope(TransportEnvelope envelope)
  {
    switch (envelope.Kind)
    {
      case "declare":
        HandleDeclare(envelope.Json);
        break;
      case "error":
        HandleHostError(envelope.Json);
        break;
      case "settle":
        HandleSettle(envelope.Json);
        break;
      case "root-turn-event":
        HandleRootTurnEvent(envelope.Json);
        break;
      default:
        break;
    }
  }

  private void HandleDeclare(string json)
  {
    DeclareCommand? declared = JsonSerializer.Deserialize<DeclareCommand>(json);
    if (declared is not null)
    {
      _declaredLive = [.. declared.LiveIds];
    }
  }

  private void HandleHostError(string json)
  {
    // Host-side fault for one child (e.g. record not found, host ceiling): complete
    // that child's waiters with a well-formed failure instead of leaving them hung.
    HostError? fault = JsonSerializer.Deserialize<HostError>(json);
    if (fault is not null && _settling.TryRemove(fault.RecordId, out TaskCompletionSource<AgentRunOutcome>? faulted))
    {
      _ = faulted.TrySetResult(new AgentRunOutcome(new AgentId(fault.RecordId),
          AgentStatus.Failed, AgentFailureReason.ProviderError,
          "Error [HostFault]: " + fault.Message, "remote", 0));
    }
  }

  private void HandleSettle(string json)
  {
    SettleNotice? notice = JsonSerializer.Deserialize<SettleNotice>(json);
    if (notice is not null)
    {
      AgentStatus status = Enum.TryParse(notice.Status, out AgentStatus parsed)
          ? parsed : AgentStatus.Failed;
      AgentFailureReason? reason = Enum.TryParse(notice.Reason, out AgentFailureReason reasonParsed)
          ? reasonParsed : null;
      Settle(new AgentId(notice.RecordId), new AgentRunOutcome(new AgentId(notice.RecordId),
          status, reason, notice.Report, "remote", 0));
    }
  }

  /// <summary>Completes every waiter for one child, RETAINING the completed source as
  ///     an outcome record — the same contract the in-process runtime's Settle upholds
  ///     (T3 ruling): a run may settle more than once (the host watchdog's wrap-up
  ///     retry settles the interrupted attempt before the FINAL terminal settle), and
  ///     a waiter observing the run at any point must read a well-formed outcome —
  ///     never NotFound, never a silently dropped envelope. The FIRST settle is the
  ///     interrupted attempt's outcome; later settles REPLACE it so late waiters see
  ///     the latest outcome.</summary>
  private void Settle(AgentId id, AgentRunOutcome outcome)
  {
    while (true)
    {
      if (_settling.TryGetValue(id.Value, out TaskCompletionSource<AgentRunOutcome>? source))
      {
        if (source.TrySetResult(outcome))
        {
          return;
        }

        // Stale completed source from an older run: late waiters must observe the
        // LATEST outcome, so swap in a fresh already-completed source and retry on
        // add/update races.
        TaskCompletionSource<AgentRunOutcome> replacement = NewSettleSource();
        _ = replacement.TrySetResult(outcome);
        if (_settling.TryUpdate(id.Value, replacement, source))
        {
          return;
        }
      }
      else
      {
        // Settle for an id this runtime never started (defensive, e.g. re-attach
        // delivers a settle for a child started by a previous app lifetime): record
        // the outcome so a late waiter reads a well-formed result instead of NotFound.
        TaskCompletionSource<AgentRunOutcome> fresh = NewSettleSource();
        _ = fresh.TrySetResult(outcome);
        _ = _settling.TryAdd(id.Value, fresh);
        return;
      }
    }
  }

  private static TaskCompletionSource<AgentRunOutcome> NewSettleSource()
      => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private async Task<Result<AgentId>> SendStartAsync(StartCommand command, AgentId id, CancellationToken ct)
  {
    return await SendEnvelopeAsync("start", JsonSerializer.Serialize(command), id, ct).ConfigureAwait(false)
        ? Result.Success(id)
        : Result.Failure<AgentId>(new DomainError("HostUnavailable", "the child host refused the start."));
  }

  private async Task<bool> SendEnvelopeAsync(string kind, string json, AgentId id, CancellationToken ct)
  {
    long sequence = Interlocked.Increment(ref _sequence);
    try
    {
      NamedPipeChildTransport transport = _transport ?? throw new TransportClosedException("the runtime is not attached to a host pipe.");
      await transport.SendAsync(new TransportEnvelope(kind, json, sequence), ct).ConfigureAwait(false);
      return true;
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): a failed wire send is a declared HostUnavailable outcome.
      lock (_owned)
      {
        _ = _owned.TryRemove(id.Value, out _);
      }

      _ = _settling.TryRemove(id.Value, out _);
      return false;
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }
}

/// <summary>One in-flight root turn: the caller's event sink plus the source that
///     completes when the host settles the turn.</summary>
internal sealed record RootTurnWaiter(Action<RootTurnEvent> OnEvent, TaskCompletionSource<Result<string>> Settled);

public sealed record StartCommand(Guid RecordId, int MaxConcurrent, string ModelId);
public sealed record DeliverCommand(Guid RecordId, string Text, int Urgency, string Sender);
public sealed record InterruptCommand(Guid? RecordId);
public sealed record SettleNotice(Guid RecordId, string Status, string? Reason, string Report);
public sealed record DeclareCommand(IReadOnlyCollection<Guid> LiveIds);
public sealed record HostError(Guid RecordId, string Code, string Message);
