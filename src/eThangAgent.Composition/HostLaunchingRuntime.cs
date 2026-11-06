using System.Collections.Concurrent;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>THE HOST'S ORCHESTRATION MECHANISM (issue #305 D1/D2): the <see cref="IAgentRuntime"/>
///     a conversation host resolves, which runs each agent in its OWN host process instead of
///     sharing this one.
///     <para>WHAT CHANGED AND WHY. Until #305 a host ran every child it was handed inside its own
///     process: one job object, one pipe pair, one scratch folder for all of them. That is the
///     condition the direction exists to end — a hung agent sharing a process with three others
///     cannot be killed without killing them, and a crashed one cannot be resumed without taking
///     the others down. D1 is explicit that the in-process child runtime is RETIRED rather than
///     kept alongside, because two orchestration mechanisms is the thing being ended.</para>
///     <para>WHAT 'RETIRED' MEANS HERE, STATED SO NOBODY HAS TO GUESS. The orchestration decision
///     — how a parent runs an agent — is now singular: launch a host. The in-process runner
///     survives as the LEAF EXECUTOR that runs the one agent a host owns (the host's local runner), and every agent uses it. There is exactly one way to
///     start an agent and exactly one runner, so there is no second mechanism; a host that
///     called back into this type would recurse into launching a host for the agent it already
///     is, which is why the host's server reaches for the local runner explicitly.</para>
///     <para>ONE HOST PER AGENT, AND THE IDENTITY IS THE AGENT. Pipe name and scratch folder
///     both derive from the record id (<see cref="RemoteHostSupervisor.PipeNameForConversation"/>),
///     so two agents get two hosts, two job objects, and two scratch folders — and the same
///     derivations stay stable across an app restart. The host is disposed once its agent
///     settles: the host serves one connection and then exits (issue #274), so keeping it
///     alive would leave a process nobody will ever dial again.</para>
///     <para>CONTAINMENT STILL HOLDS ACROSS THE EXTRA LEVEL. A child host launched from a parent
///     host joins a job object the PARENT holds, which is itself inside the job the APP holds,
///     so killing the app still reaps the whole tree. Killing ONE agent's host leaves its
///     siblings untouched — criterion 9 — because each sibling has its own job.</para>
///     <para>ADMISSION IS THE HOST CAP (criterion 3/4). A start asks <see cref="HostAdmissionGate"/>
///     for a host slot; past the cap it PARKS rather than dropping, and the parked start keeps
///     its settle source so a settle observer finds it instead of failing NotFound — the same
///     contract the in-process runtime gave a queued start (#238), now at the host boundary. A
///     released slot is handed directly to the park head.</para></summary>
public sealed class HostLaunchingRuntime : IAgentRuntime, IAsyncDisposable
{
  private readonly AgentSettings _settings;
  private readonly string _databasePath;
  private readonly Func<string> _hostExePath;
  private readonly Action<string> _reportNotice;
  private readonly HostAdmissionGate _admission;
  // The stream a launched host's forwarded agent events republish onto. Null drops them.
  private readonly IAgentEvents? _events;
  private readonly Lock _gate = new();

  // One live host per agent: the supervisor (the process + its job) and the wire runtime that
  // talks to it. Both are created together and disposed together.
  private readonly Dictionary<Guid, LaunchedAgent> _live = [];

  // Launches and retires that have not finished yet. A host process exists BEFORE its entry
  // reaches _live (the supervisor spawns it, then attaches) and outlives the entry's removal
  // (the retire disposes it after taking it out), so neither half is visible in _live alone.
  // Teardown awaits these, which is what makes 'disposed' mean 'no host of mine is alive'.
  private readonly HashSet<Task> _inFlight = [];

  // True once teardown has begun. A start is refused rather than admitted into a runtime that
  // will never dispose the host it launches.
  private bool _disposed;

  // Every start owns its settle source BEFORE admission, so a parked agent is awaitable and
  // steerable from the moment its caller is told it started (the #238 rule, at host granularity).
  private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentRunOutcome>> _settling = [];

