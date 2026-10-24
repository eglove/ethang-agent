using System.Collections.Concurrent;
using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.ChildHost;

/// <summary>The host's serve loop: receives envelopes, runs children through the real
///     spawner stack built from a headless session composition, streams events/settlements
///     back, acks app-sent frames (declared at-least-once, FR-X3), and enforces the host's
///     own concurrency ceiling (FR-X5). One connection; host exits when the app disconnects
///     or the pipe closes — children keep running per survivability (FR-L7/T4) and the
///     app re-attaches on restart.</summary>
public sealed class ChildHostServer(string settingsPath, string databasePath)
{
  private volatile NamedPipeChildTransport? _transport;
  private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = [];

  // W3 delivery fix: the delivery target for each running child is the mailbox in the
  // CHILD CONTAINER's own registry — the SAME box the runtime registers at BeginRun and
  // the child loop drains. The server previously kept its own parallel map of boxes the
  // loop never saw, so wire-delivered steering landed in a box nobody drains.
  private readonly ConcurrentDictionary<Guid, ChildMailboxRegistry> _delivery = [];

  // Subtree resolution (task 1, scoped stop): each active child's parent link, read
  // from the record the start command loaded. Interrupt dispatch walks these edges
  // deepest-first so a subtree-root interrupt cancels only that session's descendants.
  private readonly ConcurrentDictionary<Guid, Guid?> _parents = [];

  // Enactment (fix round 1): each active child's own container runtime — the owner of
  // the run's REAL cancellation token. The server's _active CTSs are only waiter tokens
  // for WhenSettledAsync; cancelling them never touches the run itself.
  private readonly ConcurrentDictionary<Guid, IAgentRuntime> _runtimes = [];

  /// <summary>Live ROOT turns by turn id. A root turn has no record id, so the child
  ///     registries cannot address it — without this an interrupt envelope could only
  ///     abandon the app's local wait while the host kept executing the stopped turn.</summary>
  private readonly ConcurrentDictionary<string, CancellationTokenSource> _rootTurns = [];

  /// <summary>The ROOT container's steering inbox, resolved lazily from the root runner's
  ///     own services (issue #253). A root turn has no record id, so the child delivery
  ///     registry cannot address it either — and the APP container's inbox is a different
  ///     object in a different process that drains nothing. Delivering through this box is
  ///     what makes a mid-turn steer reach the model, and what arms the sticky user wake on
  ///     the hub the root's waits subscribe to.</summary>
  private IAgentInbox? _rootInbox;

  /// <summary>Set once the root inbox has been resolved (or definitively found absent), so
  ///     the resolve runs once per host rather than per envelope. Guarded by the same lock
  ///     the root-runner latch uses: both answer 'is the root runner up yet'.</summary>
  private bool _rootInboxResolved;

  // Child-event forwarding (issue #245): one subscription lease per running child, over
  // the CHILD CONTAINER's own event stream. The child's events publish into that
  // container's stream; without a forwarder they never leave the host process and the
  // app's subagent panel sees nothing. The lease dies with the child's finally block.
  private readonly ConcurrentDictionary<Guid, IDisposable> _eventLeases = [];

  // The ROOT runner (spec D6): the conversation's own agent runs in this process. Built
  // ONCE, lazily, from the host's own container - the root transcript sink seeds its
  // persisted baseline at construction, so a per-turn runner would re-seed mid-conversation
  // and re-append messages the store already holds. Null when the shipped settings name no
  // root session: a host with no bound session must not guess which conversation a turn
  // belongs to, and answers with a typed settle instead.
  private RootRunHost? _rootHost;
  // SINGLE-FLIGHT (fix round 2, finding 2): a plain bool read-then-write let two cold turns
  // race, and the loser observed 'built' while the field was still null - answering a FALSE
  // 'not bound to a root session' for a host that IS bound. The task is the latch: both
  // callers await the same build.
  private readonly Lock _rootHostGate = new();
  private Task<RootRunHost?>? _rootHostBuild;
  // WHY the host has no root runner, when it has none. The refusal a turn receives
  // must name the REAL cause: the previous build swallowed the exception that broke
  // the container and answered "not bound to a root session" for every turn, which
  // sent the reader hunting a wiring bug that was not there (the actual cause was a
  // locked database the host's own migration pass had caused).
  private string? _rootHostFailure;
  private long _sequence;

  /// <summary>The ids the host currently runs — the exact live set the app's orphan
  ///     repair consumes (FR-L8). Sent on every (re)connect and after each mutation.</summary>
  public IReadOnlyCollection<Guid> LiveIds() => [.. _active.Keys];

