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

  /// <summary>Creates a runtime with an explicit root-turn budget (tests, composition).</summary>
  public RemoteAgentRuntime(NamedPipeChildTransport? transport, RootTurnBudget rootTurnBudget)
      : this(transport, rootTurnBudget, events: null)
  {
  }

  /// <summary>Creates a runtime with an explicit root-turn budget and the event stream
  ///     forwarded <c>child-event</c> envelopes republish onto (issue #245). Null keeps
  ///     the legacy shape: forwarded events are dropped, byte-identical to before.</summary>
  public RemoteAgentRuntime(NamedPipeChildTransport? transport, RootTurnBudget rootTurnBudget,
      IAgentEvents? events)
  {
    ArgumentNullException.ThrowIfNull(rootTurnBudget);
    _transport = transport;
    TurnBudget = rootTurnBudget;
    _events = events;
  }

  /// <summary>The root-turn budget this runtime enforces.</summary>
  public RootTurnBudget TurnBudget { get; } = RootTurnBudget.Default;

  private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentRunOutcome>> _settling = [];
  private readonly ConcurrentDictionary<Guid, byte> _owned = [];

  // The session's event stream (issue #245): forwarded child-event envelopes republish
  // here, so the app's panel/badge/wake adapters observe remote children. Null (tests,
  // legacy wiring) drops them - the legacy shape is byte-identical.
  private readonly IAgentEvents? _events;

  // Root turns in flight (spec D6): one waiter per turn id, keyed by the id the app
  // minted. The pump routes 'root-turn-event' envelopes here; connection loss and
  // cancellation complete every pending waiter so no caller can ever hang.
  private readonly ConcurrentDictionary<string, RootTurnWaiter> _rootTurns = new(StringComparer.Ordinal);

  /// <summary>Set once the connection is known gone: a turn sent afterwards refuses
  ///     immediately with the declared failure instead of waiting for a host that will
  ///     never answer.</summary>
  private volatile bool _hostLost;

  /// <summary>True when this runtime believes its connection is gone (a pump fault or a
  ///     declared connection loss). Exposed so a host can decide whether to re-attach, and so
  ///     tests can assert that a recoverable failure did NOT tear the connection down.</summary>
  public bool HostLost => _hostLost;

  private Guid[] _declaredLive = [];
  private long _sequence;
  private long _turnGeneration;

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
    // The domain contract (IAgentRuntime.Deliver): an id this runtime does not OWN fails
    // NotRunning. Ownership is what makes the capability provider's cross-container
    // fallback reachable - a wire send for a foreign id would otherwise claim delivery
    // and the process-wide locator would never be consulted (the in-process runtime
    // answers exactly this way for an id it holds no mailbox for).
    bool owned;
    lock (_owned)
    {
      owned = _owned.ContainsKey(id.Value);
    }

    if (!owned)
    {
      return Result.Failure<bool>(new DomainError(MailboxErrors.NotRunning,
          $"agent '{id}' is not running in this container; the message was refused."));
    }

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

  /// <summary>Interrupts ONE live root turn by its turn id. A root turn has no record id,
  ///     so the child interrupt paths cannot address it: without this a stop abandoned only
  ///     the caller's local wait while the host kept executing — and persisting — the turn
  ///     the user stopped.</summary>
  public void InterruptRootTurn(string turnId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
    _ = SendEnvelopeAsync("interrupt",
        JsonSerializer.Serialize(new InterruptCommand(null, turnId)),
        new AgentId(Guid.NewGuid()), CancellationToken.None);
  }

  /// <summary>Answers one session-request (issue #234): the app-side session the request
  ///     produced, or the typed failure the app refused with. Fire-and-forget by contract —
  ///     the requesting tool awaits its response through its own channel, and a lost answer
  ///     surfaces there as its timeout, never here.</summary>
  public void SendSessionResponse(string requestId, string? sessionId, string? errorCode, string? errorMessage)
      => _ = SendEnvelopeAsync("session-response",
          JsonSerializer.Serialize(new SessionResponseCommand(requestId, sessionId, errorCode, errorMessage)),
          new AgentId(Guid.NewGuid()), CancellationToken.None);

  /// <inheritdoc cref="IAgentRuntime.Interrupt"/>
  public void Interrupt(AgentId? childId = null)
      => _ = SendEnvelopeAsync("interrupt", JsonSerializer.Serialize(new InterruptCommand(childId?.Value)), childId ?? new AgentId(Guid.NewGuid()), CancellationToken.None);

  /// <summary>Sends one root turn to the conversation host and streams its events back
  ///     (spec D6). The app runs no agent work: it forwards the user's input and renders
  ///     what the host streams. The returned Result carries the settled payload when the
  ///     host settles the turn.
  /// <para>EVERY path settles the caller. The failures are typed and distinct:
  /// <c>InvalidTurnId</c> when the id is empty or already in flight, <c>HostUnavailable</c>
  /// when there is no live connection or the frame cannot be put on the wire,
  /// <c>TurnCancelled</c> when the caller cancelled, and <c>TurnUnaddressable</c> when the
  /// host goes SILENT for <see cref="RootTurnBudget.Idle"/>.</para>
  /// <para>Two bounds, and deliberately no third. A healthy turn may run for many minutes
  /// while streaming constantly — the idle bound is what protects it, because it resets on
  /// every event; putting one frame on the wire is a separate question bounded by
  /// <see cref="RootTurnBudget.Send"/>. There is NO total-duration ceiling (issue #248): it
  /// killed legitimate long turns with nothing to resume, so a turn now runs until the host
  /// settles it or the caller stops it.</para>
  /// <para>A bounded write is CANCELLED, and a cancellation that interrupts a frame
  /// desynchronizes the peer's frame stream permanently — so that path closes the connection
  /// and completes every pending settle with a declared failure rather than leaving a
  /// silently dead connection that still reports itself alive.</para></summary>
  public async Task<Result<string>> SendRootTurnAsync(
      RootTurnRequest request, Action<RootTurnEvent> onEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    // The codec refuses an empty turn id on PARSE; the send path must match, or the waiter
    // would be registered under a key the host can never address.
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
    // the first waiter, which then hangs forever while the second completes, and the finally
    // would remove the second's entry so a late settle for the first is dropped too.
    // The CLIENT owns turn identity: mint a fresh generation per turn. A caller-supplied
    // value cannot be trusted for this — two turns on a reused id would both carry the
    // default 0 and a stale settle would match the live turn (ruling 2).
    long generation = Interlocked.Increment(ref _turnGeneration);
    RootTurnWaiter waiter = new(request.TurnId, generation, onEvent, settled);
    if (!_rootTurns.TryAdd(request.TurnId, waiter))
    {
      return Result.Failure<string>(new DomainError("InvalidTurnId",
          "a root turn with id '" + request.TurnId + "' is already in flight."));
    }

    try
    {
      // The SEND is bounded too: a peer that accepts and never reads blocks the write
      // forever, so putting the frame on the wire must not be the one unbounded step.
      //
      // The bound is TurnBudget.Send, NOT the idle window: a slow-but-healthy host may take
      // a long time to absorb a large frame, and since a lapsed write is CANCELLED — and a
      // mid-frame cancellation costs the whole connection — judging the write by the silence
      // window would drop a healthy connection for a frame it was still digesting.
      //
      // The token is passed to SendAsync. A named-pipe write DOES observe its token:
      // cancelling it settles the write Canceled and releases the transport's single write
      // gate. (An earlier version claimed a blocked pipe write "cannot be cancelled by ANY
      // token" and tore the whole connection down on that false premise.)
      using CancellationTokenSource sendTick = CancellationTokenSource.CreateLinkedTokenSource(ct);
      sendTick.CancelAfter(TurnBudget.Send);
      // The idle window rides the request: the host heartbeats inside it, so the window
      // measures LIVENESS instead of busyness (2026-10-22). Without it a long tool call
      // (an exec script, a subagent wait) produced no events at all, the app read the
      // silence as a dead host, and failed a perfectly healthy turn.
      Task send = transport.SendAsync(new TransportEnvelope("root-turn",
          RootTurnProtocol.SerializeRequest(request with
          {
            Generation = generation,
            IdleTimeout = TurnBudget.Idle,
          }),
          Interlocked.Increment(ref _sequence)), sendTick.Token);
      try
      {
        await send.ConfigureAwait(false);
      }
      catch (TransportFrameInterruptedException)
      {
        // CONTROLLER RULING, both halves. The write was CANCELLED (so the transport's single
        // write gate is released — that half is why round 4 was right), but the cancellation
        // landed AFTER the frame header reached the wire, so the peer's frame stream is
        // permanently desynchronized: it will never parse another frame, and every later
        // frame written here is swallowed as this frame's payload. The connection is dead
        // while HostLost would otherwise still report it alive.
        //
        // So: tear it down and complete every pending settle with a declared failure. A
        // caller cancellation still reports TurnCancelled (it is the caller's doing), but
        // the connection loss is real either way.
        // SUPERSEDED-CONNECTION GUARD (mirrors the pump's check): this send was writing on
        // `transport`; if a newer transport has been installed since, the failure belongs to a
        // connection nobody uses any more, and acting on it would clobber the fresh
        // connection's live state - the same re-attach race the pump guard closes, which this
        // path reintroduced (measured 8/40 iterations tearing down a healthy re-attached host).
        if (ReferenceEquals(Volatile.Read(ref _transport), transport))
        {
          MarkConnectionLost("a write was cancelled mid-frame; the peer's frame stream is "
              + "desynchronized and the connection cannot be reused.");
        }
        ct.ThrowIfCancellationRequested();
        return HostUnavailable("the root turn could not be put on the wire within "
            + TurnBudget.Send + ": the host did not consume the frame in time and the "
            + "interrupted write desynchronized the connection, which was closed; "
            + "re-attach to continue.");
      }
      catch (OperationCanceledException)
      {
        // Cancelled BEFORE any byte of the frame reached the wire (the header write is the
        // only thing that can be interrupted without desynchronizing): the stream is intact
        // and the gate is free, so this is a turn-level failure only.
        //
        // The diagnosis must be TRUE of both peers this fires for. A peer that reads nothing
        // and a peer that reads too slowly to finish the frame are the same failure from
        // here — this side cannot tell them apart, and it must not claim the peer "stopped
        // consuming frames" when it is consuming them, just not fast enough. What IS true:
        // the frame was not consumed inside the bound. The connection is still usable, and
        // the message says so, because the caller's next move differs.
        ct.ThrowIfCancellationRequested();
        return HostUnavailable("the root turn could not be put on the wire within "
            + TurnBudget.Send + ": the host did not consume the frame in time. The "
            + "connection is still usable.");
      }
      Result<string> outcome = await AwaitSettleAsync(waiter, ct).ConfigureAwait(false);
      if (!outcome.IsSuccess && IsAbandonment(outcome.Error))
      {
        // THE APP GAVE UP ON A TURN THE HOST IS STILL RUNNING (2026-10-22). The host
        // holds its turn gate for the whole turn, so an abandoned turn blocks every
        // LATER turn behind it - each one queued, silent, and failed the same way. That
        // is what made the session unusable from the first error onward. The app must
        // tell the host to stop the turn it is no longer waiting for.
        InterruptRootTurn(request.TurnId);
      }

      return outcome;
    }
    catch (OperationCanceledException)
    {
      // The caller stopped the turn: the host is still running it, and the same gate
      // holds every later turn. Tell the host to stop (the stop button does this too -
      // a repeated interrupt is a no-op on the host).
      InterruptRootTurn(request.TurnId);
      return Result.Failure<string>(new DomainError("TurnCancelled", "the turn was stopped."));
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

  /// <summary>Waits for one turn's settle under the IDLE bound, which resets on every event
  ///     the waiter routes (see <see cref="HandleRootTurnEvent"/>). There is no total-duration
  ///     bound (issue #248): a turn that keeps proving it is alive runs for as long as it
  ///     needs, and only the caller's own cancellation or the host's settle ends this wait.</summary>
  private async Task<Result<string>> AwaitSettleAsync(RootTurnWaiter waiter, CancellationToken ct)
  {
    while (true)
    {
      // Wait for the settle, for the turn to make progress, or for the window to lapse.
      // The lapse is a TIMER CALLBACK (CancelAfter), not a poll: this ACL owns one bounded
      // wait and does not sweep (R5.1 doctrine — no timed waits outside the allowlist).
      TimeSpan wait = waiter.IdleRemaining(TurnBudget.Idle, DateTimeOffset.UtcNow);
      using CancellationTokenSource tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
      tick.CancelAfter(wait);
      TaskCompletionSource<bool> expired = new(TaskCreationOptions.RunContinuationsAsynchronously);
      using CancellationTokenRegistration registration =
          tick.Token.Register(() => expired.TrySetResult(true));

      Task progress = waiter.WaitForProgressAsync(ct);
      Task<Result<string>> settle = waiter.Settled.Task;
      Task winner = await Task.WhenAny(settle, progress, expired.Task).ConfigureAwait(false);
      ct.ThrowIfCancellationRequested();
      if (winner == settle)
      {
        return await settle.ConfigureAwait(false);
      }

      // Progress or idle expiry: re-derive the clock and loop. A lapsed idle window with
      // no progress since is the unaddressable case.
      if (waiter.IdleRemaining(TurnBudget.Idle, DateTimeOffset.UtcNow) <= TimeSpan.Zero)
      {
        return Result.Failure<string>(new DomainError("TurnUnaddressable",
            "the conversation host went idle for " + TurnBudget.Idle
            + " without answering the turn; the turn was abandoned and the host was asked to stop it, "
            + "so the session can continue with the next message."));
      }
    }
  }
  private static Result<string> HostUnavailable(string message)
      => Result.Failure<string>(new DomainError("HostUnavailable", message));

  /// <summary>True when a failed turn is one the app ABANDONED rather than one the host
  ///     refused: the idle bound ends the app's wait while the host may still be executing
  ///     the turn, and an abandoned turn must be released on the host side (2026-10-22).
  ///     A turn the host SETTLED - even with an error payload - is not an abandonment: the
  ///     host is done with it, and its gate is free.</summary>
  private static bool IsAbandonment(DomainError? error)
      => error is { } failure
          && string.Equals(failure.Code, "TurnUnaddressable", StringComparison.Ordinal);

  /// <summary>Routes one streamed root-turn event to its waiter: every event reaches the
  ///     caller's callback in arrival order and resets the idle clock, and a 'settled' event
  ///     completes the turn with its payload.
  /// <para>Matching is on (turn id, GENERATION), not the id alone. A turn id becomes
  ///     reusable once its predecessor timed out, and the predecessor's settle may still be
  ///     on the wire; delivering it to the live turn would be a wrong-payload SUCCESS — a
  ///     silent lie. A non-matching event is dropped: it belongs to a turn nobody is
  ///     waiting for any more.</para></summary>
  private void HandleRootTurnEvent(string json)
  {
    RootTurnEvent evt = RootTurnProtocol.ParseEvent(json);
    if (!_rootTurns.TryGetValue(evt.TurnId, out RootTurnWaiter? waiter) || !waiter.Matches(evt))
    {
      return;
    }

    waiter.NoteProgress();

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

  /// <summary>Tears the connection down and completes EVERY pending await with a declared
  ///     failure: every root-turn waiter fails <c>HostUnavailable</c>, every in-flight child
  ///     run settles Failed(ProviderError), the transport is dropped so later sends refuse
  ///     at once instead of blocking on a gate nobody will open, and
  ///     <see cref="HostLost"/> becomes true so the state is observable and recoverable by
  ///     <see cref="ReplaceTransport"/>.
  /// <para>Called from two places, both of which have established that nothing on this
  ///     connection can be recovered: the pump's fault path (the peer is gone) and the
  ///     cancelled-mid-frame path in <see cref="SendRootTurnAsync"/> (the peer's frame
  ///     stream is desynchronized, so the connection is silently dead while it still looks
  ///     alive). The reason is carried into every message so a caller reads WHY.</para></summary>
  private void MarkConnectionLost(string reason)
  {
    _hostLost = true;
    foreach (RootTurnWaiter waiter in _rootTurns.Values)
    {
      _ = waiter.Settled.TrySetResult(HostUnavailable(reason));
    }

    _rootTurns.Clear();
    foreach (KeyValuePair<Guid, TaskCompletionSource<AgentRunOutcome>> entry in _settling)
    {
      _ = entry.Value.TrySetResult(new AgentRunOutcome(new AgentId(entry.Key), AgentStatus.Failed,
          AgentFailureReason.ProviderError, "Error [ProviderError]: " + reason, "remote", 0));
    }

    // Drop the transport: a write blocked on the dead connection faults out, releasing the
    // single write gate, and every later send refuses immediately instead of queueing
    // behind it. ReplaceTransport installs a fresh one and clears the lost flag.
    NamedPipeChildTransport? lost = Interlocked.Exchange(ref _transport, null);
    if (lost is not null)
    {
      _ = Task.Run(async () => await lost.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }
  }

  private async Task PumpAsync(CancellationToken ct)
  {
    // The pump serves ONE connection: capture it so a fault can be attributed to the
    // connection that actually failed. Without this a SUPERSEDED pump's fault would clobber
    // the state of the fresh connection ReplaceTransport just installed (the re-attach race:
    // the stale pump set _hostLost after the swap cleared it, so the new connection was
    // refused as "not connected").
    NamedPipeChildTransport? served = _transport;
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
      // SUPERSEDED PUMP: a newer transport was installed while this one was faulting, so
      // this failure belongs to a connection nobody uses any more. Acting on it would
      // clobber the fresh connection's live state — exactly the re-attach race.
      if (!ReferenceEquals(Volatile.Read(ref _transport), served))
      {
        return;
      }

      // Root turns in flight are the app's user-facing work and child runs are the app's
      // in-flight work: both must settle with the declared failure, never hang on a host
      // that is gone (spec D6, FR-X3). One teardown path, so the pump fault and the
      // desynchronized-write path cannot drift apart.
      MarkConnectionLost("the conversation host connection was lost.");
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
      case "child-event":
        HandleChildEvent(envelope.Json);
        break;
      default:
        break;
    }
  }

  private void HandleDeclare(string json)
  {
    DeclareCommand? declared = JsonSerializer.Deserialize<DeclareCommand>(json);
    if (declared is null)
    {
      return;
    }

    Guid[] previous = _declaredLive;
    _declaredLive = [.. declared.LiveIds];

    // The declared set IS ownership (review round 1, finding 1): a child the host kept
    // running across an app restart was never STARTED by this runtime, so seeding
    // ownership from Start alone would refuse steering to a LIVE child - and the
    // cross-container fallback cannot rescue it, because it gates on this same owned set.
    //
    // It is a RECONCILIATION, not an accumulation (review round 2, minor 4): the host
    // re-declares after every start and settle, so an id it stops declaring is no longer
    // live there. Keeping it owned would report a delivery as successful while the host
    // silently dropped it - a lying receipt, worse than the honest NotRunning.
    lock (_owned)
    {
      foreach (Guid live in declared.LiveIds)
      {
        _owned[live] = 1;
      }

      foreach (Guid gone in previous.Where(id => !declared.LiveIds.Contains(id)))
      {
        _ = _owned.TryRemove(gone, out _);
      }
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

  /// <summary>Republishes one forwarded child event onto the session's event stream
  ///     (issue #245): the host serializes the child container's own ChildEvent and this
  ///     side re-publishes it verbatim, so the Desktop's subagent panel, unread badge, and
  ///     wait-wake adapters observe REMOTE children exactly like in-process ones. An
  ///     undecodable payload is dropped (a peer's forward-compatible event kind) - never
  ///     a pump fault. No stream wired: a no-op, byte-identical to the legacy shape.
  ///     <para>The app-side supervisor feed is unaffected: remote children are never
  ///     registered in the app's ChildSupervisorRegistry, so a forwarded progress or
  ///     settled event finds no supervisor and feeds nothing - supervision of remote
  ///     children stays host-side (R3 doctrine).</para></summary>
  private void HandleChildEvent(string json)
  {
    if (_events is null)
    {
      return;
    }

    ChildEventEnvelope? envelope;
    try
    {
      envelope = JsonSerializer.Deserialize<ChildEventEnvelope>(json);
    }
#pragma warning disable CA1031 // Named decision: an undecodable envelope is a peer fault, not a pump crash.
    catch (JsonException)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return;
    }

    if (envelope?.Decode() is not { } evt)
    {
      return;
    }

    _events.Publish(evt);
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
      if (_hostLost)
      {
        // The connection is known dead (a bounded send was abandoned and its write gate is
        // wedged): refuse at once instead of blocking on a gate that will never open.
        throw new TransportClosedException("the runtime's connection is lost; re-attach required.");
      }

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

/// <summary>One in-flight root turn: the caller's event sink, the source that completes when
///     the host settles it, and the two facts the bounds need.
/// <para><see cref="Generation"/> is the per-turn identity (controller ruling 2). A turn id
///     can be reused once its predecessor timed out, and the predecessor's settle may still
///     arrive; events are matched on (id, generation) so a stale settle can never be
///     delivered to the live turn as a wrong-payload SUCCESS — the worst outcome, because it
///     is silent.</para>
/// <para>Progress signalling is what makes the idle bound progress-aware: every routed
///     event resets the clock, so a healthy long turn keeps extending its own deadline.</para></summary>
internal sealed class RootTurnWaiter(
    string turnId, long generation, Action<RootTurnEvent> onEvent,
    TaskCompletionSource<Result<string>> settled)
{
  private readonly Lock _gate = new();
  private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

  public string TurnId { get; } = turnId;
  public long Generation { get; } = generation;
  public Action<RootTurnEvent> OnEvent { get; } = onEvent;
  public TaskCompletionSource<Result<string>> Settled { get; } = settled;

  /// <summary>True when this event belongs to THIS turn: same id AND same generation.</summary>
  public bool Matches(RootTurnEvent evt)
      => string.Equals(evt.TurnId, TurnId, StringComparison.Ordinal) && evt.Generation == Generation;

  /// <summary>Records activity and wakes the wait loop so it re-derives the idle clock.</summary>
  public void NoteProgress()
  {
    TaskCompletionSource next;
    lock (_gate)
    {
      _lastActivity = DateTimeOffset.UtcNow;
      next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _ = _progress.TrySetResult();
      _progress = next;
    }
  }

  /// <summary>How much of the idle window is left, measured from the last routed event.</summary>
  public TimeSpan IdleRemaining(TimeSpan idle, DateTimeOffset now)
  {
    lock (_gate)
    {
      return idle - (now - _lastActivity);
    }
  }

  /// <summary>Completes when the turn makes progress or the wait is cancelled; the caller
  ///     re-derives both clocks afterwards. Deliberately holds no timer of its own — the
  ///     lapse is the caller's CancelAfter tick.</summary>
  public async Task WaitForProgressAsync(CancellationToken ct)
  {
    Task progress;
    lock (_gate)
    {
      progress = _progress.Task;
    }

    await progress.WaitAsync(ct).ConfigureAwait(false);
  }
}

public sealed record StartCommand(Guid RecordId, int MaxConcurrent, string ModelId);
public sealed record DeliverCommand(Guid RecordId, string Text, int Urgency, string Sender);
/// <summary>One interrupt request. <paramref name="RecordId"/> addresses a CHILD subtree
///     (the FR-C6 scoped teardown); <paramref name="TurnId"/> addresses a live ROOT turn,
///     which has no record id of its own. A stop button sends both: the subtree and the
///     root turn the user is actually watching.</summary>
public sealed record InterruptCommand(Guid? RecordId, string? TurnId = null);
public sealed record SettleNotice(Guid RecordId, string Status, string? Reason, string Report);
public sealed record DeclareCommand(IReadOnlyCollection<Guid> LiveIds);
public sealed record HostError(Guid RecordId, string Code, string Message);

/// <summary>The app's answer to one session-request (issue #234): the new session id,
///     or the typed failure (ErrorCode/ErrorMessage) when the launch was refused.</summary>
public sealed record SessionResponseCommand(string RequestId, string? SessionId, string? ErrorCode, string? ErrorMessage);
