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

      switch (envelope.Kind)
      {
        case "start":
          await HandleStartAsync(envelope).ConfigureAwait(false);
          break;
        case "deliver":
          HandleDeliver(envelope);
          break;
        case "interrupt":
          HandleInterrupt(envelope);
          break;
        case "root-turn":
          await HandleRootTurnAsync(envelope).ConfigureAwait(false);
          break;
        default:
          break; // unknown kinds ignored forward-compatibly
      }
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
      _ = _active.TryRemove(command.RecordId, out _);
      _ = _delivery.TryRemove(command.RecordId, out _);
      _ = _parents.TryRemove(command.RecordId, out _);
      _ = _runtimes.TryRemove(command.RecordId, out _);
      await SendLiveSetAsync().ConfigureAwait(false);
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
  ///     host answers with that turn's event stream. The runner that actually EXECUTES the
  ///     turn is task 9's work; until it is wired, the host answers every ADDRESSABLE turn
  ///     with a typed settle failure rather than silence.
  /// <para>An UNADDRESSABLE turn — one whose id cannot be recovered from the payload — is
  /// dropped here with no reply, because there is no id to address a settle to. This method
  /// therefore does NOT by itself guarantee the caller settles; that guarantee belongs to
  /// the app side, where <c>RemoteAgentRuntime.SendRootTurnAsync</c> bounds every turn by
  /// its root-turn budget and fails <c>TurnUnaddressable</c> when no settle arrives. The
  /// two halves together mean no caller hangs. A malformed request is a peer fault, never a
  /// host crash.</para></summary>
  private async Task HandleRootTurnAsync(TransportEnvelope envelope)
  {
    string turnId = ReadTurnIdLeniently(envelope.Json);
    if (turnId.Length == 0)
    {
      return; // unaddressable: a request carrying no turn id cannot be settled
    }

    await SendAsync("root-turn-event", RootTurnProtocol.SerializeEvent(new RootTurnEvent(turnId,
        "settled",
        "Error [RootTurnUnavailable]: the conversation host has no root turn runner wired in this build.")))
        .ConfigureAwait(false);
  }

  /// <summary>Reads the turn id from a root-turn payload WITHOUT the codec's strictness,
  ///     so a malformed request can still be answered instead of leaving its caller
  ///     waiting. Returns empty when no turn id can be recovered at all.</summary>
  private static string ReadTurnIdLeniently(string json)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(json);
      return document.RootElement.ValueKind == JsonValueKind.Object
          && document.RootElement.TryGetProperty("TurnId", out JsonElement turnId)
          && turnId.ValueKind == JsonValueKind.String
              ? turnId.GetString() ?? string.Empty
              : string.Empty;
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): undecodable input is a peer fault, not a host crash.
      return string.Empty;
    }
#pragma warning restore CA1031 // Do not catch general exception types
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
      _ = mailbox.Deliver(new PendingMessage(command.Text, (MessageUrgency)command.Urgency,
          DateTimeOffset.UtcNow, command.Sender));
    }
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
    if (command!.RecordId is { } root)
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