  /// <summary>Serves the CURRENT app connection. On entry the host declares its live
  ///     set (R3.1: a re-attaching app learns exactly which Running records the host
  ///     still owns). When the app goes away children keep running (survivability); a
  ///     later call with a fresh transport re-attaches them and settle emission
  ///     resumes through <see cref="AttachTransport"/>.</summary>
  public async Task ServeAsync()
  {
    // The caller attaches the transport BEFORE calling ServeAsync; hold it locally so
    // the serve loop reads one consistent connection even if a new one attaches later.
    NamedPipeChildTransport transport = _transport
        ?? throw new InvalidOperationException("AttachTransport must be called before ServeAsync.");
    await SendLiveSetAsync().ConfigureAwait(false);
    while (true)
    {
      TransportEnvelope envelope;
      try
      {
        envelope = await transport.ReceiveAsync().ConfigureAwait(false);
      }
      catch (TransportClosedException)
      {
        return; // app gone: children keep running (survivability)
      }

      // Named decision (CA1031): a failed ACK write IS the disconnect (broken pipe) —
      // return so the accept loop re-attaches the next connection; children keep running.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        await transport.SendAsync(new TransportEnvelope("ack", "\"" + envelope.Sequence + "\"", envelope.Sequence)).ConfigureAwait(false);
      }
      catch
      {
        return;
      }
#pragma warning restore CA1031

      // ISOLATION (fix round 1, finding 1): no single envelope may end the serve loop.
      // The handlers below each guard their own faults, but this boundary is the last
      // line: an unexpected fault costs the ENVELOPE, never the host and its children.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        await DispatchEnvelopeAsync(envelope).ConfigureAwait(false);
      }
      catch (TransportClosedException)
      {
        return; // the app went away: children keep running (survivability)
      }
      catch (Exception)
      {
        // Named decision (CA1031): see the isolation note above.
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }
  }

  /// <summary>Routes one envelope to its handler. Split out of the serve loop so the loop
  ///     itself can wrap the whole dispatch in one isolation boundary.</summary>
  private async Task DispatchEnvelopeAsync(TransportEnvelope envelope)
  {
    switch (envelope.Kind)
    {
      case "start":
        await HandleStartAsync(envelope).ConfigureAwait(false);
        break;
      case "deliver":
        HandleDeliver(envelope);
        break;
      case "root-steer":
        // Synchronous on purpose: the handler only enqueues into the root inbox (no I/O,
        // no model call), so it must not leave the serve loop. The DRAIN happens in the
        // root turn's own loop at its next safe point.
        HandleRootSteer(envelope);
        break;
      case "interrupt":
        HandleInterrupt(envelope);
        break;
      case "session-response":
        // Synchronous on purpose (issue #234): the handler only completes a pending
        // request's TaskCompletionSource — no I/O, no reason to leave the serve loop.
        HandleSessionResponse(envelope);
        break;
      case "root-turn":
        // OFF the serve loop (fix round 1, finding 4): a turn can run for minutes, and
        // awaiting it here stopped the host processing ANY other envelope - start,
        // deliver, interrupt, or the next turn - for its whole duration. Children already
        // run this way (HandleStartAsync fires Task.Run and returns).
        _ = Task.Run(() => HandleRootTurnAsync(envelope));
        break;
      default:
        break; // unknown kinds ignored forward-compatibly
    }
  }

  private async Task HandleStartAsync(TransportEnvelope envelope)
  {
    StartCommand command = JsonSerializer.Deserialize<StartCommand>(envelope.Json)
        ?? throw new InvalidOperationException("null start command.");
    if (_active.Count >= command.MaxConcurrent)
    {
      // Host-boundary budget enforcement (FR-X5): refuse rather than oversubscribe.
      await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, "ConcurrencyCapReached",
          "the child host is at its concurrency ceiling."))).ConfigureAwait(false);
      return;
    }

    CancellationTokenSource cts = new();
    _active[command.RecordId] = cts;
    _ = Task.Run(() => RunChildAsync(command, cts));
    // Named decision (CA1031): a failed live-set broadcast IS the disconnect; the accept
    // loop re-attaches. The child was already registered and keeps running.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      await SendLiveSetAsync().ConfigureAwait(false);
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
  }

  private async Task RunChildAsync(StartCommand command, CancellationTokenSource cts)
  {
    try
    {
      SessionHost host = SessionHost.Create(settingsPath, databasePath);
      AttachDeliveryRegistry(command.RecordId, host.Mailboxes);
      Result<AgentRecord> loaded = await host.Store.GetAsync(new AgentId(command.RecordId), CancellationToken.None).ConfigureAwait(false);
      if (!loaded.IsSuccess)
      {
        await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, "NotFound",
            "record not found in the shared database."))).ConfigureAwait(false);
        return;
      }

      // Subtree resolution: record the parent edge while the record is in hand, so
      // interrupt dispatch walks THIS child's ancestry without a store round-trip.
      _parents[command.RecordId] = loaded.Value.ParentId?.Value;
      // Enactment: the child's own container runtime owns the run's REAL cancellation
      // token — scoped interrupts must enact through it, not through the bookkeeping
      // CTS (whose cancellation only ends the settle WAIT, never the run).
      _runtimes[command.RecordId] = host.Runtime;

      // Route through the host's OWN runtime (not the raw spawner): children gain the
      // host-side supervisors, budget ceilings, and mailbox lifecycle the app-side
      // container enjoys. Start returns immediately (FR-L1); the settle is awaited.
      Result<AgentId> started = await host.Runtime.Start(loaded.Value, CancellationToken.None).ConfigureAwait(false);
      if (!started.IsSuccess)
      {
        await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, started.Error.Code,
            started.Error.Message))).ConfigureAwait(false);
        return;
      }

      // Host-side idle detection (handoff item 2): the host runs the watchdog over the
      // child run — facts from the child container's event stream, policy enacted
      // through the host's own runtime. The app never guesses from absent beats.
      if (BuildChildWatchdog(host, loaded.Value.Id) is { } watchdog)
      {
        watchdog.Start();
      }

      // Child-event forwarding (issue #245): the app's subagent panel subscribes the
      // APP container's event stream, and this child publishes into ITS OWN container's
      // stream — a different object in a different process. The forwarder republishes
      // every event as a child-event envelope, so the panel, badge, and wake adapters
      // observe remote children exactly like in-process ones.
      AttachEventForwarder(host, loaded.Value.Id.Value);
      Result<AgentRunOutcome> outcome = await host.Runtime.WhenSettledAsync(loaded.Value.Id, cts.Token).ConfigureAwait(false);
      if (outcome.IsSuccess)
      {
        await SendAsync("settle", JsonSerializer.Serialize(new SettleNotice(command.RecordId,
            outcome.Value.Status.ToString(), outcome.Value.Reason?.ToString(), outcome.Value.Report))).ConfigureAwait(false);
      }
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): the host is an isolation boundary — ANY child fault becomes
      // a well-formed error envelope to the app, never a host crash.
      await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, "HostFault", ex.Message))).ConfigureAwait(false);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      if (_eventLeases.TryRemove(command.RecordId, out IDisposable? lease))
      {
        lease.Dispose();
      }

      _ = _active.TryRemove(command.RecordId, out _);
      _ = _delivery.TryRemove(command.RecordId, out _);
      _ = _parents.TryRemove(command.RecordId, out _);
      _ = _runtimes.TryRemove(command.RecordId, out _);
      await SendLiveSetAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Subscribes the child-event forwarder (issue #245) to the CHILD CONTAINER's
  ///     own event stream: every event the child publishes is serialized into a
  ///     <see cref="ChildEventEnvelope"/> and sent as a <c>child-event</c> envelope, so
  ///     the app-side runtime can republish it onto the session's stream. The lease is
  ///     registered for the child's finally block to dispose. Best-effort: a container
  ///     without an event stream (a test rig) skips silently — the child still runs, only
  ///     the app's realtime view is absent. A failed subscribe never fails the run.
  ///     <para>The events are METADATA-ONLY by the ChildEvent family's own contract (D5):
  ///     ids, timestamps, enums, counts, short labels — never message or report content —
  ///     so the wire carries nothing the transcript does not already hold.</para></summary>
  private void AttachEventForwarder(SessionHost host, Guid recordId)
  {
    if (host.Services.GetService(typeof(IAgentEvents)) is not IAgentEvents events)
    {
      return;
    }

    // The envelope's child id comes from the EVENT itself (ChildEventEnvelope.From), so
    // the forwarder needs no per-child state — one subscriber shape serves every child.
    ChildEventForwarder forwarder = new(SendChildEventAsync);
    IDisposable lease = events.Subscribe(forwarder);
    _eventLeases[recordId] = lease;
  }

  /// <summary>Puts one forwarded child event on the wire. Fire-and-forget by contract:
  ///   the event stream's publisher is the child's own loop (a synchronous callback that
  ///   must never await a pipe write), so a failed or backpressured write drops the
  ///   event — the app's panel is a realtime VIEW, not a durable record (the transcript
  ///   store is), and a dropped progress beat costs nothing. The fault is contained
  ///   here: the stream's other subscribers must never see it.</summary>
  private async Task SendChildEventAsync(ChildEventEnvelope envelope)
  {
#pragma warning disable CA1031 // Named decision: a failed wire send must never reach the child loop.
    try
    {
      await SendAsync("child-event", JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>The forwarder itself: translates one child event into its wire envelope.
  ///   A fault in serialization is contained (the stream's fault boundary) — a lost
  ///   event degrades the realtime view, never the run.</summary>
  private sealed class ChildEventForwarder(Func<ChildEventEnvelope, Task> send) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      // Named decision (CA1031): presentation containment — the child loop's other
      // subscribers (the host watchdog's SupervisorFeed among them) must never see a
      // wire fault.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        _ = send(ChildEventEnvelope.From(evt));
      }
      catch
      {
        // Swallowed deliberately: see the named decision above.
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }
  }

  /// <summary>Builds the host watchdog for one child run from the child container's
  ///     own seams — the same WatchdogServices shape the app's DesktopHost builds per
  ///     session, here rooted at the child id so the registry path supervises it.
  ///     Best-effort: a container missing a seam yields no watchdog (the child still
  ///     runs; only idle detection is absent), never a failed start.</summary>
  private static HostChildWatchdog? BuildChildWatchdog(SessionHost host, AgentId childId)
  {
    if (host.Services.GetService(typeof(IAgentHeartbeat)) is not IAgentHeartbeat heartbeat
        || host.Services.GetService(typeof(IWatchdogEventStore)) is not IWatchdogEventStore audit)
    {
      return null;
    }

    // W1.2: the operator's SubAgent:Watchdog section governs the host watchdog —
    // exactly those values, or WatchdogOptions.Default when the app shipped none.
    WatchdogOptions options = host.EffectiveWatchdogOptions;
    IAgentEvents? stream = host.Services.GetService(typeof(IAgentEvents)) as IAgentEvents;
    ChildSupervisorRegistry? supervisors = host.Services.GetService(typeof(ChildSupervisorRegistry)) as ChildSupervisorRegistry;
    WatchdogServices services = new(host.Store, host.Runtime, heartbeat, audit,
        WatchdogPolicyFactory.FromOptions(options),
        options, TimeProvider.System, stream, supervisors);
    return new HostChildWatchdog(childId, services);
  }

  /// <summary>Handles one root turn (spec D6): the app forwards the user's input and the
  ///     host answers with that turn's event stream. The runner that EXECUTES the turn is
  ///     <see cref="RootRunHost"/>, built once with the host's container; a host with no bound
  ///     root session answers every ADDRESSABLE turn with a typed settle failure rather than
  ///     silence.
  /// <para>An UNADDRESSABLE turn — one whose id cannot be recovered from the payload — is
  /// dropped here with no reply, because there is no id to address a settle to. This method
  /// therefore does NOT by itself guarantee the caller settles; that guarantee belongs to
  /// the app side, where <c>RemoteAgentRuntime.SendRootTurnAsync</c> bounds every turn by
  /// its root-turn budget and fails <c>TurnUnaddressable</c> when no settle arrives. The
  /// two halves together mean no caller hangs. A malformed request is a peer fault, never a
  /// host crash.</para></summary>
  private async Task HandleRootTurnAsync(TransportEnvelope envelope)
  {
    (string TurnId, long Generation) = ReadTurnIdentityLeniently(envelope.Json);
    if (TurnId.Length == 0)
    {
      return; // unaddressable: a request carrying no turn id cannot be settled
    }

    RootRunHost? rootHost = await ResolveRootHostAsync().ConfigureAwait(false);
    if (rootHost is null)
    {
      // No root session is bound to this host (the settings named none): answer with a
      // typed settle rather than silence. The generation is echoed verbatim - it is the
      // app's per-turn identity, and a settle that does not carry it back is dropped by
      // the client as stale (ruling 2).
      await SendAsync("root-turn-event", RootTurnProtocol.SerializeEvent(new RootTurnEvent(TurnId,
          "settled",
          "Error [RootTurnUnavailable]: this conversation host is not bound to a root session"
          + (_rootHostFailure is null ? "." : " (" + _rootHostFailure + ")"),
          Generation)))
          .ConfigureAwait(false);
      return;
    }

    RootTurnRequest request;
    try
    {
      request = RootTurnProtocol.ParseRequest(envelope.Json);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): a malformed payload is a PEER fault. The turn id was
      // recoverable, so the turn is answered with a typed settle; letting the parse fault
      // escape would end ServeAsync and take the host - and every running child - with it.
      await SendRootTurnEventAsync(new RootTurnEvent(TurnId, "settled",
          "Error [InvalidRootTurn]: " + ex.Message, Generation)).ConfigureAwait(false);
      return;
    }
#pragma warning restore CA1031 // Do not catch general exception types

    // The ROOT turn's own cancellation source, registered so an interrupt envelope can
    // reach it. Without this a stop only abandoned the app's local wait: the host kept
    // executing the turn, kept growing the conversation, and persisted a turn the user had
    // stopped. Keyed by turn id, since a root turn has no record id.
    using CancellationTokenSource turnCts = new();
    _rootTurns[request.TurnId] = turnCts;
    try
    {
      _ = await rootHost
          .RunTurnAsync(request, evt => SendRootTurnEventAsync(evt).GetAwaiter().GetResult(),
              turnCts.Token)
          .ConfigureAwait(false);
      // The settled event already carried the outcome to the app; the Result is returned
      // for the caller's own logging and is deliberately not turned into a second envelope.
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): the same isolation boundary the child path honors. A turn
      // that faults settles with the fault instead of escaping into the serve loop.
      await SendRootTurnEventAsync(new RootTurnEvent(TurnId, "settled",
          "Error [RootTurnFault]: " + HostFault.Describe(ex), Generation)).ConfigureAwait(false);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      _ = _rootTurns.TryRemove(request.TurnId, out _);
    }
  }

  /// <summary>Resolves the ROOT runner once, from the host's own container (spec D6).
  ///     Lazy rather than eager: a host that only ever runs children must not pay for a
  ///     session container it never uses, and a container that cannot be built must not
  ///     stop children from running. A build failure is reported the same way a missing
  ///     binding is — a typed settle on the turn that needed it — never a host crash.</summary>
  private async Task<RootRunHost?> ResolveRootHostAsync()
  {
    Task<RootRunHost?> build;
    lock (_rootHostGate)
    {
      _rootHostBuild ??= BuildRootHostAsync();
      build = _rootHostBuild;
    }

    return await build.ConfigureAwait(false);
  }

  /// <summary>The one build attempt. A container that cannot be built leaves root turns
  ///     unserved (a typed settle answers them) and never takes the child-serving path down
  ///     with it.</summary>
  private Task<RootRunHost?> BuildRootHostAsync()
  {
    try
    {
      _rootHost = SessionHost.Create(settingsPath, databasePath).RootRun;
      _rootHostFailure = _rootHost is null
          ? "the host settings named no root session id."
          : null;
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): the host is an isolation boundary - a container that
      // cannot be built must never take the child-serving path down with it. The
      // REASON is kept, though: the turn that needs this runner answers with it, so
      // the user reads what actually broke instead of a generic refusal.
      _rootHost = null;
      _rootHostFailure = HostFault.Describe(ex);
    }