  /// <summary>Builds the launcher over the settings this host was started with: a child host is
  ///     launched with the SAME settings (same workspace, same skill and trusted-directory
  ///     lists, same provider keys), differing only in the session id that keys its pipe and
  ///     scratch folder.</summary>
  public HostLaunchingRuntime(AgentSettings settings, string databasePath, HostAdmissionGate admission,
      Action<string> reportNotice, IAgentEvents? events = null, Func<string>? hostExePath = null)
  {
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    ArgumentNullException.ThrowIfNull(admission);
    _settings = settings;
    _databasePath = databasePath;
    _admission = admission;
    _reportNotice = reportNotice;
    _events = events;
    // null keeps the default resolution (the copy shipped next to the app, else the repo's
    // live build output) — never a null dereference.
    _hostExePath = hostExePath ?? DefaultHostExePath;
  }

  /// <summary>The cap this runtime admits against (exposed for diagnostics and tests).</summary>
  public int HostCapacity => _admission.Capacity;

  /// <summary>Agents currently holding a host slot.</summary>
  public int LiveHosts => _admission.Live;

  /// <summary>Agents parked at the host boundary, in admission order.</summary>
  public int ParkedAgents => _admission.ParkedCount;

  /// <inheritdoc />
  public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    // REFUSED ONCE TEARDOWN HAS BEGUN: a host launched now would be spawned after the walk that
    // disposes hosts, so nothing would ever ask it to exit.
    lock (_gate)
    {
      if (_disposed)
      {
        return Task.FromResult(Result.Failure<AgentId>(new DomainError("RuntimeDisposed",
            "this container is being disposed; the agent was not started.")));
      }
    }