#pragma warning restore CA1031 // Do not catch general exception types

    return Task.FromResult(_rootHost);
  }

  /// <summary>Puts one streamed root-turn event on the wire. The runner's callback is
  ///     synchronous by contract (the loop fires deltas from arbitrary threads and never
  ///     awaits the observer), so the write is awaited inline here: events reach the app in
  ///     the order the turn produced them, and a failed write surfaces as the transport
  ///     fault it is rather than vanishing into an unobserved task.
  ///     <para>THE WRITE IS UNBOUNDED (fix round 2, finding 3 — the previous text claimed a
  ///     bound the code never had). <see cref="SendAsync"/> passes NO cancellation token:
  ///     <c>NamedPipeChildTransport.SendAsync</c> awaits its write gate with
  ///     <c>CancellationToken.None</c> and frames with <c>CancellationToken.None</c>. A peer
  ///     that stops reading therefore blocks this call indefinitely, and because it is
  ///     awaited inline from the delta callback it stalls the provider's stream read and the
  ///     turn with it. The consequence is stated rather than fixed: bounding this write needs
  ///     a token threaded through the whole transport write path, which is a transport-level
  ///     change, not a host-side one.</para></summary>
  private async Task SendRootTurnEventAsync(RootTurnEvent evt)
      => await SendAsync("root-turn-event", RootTurnProtocol.SerializeEvent(evt)).ConfigureAwait(false);

  /// <summary>Reads the turn IDENTITY (id + generation) from a root-turn payload WITHOUT the
  ///     codec's strictness, so a malformed request can still be answered instead of leaving
  ///     its caller waiting. The generation defaults to 0 when absent; an empty turn id means
  ///     the request is unaddressable.</summary>
  private static (string TurnId, long Generation) ReadTurnIdentityLeniently(string json)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(json);
      if (document.RootElement.ValueKind != JsonValueKind.Object
          || !document.RootElement.TryGetProperty("TurnId", out JsonElement turnId)
          || turnId.ValueKind != JsonValueKind.String)
      {
        return (string.Empty, 0);
      }

      long generation = document.RootElement.TryGetProperty("Generation", out JsonElement gen)
          && gen.ValueKind == JsonValueKind.Number
          && gen.TryGetInt64(out long parsed)
              ? parsed
              : 0;
      return (turnId.GetString() ?? string.Empty, generation);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): undecodable input is a peer fault, not a host crash.
      return (string.Empty, 0);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Completes one pending session-request with the app's answer (issue #234).
  ///     The envelope carries the new session's id, or the typed refusal the app
  ///     answered with. Unknown requestIds are ignored by the channel (a late or
  ///     duplicate response for an already-completed request); a response that names
  ///     NEITHER a session id NOR an error is a malformed peer message and is answered
  ///     as a SessionResponseMalformed failure rather than left pending.
  ///     Synchronous like HandleDeliver: it completes a TCS and returns.</summary>
  private void HandleSessionResponse(TransportEnvelope envelope)
  {
    SessionResponseCommand? command;
    try
    {
      command = JsonSerializer.Deserialize<SessionResponseCommand>(envelope.Json);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): an undecodable response is a PEER fault; there is no
      // requestId to answer, so it is dropped — the requesting tool's own budget and
      // the turn's teardown bound the wait, never this handler.
      return;
    }
#pragma warning restore CA1031
    if (command is null || string.IsNullOrWhiteSpace(command.RequestId))
    {
      return; // unaddressable: no requestId means no pending request to complete
    }

    Result<string> outcome = (command.SessionId, command.ErrorCode) switch
    {
      ({ Length: > 0 } sessionId, _) => Result.Success(sessionId),
      (_, { Length: > 0 } errorCode) => Result.Failure<string>(new DomainError(errorCode,
          command.ErrorMessage ?? "the app refused the session launch.")),
      _ => Result.Failure<string>(new DomainError("SessionResponseMalformed",
          "the app's session-response named neither a session id nor an error.")),
    };
    _ = ResolveRootHostAsync().ContinueWith(resolved =>
    {
      // The resolution is awaited here rather than blocking the serve loop: the runner
      // may still be building on a cold host. A null runner (no root session bound, or
      // the build failed) is a silent drop — there is no requester to answer.
      if (resolved.Result is { } rootHost)
      {
        rootHost.CompleteSessionRequest(command.RequestId, outcome);
      }
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
  }

  /// <summary>Delivers a steering envelope into the running child's mailbox (FR-C2).
  ///     NotRunning (unknown/finished child) and MailboxFull are dropped here: the
  ///     APP-side runtime already returned those receipts to the sender synchronously —
  ///     the wire path is the at-least-once replay, and a replay after a settle is
  ///     stale by definition (A3: no silent drops on the live path).</summary>
  private void HandleDeliver(TransportEnvelope envelope)
  {
    DeliverCommand? command = JsonSerializer.Deserialize<DeliverCommand>(envelope.Json);
    if (command is null)
    {
      return;
    }

    if (_delivery.TryGetValue(command.RecordId, out ChildMailboxRegistry? registry)
        && registry.MailboxFor(new AgentId(command.RecordId)) is { } mailbox)
    {
      // The identity travels with the message (issue #92): the app-side sender's receipt
      // named this handle, so the host's drain/remainder facts must name the same one.
      // An older app's frame carries no id; minting one here keeps delivery working while
      // leaving that message unaddressable - the pre-handshake behavior.
      Guid messageId = command.MessageId == Guid.Empty ? Guid.NewGuid() : command.MessageId;
      _ = mailbox.Deliver(new PendingMessage(command.Text, (MessageUrgency)command.Urgency,
          DateTimeOffset.UtcNow, command.Sender, messageId));
    }
  }

  /// <summary>Delivers one steering message into the ROOT container's inbox (issue #253).
  ///     <para>WHY THIS EXISTS: every conversation runs its turn in a host process (spec
  ///     D6), so the app's own container runs no agent work. The app's steering inbox was
  ///     therefore a mailbox nothing drains — a message typed during a turn was queued in
  ///     the wrong process, never reached the model, and never woke an <c>agent.wait</c>
  ///     (whose sticky user wake arms on the HOST container's hub). This is the root
  ///     counterpart of the child deliver path.</para>
  ///     <para>An unknown turn id is a silent drop, matching the child path's pinned
  ///     contract: the wire is the at-least-once replay, and a message addressed to a turn
  ///     that already ended is stale by definition. A refused post (full or closed
  ///     mailbox) is likewise dropped here — the app-side post is the one that reports a
  ///     refusal to the human.</para></summary>
  private void HandleRootSteer(TransportEnvelope envelope)
  {
    RootSteerCommand? command = JsonSerializer.Deserialize<RootSteerCommand>(envelope.Json);
    if (command is null || string.IsNullOrWhiteSpace(command.TurnId))
    {
      return;
    }

    DeliverToRootTurn(command.TurnId, command.Text, command.Sender, command.MessageId);
  }

  /// <summary>Delivers one steering message to a LIVE root turn's inbox, or drops it when
  ///     the turn is not live here. Public so the wire tests can drive the production
  ///     delivery path without a full provider stack (the same seam shape
  ///     <see cref="AttachDeliveryRegistry"/> gives the child path).</summary>
  public void DeliverToRootTurn(string turnId, string text, string sender, Guid messageId = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
    if (!_rootTurns.ContainsKey(turnId))
    {
      return; // not a live turn: a stale replay, dropped like the child path's
    }

    if (ResolveRootInbox() is not { } inbox)
    {
      return; // this host has no root conversation bound: nothing to steer
    }

    // The identity travels with the message (issue #92, and issue #253 B3): the app pinned
    // its echo under this id, and the loop's drain fact names it back - that name is what
    // releases the pin. Minting a fresh id here would leave the echo pinned forever.
    Guid id = messageId == Guid.Empty ? Guid.NewGuid() : messageId;
    PendingMessage message = new(text, MessageUrgency.Normal, DateTimeOffset.UtcNow, sender, id);
    if (inbox is IAgentMailbox mailbox)
    {
      _ = mailbox.Deliver(message);
      return;
    }

    _ = inbox.TryPost(text);
  }

  /// <summary>Subscribes the child-event forwarder to the ROOT container's event stream
  ///     (issue #253 B3), so the root loop's mailbox lifecycle facts - above all the DRAIN
  ///     naming which steering messages it consumed - reach the app. The app's pinned
  ///     steering echo is released by exactly that name; without the forwarder the echo
  ///     would stay pinned forever, because the drain happens in this process.
  ///     <para>Idempotent per runner: the lease is kept and disposed with the server. A
  ///     container with no event stream (a test rig) skips silently.</para></summary>
  private void AttachRootEventForwarder(RootRunHost runner)
  {
    if (runner.Services.GetService(typeof(IAgentEvents)) is not IAgentEvents events)
    {
      return;
    }

    ChildEventForwarder forwarder = new(SendChildEventAsync);
    _rootEventLease ??= events.Subscribe(forwarder);
  }

  /// <summary>The ROOT container's event-forwarder lease (issue #253 B3): one per host,
  ///     attached with the root runner.</summary>
  private IDisposable? _rootEventLease;

  /// <summary>The ROOT container's steering inbox, resolved once from the runner's own
  ///     services. Null when this host serves no root conversation (a child-only host) or
  ///     the container has no inbox registered — both are ordinary, not faults.</summary>
  private IAgentInbox? ResolveRootInbox()
  {
    lock (_rootHostGate)
    {
      if (_rootInboxResolved)
      {
        return _rootInbox;
      }
    }

    // The runner is built lazily on the first turn; resolving here would race that build,
    // so the resolve is retried until it succeeds. A host with no bound session answers
    // null forever, which is the honest 'nothing to steer'.
    if (_rootHost is not { } runner)
    {
      return null;
    }

    // The root container's event stream is the only place its drain fact is published
    // (issue #253 B3), so the forwarder attaches the moment the runner exists.
    AttachRootEventForwarder(runner);
    IAgentInbox? resolved = runner.Services.GetService(typeof(IAgentInbox)) as IAgentInbox;
    lock (_rootHostGate)
    {
      _rootInbox = resolved;
      _rootInboxResolved = resolved is not null;
    }

    return resolved;
  }

  /// <summary>Registers the per-child container registry whose MailboxFor answers the
  ///     deliver path (RunChildAsync attaches each child's own; tests may attach a rig
  ///     registry). MailboxFor returns null once the runtime unregisters the box at
  ///     settle — stale delivers stay silent drops (the pinned HandleDeliver contract).</summary>
  public void AttachDeliveryRegistry(Guid recordId, ChildMailboxRegistry registry)
      => _delivery[recordId] = registry;

  /// <summary>Registers a live child run the way RunChildAsync does (a cancellable CTS
  ///     in the active set) together with the record's parent link and the CHILD
  ///     CONTAINER's runtime — the runtime whose internal CTS is the run's ACTUAL
  ///     cancellation token, so interrupt dispatch can enact through it. The parent
  ///     edge is read from the record the start command loaded; tests may attach rigs
  ///     the same way they attach delivery registries.</summary>
  public void AttachActiveChild(Guid recordId, Guid? parentId, CancellationTokenSource cts, IAgentRuntime runtime)
  {
    _parents[recordId] = parentId;
    _active[recordId] = cts;
    _runtimes[recordId] = runtime;
  }

  /// <summary>Registers the ROOT runner the server dispatches 'root-turn' envelopes to,
  ///     the way <see cref="AttachDeliveryRegistry"/> registers a delivery target: tests
  ///     drive the real dispatch path over a real pipe with a scripted runner, without
  ///     building a full provider stack. Production resolves its runner from the host's own
  ///     container (<see cref="ResolveRootHostAsync"/>).</summary>
  public void AttachRootRun(RootRunHost runner)
  {
    ArgumentNullException.ThrowIfNull(runner);
    lock (_rootHostGate)
    {
      _rootHost = runner;
      // A runner attached here answers every turn, so the "no root runner" reason
      // (and any earlier one) no longer applies.
      _rootHostFailure = null;
      // Pre-completing the build task is the latch: every later resolve shares it and no
      // caller can observe a half-built state.
      _rootHostBuild = Task.FromResult<RootRunHost?>(runner);
    }
  }

  /// <summary>Observation seam for tests: the runner resolution the dispatch path uses, so a
  ///     concurrency test can drive the LATCH directly. Production reaches it through
  ///     <see cref="HandleRootTurnAsync"/>; the seam exists because building a real provider
  ///     stack costs seconds and the property under test is the latch, not the container.</summary>
  public Task<RootRunHost?> ResolveRootHostForTest() => ResolveRootHostAsync();

  /// <summary>Registers a live ROOT turn's cancellation source the way
  ///     <see cref="HandleRootTurnAsync"/> does, so a test can exercise the scoped
  ///     root-turn interrupt over a real pipe without running a whole agent turn.</summary>
  public void RegisterRootTurn(string turnId, CancellationTokenSource cts)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
    ArgumentNullException.ThrowIfNull(cts);
    _rootTurns[turnId] = cts;
  }

  /// <summary>Observation seam for tests: the cancellation token of one active child's
  ///     run, or null when the host runs no such child.</summary>
  public CancellationToken? ActiveToken(Guid recordId)
      => _active.TryGetValue(recordId, out CancellationTokenSource? cts) ? cts.Token : null;

  /// <summary>Interrupt dispatch. A NULL record id is the legacy blanket cancel: every
  ///     active CTS in the host. A NON-NULL id is a SUBTREE-ROOT interrupt (task 1,
  ///     scoped stop): interrupt that id and every ACTIVE descendant through the child
  ///     containers' OWN runtimes — the run's real cancellation token lives there; the
  ///     server's bookkeeping CTSs are waiter tokens only and are deliberately left
  ///     alone so the real Interrupted outcome reaches the app as a settle envelope.
  ///     Deepest-first so parents observe children settling before their own teardown
  ///     (the in-process runtime's InterruptSubtree contract). The walk covers only ids
  ///     the host actively runs, so another session's children — whose ancestry never
  ///     reaches this root — are untouched.</summary>
  private void HandleInterrupt(TransportEnvelope envelope)
  {
    InterruptCommand? command = JsonSerializer.Deserialize<InterruptCommand>(envelope.Json);

    // A SUPPLIED TurnId MAKES THIS INTERRUPT SCOPED, and it must RETURN here. A root turn
    // has no record id, so a root-turn stop arrives with RecordId null - which is exactly
    // the shape the blanket branch below treats as 'cancel everything'. Falling through
    // would cancel every child run in the host, INCLUDING OTHER SESSIONS' CHILDREN, which
    // contradicts this method's own contract and the FR-C6 scoped-teardown rule. The stop
    // button sends the subtree interrupt and this root-turn interrupt as TWO envelopes, so
    // each one does exactly its own job.
    if (command!.TurnId is { Length: > 0 } turnId)
    {
      if (_rootTurns.TryGetValue(turnId, out CancellationTokenSource? turnCts))
      {
        turnCts.Cancel();
      }

      return;
    }

    if (command.RecordId is { } root)
    {
      // Deepest first: children settle before their parents observe the teardown.
      List<Guid> targets = [.. _active.Keys
          .Where(activeId => activeId == root || IsDescendantOf(activeId, root))
          .OrderByDescending(DepthOf)];
      foreach (Guid id in targets)
      {
        // Enactment: cancel the run's REAL token through the child container's own
        // runtime — the bookkeeping CTS only ends the settle WAIT, so cancelling it
        // alone would leave the run executing (the fix-round-1 finding). It is
        // deliberately NOT cancelled here: a cancelled waiter token makes
        // WhenSettledAsync return a bare Cancelled failure and the run's real
        // Interrupted outcome would never be forwarded to the app as a settle
        // envelope. The run settles itself, completing the wait naturally.
        if (_runtimes.TryGetValue(id, out IAgentRuntime? runtime))
        {
          runtime.Interrupt(new AgentId(id));
        }
      }
    }
    else
    {
      foreach (CancellationTokenSource cts in _active.Values)
      {
        cts.Cancel();
      }
    }
  }

  private bool IsDescendantOf(Guid candidate, Guid root)
  {
    Guid? cursor = candidate;
    while (cursor is { } current)
    {
      if (current == root)
      {
        return true;
      }

      cursor = _parents.GetValueOrDefault(current);
    }

    return false;
  }

  private int DepthOf(Guid id)
  {
    int depth = 0;
    Guid? cursor = id;
    while (cursor is { } current && _parents.TryGetValue(current, out Guid? parent) && parent is not null)
    {
      depth++;
      cursor = parent;
    }

    return depth;
  }

  /// <summary>Declares the host's live child set to the app (R3.2's exact orphan
  ///     resolution input). Called on connect and after every start/settle mutation.</summary>
  private async Task SendLiveSetAsync()
      => await SendAsync("declare", JsonSerializer.Serialize(new DeclareCommand([.. LiveIds()]))).ConfigureAwait(false);

  /// <summary>Re-attach (R3.1): swaps in the app's NEW connection so settles for
  ///     children that kept running during the app's absence reach it again.
  ///     Single-connection by design; the old transport is closed first.</summary>
  public void AttachTransport(NamedPipeChildTransport fresh)
  {
    NamedPipeChildTransport? stale = Interlocked.Exchange(ref _transport, fresh);
    if (stale is not null)
    {
      _ = Task.Run(async () => await stale.DisposeAsync().ConfigureAwait(false));
    }
  }

  private async Task SendAsync(string kind, string json)
  {
    NamedPipeChildTransport? transport = _transport;
    if (transport is null)
    {
      return; // no app attached (yet): settles/declares resume on the next attach
    }

    long sequence = Interlocked.Increment(ref _sequence);
    await transport.SendAsync(new TransportEnvelope(kind, json, sequence)).ConfigureAwait(false);
  }
}

public sealed record HostError(Guid RecordId, string Code, string Message);