    return TrackedAsync(() => StartCoreAsync(record, ct));
  }

  /// <summary>Runs one start with a place in the in-flight set, so teardown waits for it.</summary>
  private async Task<Result<AgentId>> TrackedAsync(Func<Task<Result<AgentId>>> start)
  {
    TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (_gate)
    {
      _ = _inFlight.Add(finished.Task);
    }

    try
    {
      return await start().ConfigureAwait(false);
    }
    finally
    {
      lock (_gate)
      {
        _ = _inFlight.Remove(finished.Task);
      }

      _ = finished.TrySetResult();
    }
  }

  /// <summary>The start itself: admission, then a launch when a slot is free.</summary>
  private async Task<Result<AgentId>> StartCoreAsync(AgentRecord record, CancellationToken ct)
  {
    // The settle source exists from the moment the caller is told the agent started, whether it
    // runs now or parks — that is what makes a parked agent awaitable instead of a NotFound.
    _ = _settling.AddOrUpdate(record.Id.Value,
        static _ => NewSettleSource(),
        static (_, existing) => existing.Task.IsCompleted ? NewSettleSource() : existing);

    HostAdmission decision = _admission.Request(record.Id.Value);
    if (!decision.IsAdmitted)
    {
      // PARKED, never dropped: the row stays as the caller persisted it (Queued) and the
      // settle source stays alive. A drop would leave a Running row with no run behind it —
      // the stranded-row defect this same issue fixes.
      return Result.Success(record.Id);
    }

    return await LaunchAsync(record, ct).ConfigureAwait(false);
  }

  /// <summary>Launches one agent's host and starts the run on it. A launch fault RELEASES the
  ///     slot and hands it to the park head, so a failed launch can never leak capacity.</summary>
  private async Task<Result<AgentId>> LaunchAsync(AgentRecord record, CancellationToken ct)
  {
    RemoteHostSupervisor? supervisor = null;
    try
    {
      // The child's OWN id keys the pipe and the scratch folder, so this host is that agent's
      // alone. The supervisor stamps it as the session id, which is what lets the launched host
      // resolve the session it is bound to.
      string identity = record.Id.Value.ToString();
      // Named decision (CA2000): ownership of this supervisor TRANSFERS. Once the launch succeeds
      // it is held in _live and disposed by RetireAsync when the agent settles; if the launch
      // fails, the catch below disposes it. It is never owned by this method's own scope, which
      // is exactly the case the analyzer cannot see across a field assignment.
#pragma warning disable CA2000 // Ownership transfers to _live, or to the catch below.
      supervisor = new RemoteHostSupervisor(
          identity,
          Path.Combine(Path.GetTempPath(), "ethang-agent",
              RemoteHostSupervisor.ScratchFolderForConversation(identity)),
          _settings,
          _databasePath,
          _reportNotice,
          _hostExePath);
#pragma warning restore CA2000

      // The launched host forwards its child's events as agent-event envelopes; this
      // stream is where they land, so the container that launched the agent observes it.
      RemoteAgentRuntime runtime = new(
          transport: null,
          _settings.SubAgents.RootTurnBudget ?? RootTurnBudget.Default,
          events: _events);
      _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(false);

      lock (_gate)
      {
        _live[record.Id.Value] = new LaunchedAgent(supervisor, runtime);
      }

      Result<AgentId> started = await runtime.Start(record, ct).ConfigureAwait(false);
      if (!started.IsSuccess)
      {
        await RetireAsync(record.Id).ConfigureAwait(false);
        return started;
      }

      // The settle watch owns the host's lifetime: when the agent finishes, its host has served
      // its one connection and is disposed, releasing the slot to the park head.
      _ = WatchSettleAsync(record.Id, runtime);
      return started;
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // A launch fault is a NAMED failure for the caller, and the slot goes back either way —
      // an exception escaping here would leak capacity for the process's whole lifetime.
      if (supervisor is not null)
      {
        await supervisor.DisposeAsync().ConfigureAwait(false);
      }

      await RetireAsync(record.Id).ConfigureAwait(false);
      return Result.Failure<AgentId>(new DomainError("HostUnavailable",
          "the agent's host could not be launched: " + ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message));
    }
  }

  /// <summary>Waits for one agent's settle, then retires its host and releases the slot. Runs
  ///     detached: the caller observes the outcome through <see cref="WhenSettledAsync"/>.</summary>
  private async Task WatchSettleAsync(AgentId id, RemoteAgentRuntime runtime)
  {
    try
    {
      _ = await runtime.WhenSettledAsync(id, CancellationToken.None).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // The retire below runs either way; the settle source carries the outcome to the waiter.
    }

    await RetireAsync(id).ConfigureAwait(false);
  }

  /// <summary>Disposes one agent's host and releases its slot to the park head. Idempotent: a
  ///     second retire for the same id finds nothing and releases nothing.
  ///     <para>THE RETIRE IS VISIBLE TO TEARDOWN FROM ITS FIRST INSTRUCTION (issue #368). Taking
  ///     the entry out of the live set and disposing the supervisor are two steps, and a teardown
  ///     that snapshotted between them would return with the host still inside its bounded exit
  ///     wait - the leak the suite's guard reports. Registering the retire in the in-flight set
  ///     before the entry moves is what makes teardown wait for it.</para></summary>
  private Task RetireAsync(AgentId id)
  {
    TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (_gate)
    {
      if (!_live.TryGetValue(id.Value, out LaunchedAgent? launched))
      {
        return Task.CompletedTask;
      }

      _ = _inFlight.Add(finished.Task);
      _ = _live.Remove(id.Value);
      return RetireCoreAsync(launched, finished);
    }
  }

  /// <summary>Disposes one retired host, releases its slot, and settles the in-flight entry.
  ///     Runs OUTSIDE the gate: disposal is a bounded process wait, and holding the lock across
  ///     it would serialize every other start and teardown behind it.</summary>
  private async Task RetireCoreAsync(LaunchedAgent launched, TaskCompletionSource finished)
  {
    try
    {
      await launched.Supervisor.DisposeAsync().ConfigureAwait(false);

      // Release hands the slot DIRECTLY to the park head inside the admission gate, so a fresh
      // start can neither starve a parked agent nor push the live count past the cap.
      HostAdmission? next = _admission.Release();
      if (next is { IsAdmitted: true })
      {
        await AdmitParkedAsync(next.RecordId).ConfigureAwait(false);
      }
    }
    finally
    {
      lock (_gate)
      {
        _ = _inFlight.Remove(finished.Task);
      }

      _ = finished.TrySetResult();
    }
  }

  /// <summary>Starts a previously parked agent once a slot reaches it.
  ///     <para>NOTHING IS LAUNCHED ONCE TEARDOWN HAS BEGUN (issue #368): a slot released during
  ///     the walk would otherwise admit a park head whose host is spawned after the walk that
  ///     disposes hosts, which is a process nothing would ever ask to exit. The parked agent's
  ///     row stays as it was, so it is still resumable.</para></summary>
  private async Task AdmitParkedAsync(Guid recordId)
  {
    lock (_gate)
    {
      if (_disposed)
      {
        return;
      }
    }

    Result<AgentRecord> loaded = await _store.GetAsync(new AgentId(recordId), CancellationToken.None).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      // The record is gone: the park slot is released and the settle source completed so no
      // waiter hangs on an agent that cannot run.
      if (_settling.TryRemove(recordId, out TaskCompletionSource<AgentRunOutcome>? orphan))
      {
        _ = orphan.TrySetResult(new AgentRunOutcome(new AgentId(recordId), AgentStatus.Failed,
            AgentFailureReason.Interrupted, "Error [NotFound]: the parked agent's record no longer exists.",
            AgentRecord.UnassignedModel));
      }

      await RetireAsync(new AgentId(recordId)).ConfigureAwait(false);
      return;
    }

    _ = await LaunchAsync(loaded.Value, CancellationToken.None).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
  {
    if (!_settling.TryGetValue(id.Value, out TaskCompletionSource<AgentRunOutcome>? source))
    {
      return Result.Failure<AgentRunOutcome>(new DomainError("NotFound",
          $"agent '{id}' has no live run in this host."));
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

  /// <summary>Delivers one steering message to an agent THIS runtime launched (issue #330).
  ///     <para>THE CHILD'S OWN CONNECTION IS THE ONLY ONE THAT REACHES IT. A launched host serves
  ///     exactly one agent and this runtime holds the wire runtime attached to it, so forwarding
  ///     here addresses the process that actually runs the agent - which is the whole reason this
  ///     member exists. The caller's own channel speaks to the container this runtime lives in,
  ///     and that container holds no mailbox for a child it did not start.</para>
  ///     <para>AN AGENT THIS RUNTIME DID NOT LAUNCH IS REFUSED, never guessed at: a steer that
  ///     travelled to the wrong host would land in a mailbox nothing drains.</para></summary>
  /// <param name="agentId">The agent to steer.</param>
  /// <param name="text">The message text.</param>
  /// <param name="messageId">The identity the caller pinned its echo under.</param>
  /// <returns>The child host's own answer.</returns>
  public Task<Result<bool>> SteerAgentAsync(AgentId agentId, string text, Guid messageId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
    RemoteAgentRuntime? runtime = RuntimeFor(agentId);
    return runtime is null
        ? Task.FromResult(Result.Failure<bool>(new DomainError(RuntimeErrors.SteeringUnavailable,
            $"agent '{agentId}' has no live host in this container, so there is no mailbox to steer.")))
        : runtime.SteerAgentAsync(agentId, text, messageId);
  }

  /// <inheritdoc />
  public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
  {
    RemoteAgentRuntime? runtime = RuntimeFor(id);
    return runtime is null
        ? Task.FromResult(Result.Failure<AgentId>(new DomainError("NotRunning",
            $"agent '{id}' is not running in this host.")))
        : runtime.Resume(id, message, ct);
  }

  /// <inheritdoc />
  public void InterruptSubtree(AgentId rootOfSubtree) => RuntimeFor(rootOfSubtree)?.InterruptSubtree(rootOfSubtree);

  /// <summary>Interrupts one agent (or every agent this host launched). Cancelling a PARKED
  ///     agent withdraws it from the park queue and completes its settle source, so its waiter
  ///     never hangs on a run that will not happen.</summary>
  public void Interrupt(AgentId? agentId = null)
  {
    if (agentId is { } one)
    {
      RuntimeFor(one)?.Interrupt(one);
      if (_admission.Withdraw(one.Value) && _settling.TryRemove(one.Value, out TaskCompletionSource<AgentRunOutcome>? parked))
      {
        _ = parked.TrySetResult(new AgentRunOutcome(one, AgentStatus.Failed, AgentFailureReason.Interrupted,
            "Error [Interrupted]: the parked agent was cancelled before it started.",
            AgentRecord.UnassignedModel));
      }

      return;
    }

    Guid[] ids;
    lock (_gate)
    {
      ids = [.. _live.Keys];
    }

    foreach (Guid id in ids)
    {
      RuntimeFor(new AgentId(id))?.Interrupt(new AgentId(id));
    }
  }

  private RemoteAgentRuntime? RuntimeFor(AgentId id)
  {
    lock (_gate)
    {
      return _live.TryGetValue(id.Value, out LaunchedAgent? launched) ? launched.Runtime : null;
    }
  }

  private static TaskCompletionSource<AgentRunOutcome> NewSettleSource()
      => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private static string DefaultHostExePath()
      => ChildHostExeLocator.Resolve(
          ChildHostExeLocator.FindRepoRoot(typeof(HostLaunchingRuntime).Assembly.Location),
          AppContext.BaseDirectory);

  /// <summary>Disposes every host this runtime launched (container teardown). Each disposal
  ///     kills the agent's process and closes its job, so nothing outlives the container.
  ///     <para>TEARDOWN WAITS FOR THE WORK IT CAN SEE (issue #368). A host process exists before
  ///     its entry reaches the live set (a launch spawns it, then attaches) and outlives the
  ///     entry's removal (a retire disposes it after taking it out), so the live set alone is
  ///     not the whole answer: a launch or retire in flight is awaited here, and the live set is
  ///     re-read until nothing is left to dispose. Without that, teardown returned while a host
  ///     was still alive and the suite's leak guard found a process nothing had asked to exit.</para>
  ///     <para>NEW STARTS ARE REFUSED once this begins, so the loop cannot chase work that keeps
  ///     arriving.</para></summary>
  public async ValueTask DisposeAsync()
  {
    while (true)
    {
      Task[] inFlight;
      LaunchedAgent[] launched;
      lock (_gate)
      {
        _disposed = true;
        inFlight = [.. _inFlight];
        launched = [.. _live.Values];
        _live.Clear();
      }

      foreach (LaunchedAgent agent in launched)
      {
        await agent.Supervisor.DisposeAsync().ConfigureAwait(false);
      }

      if (inFlight.Length == 0)
      {
        return;
      }

      // Awaited OUTSIDE the gate: the work being awaited takes the gate to settle itself.
      await Task.WhenAll(inFlight).ConfigureAwait(false);
    }
  }

  /// <summary>The store this runtime reloads a parked agent's record from when its slot arrives.
  ///     Set by the container after construction (the store is a sibling registration, and the
  ///     runtime must not capture a service provider that may be disposed under it).</summary>
  public IAgentStore? Store { get; set; }

  private IAgentStore _store => Store ?? throw new InvalidOperationException(
      "HostLaunchingRuntime.Store must be assigned before a parked agent can be admitted.");

  /// <summary>One launched agent: its host process (with its job object) and the wire runtime
  ///     that talks to it.</summary>
  private sealed record LaunchedAgent(RemoteHostSupervisor Supervisor, RemoteAgentRuntime Runtime);
}
