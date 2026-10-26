using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>Launches and supervises ONE conversation's out-of-process host (spec D6/D7):
///     every conversation runs in its own host process, so the pipe name and the scratch
///     folder both derive from the CONVERSATION id — two conversations in one workspace get
///     two hosts and two budgets instead of fighting over one pipe and one settings file.
///     Both derivations are stable across app restarts, so a relaunching app FINDS the
///     still-running host instead of spawning a second one. The supervisor writes the
///     settings JSON and scratch paths the host needs — including the conversation id as
///     <see cref="AgentSettings.SessionId"/>, the ONLY thing that lets the host resolve its
///     root runner (without it every root turn answers RootTurnUnavailable) — and detects
///     host death. <see cref="AttachAsync"/> launches the host when it is not already
///     running for this supervisor, connects, swaps the runtime's transport, starts the
///     settle pump, and returns the declared live set for exact orphan repair.
///     <para>CONTAINMENT, NOT RE-ATTACH (issue #274). The host is assigned to a kill-on-close
///     Job Object held by this process, so the kernel reaps it when the app dies — a clean
///     exit AND a crash alike. Closing the app is a decision to kill everything, and a
///     subagent that outlived a crash would be a process nobody is watching. That retires
///     spec R3.1's reuse probe: a host from a previous app run no longer exists to find, so
///     every attach launches a fresh one. Continuing a conversation is the Sessions
///     catalog's job — resume the persisted session, which is the durable record.</para></summary>
public sealed class RemoteHostSupervisor : IAsyncDisposable
{
  /// <summary>The startup bound (a CLOCK, never a line count): a host that has not
  ///     announced itself by now is treated as failed rather than waited on.</summary>
  internal static readonly TimeSpan StartedLineTimeout = TimeSpan.FromSeconds(60);

  /// <summary>The connect bound (a CLOCK): a pipe held by another instance must fail named.</summary>
  internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

  /// <summary>How long disposal waits for a killed host to actually exit (a CLOCK, never a
  ///     spin count). Bounded on purpose: a host that ignores the kill must not hang
  ///     teardown, and the kill-on-close job remains the backstop. It exists because
  ///     <c>Process.Kill</c> RETURNS BEFORE THE PROCESS IS GONE, so without it a caller
  ///     could observe a 'disposed' session's host still alive and serving turns.</summary>
  internal static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(10);

  /// <summary>How the host's started line resolved.</summary>
  internal enum StartedLineOutcome
  {
    /// <summary>The host printed its host-starting line.</summary>
    Announced,

    /// <summary>The process exited before announcing itself.</summary>
    Exited,

    /// <summary>The deadline elapsed while the process was still alive and silent.</summary>
    TimedOut,
  }
  private readonly string _settingsPath;
  private readonly string _databasePath;
  // The host process's working directory: its scratch folder, under the temp root.
  private readonly string _hostWorkingDirectory;
  private readonly Func<string> _hostExePath;
  private readonly Action<string> _reportNotice;
  // The current host's output tail (one supervisor serves one conversation, so the
  // tail is per conversation). Null until a host is started.
  private HostOutputTail? _hostOutputTail;

  // The kill-on-close job containing the current host (issue #274). Held for the
  // supervisor's lifetime: the kernel reaps the host when THIS process dies, which is the
  // crash guarantee. Zero when the job could not be created (degrades to explicit kill).
  private nint _hostJob;
  private Process? Host { get; set; }

  public RemoteHostSupervisor(string conversationId, string scratchDirectory,
      AgentSettings settings, string databasePath, Action<string> reportNotice)
      : this(conversationId, scratchDirectory, settings, databasePath, reportNotice, DefaultHostExePath)
  {
  }

  public RemoteHostSupervisor(string conversationId, string scratchDirectory,
      AgentSettings settings, string databasePath, Action<string> reportNotice, Func<string>? hostExePath)
  {
    ArgumentNullException.ThrowIfNull(settings);
    HostPipeName = PipeNameForConversation(conversationId);
    _ = Directory.CreateDirectory(scratchDirectory); // the settings file below needs the directory
    _settingsPath = Path.Combine(scratchDirectory, "childhost-settings.json");
    _databasePath = databasePath;
    _reportNotice = reportNotice;
    // null means the default resolution (the repo's live build output, else the copy
    // shipped next to the app) - never a null dereference.
    _hostExePath = hostExePath ?? DefaultHostExePath;
    // The workspace root rides the settings JSON (D): the host's ONLY carrier, and
    // SessionHost anchors its container there - without it the host anchors at the
    // settings-file directory and every tool path fails. It can no longer be inferred
    // from the pipe identity (that is the conversation now), so a missing root is a
    // named startup error rather than a silent wrong anchor.
    if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
    {
      throw new InvalidOperationException(
          "the host settings must carry an absolute WorkspaceRoot; the supervisor cannot infer it from the conversation id.");
    }

    // The host's WORKING DIRECTORY is its own SCRATCH folder, never the workspace: a
    // child process whose working directory sits INSIDE the workspace pins that
    // directory, and deletes over it fail while the child lives (the documented git
    // -C hazard, which this would have reproduced for the whole workspace). The
    // scratch folder lives under the temp root, so it can never pin the workspace.
    _hostWorkingDirectory = scratchDirectory;

    // The conversation id is the host's root-session binding (spec D6): SessionHost resolves
    // its root runner ONLY when SessionId is set, so this stamp is what makes a root turn
    // run host-side at all. Caller-supplied data is never silently overwritten.
    AgentSettings shipped = settings.SessionId is null
        ? settings.WithSessionId(conversationId)
        : settings;
    File.WriteAllText(_settingsPath, JsonSerializer.Serialize(shipped));
  }

  /// <summary>The pipe name for ONE conversation's host (spec D6/D7): derived from the
  ///     conversation id, so two conversations in the same workspace get two hosts and
  ///     two budgets. Stable across app restarts, so a relaunching app finds a live host
  ///     instead of spawning a second one.</summary>
  public static string PipeNameForConversation(string conversationId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
    return "ethang-conv-" + PipeSuffix(conversationId);
  }

  /// <summary>A filesystem-safe scratch folder name for one conversation id (D5).
  ///     Hashing is the point: ids are often ABSOLUTE PATHS (or carry path separators),
  ///     and a raw path segment would silently discard every earlier Path.Combine segment
  ///     (observed: the host's settings JSON landed inside the user's workspace).
  ///     Composition joins this under the temp root, so the scratch can never reach the
  ///     workspace it serves.</summary>
  public static string ScratchFolderForConversation(string conversationId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
    return "conv-" + PipeSuffix(conversationId);
  }

  /// <summary>The pipe name (exposed for tests and diagnostics).</summary>
  public string HostPipeName { get; }

  /// <summary>The host process's id while it is running, or null when no host is
  ///     live. The app observes each conversation's memory through this handle: every
  ///     conversation runs in its OWN host process (spec D6), so the host's working set
  ///     IS that conversation's memory. Null is an honest 'no host right now', never a
  ///     fabricated id - the observer then reports the conversation as absent rather
  ///     than counting some other process's memory as its own.
  ///     <para>The read is safe AFTER <see cref="DisposeAsync"/> (issue #287): a disposed
  ///     supervisor answers null, because a consumer sampling a closed session is exactly
  ///     the case this contract exists for.</para></summary>
  public int? HostProcessId
  {
    get
    {
      Process? host = Host;
      return host is null ? null : LiveProcessId(host);
    }
  }

  /// <summary>One process's id while it is live, or null when it has none to report.
  ///     <para>The <c>HasExited</c> probe is INSIDE the catch (issue #287), and that is the
  ///     whole fix: on a DISPOSED <see cref="Process"/> it throws
  ///     <c>InvalidOperationException</c> ("No process is associated with this object.")
  ///     instead of returning true, so a guard that probes it outside the catch cannot
  ///     short-circuit - the guard itself becomes the exception. Disposed, reaped mid-read,
  ///     and already-exited all answer the same honest null.</para></summary>
  private static int? LiveProcessId(Process host)
  {
    try
    {
      return host.HasExited ? null : host.Id;
    }
    catch (InvalidOperationException)
    {
      // Disposed, or reaped between the check and the read: both mean 'no host'.
      return null;
    }
  }

  /// <summary>Stops this conversation's host process (spec D3's kill rung), releasing the
  ///     host's own memory - the model runtimes, the exec engine, and everything else it
  ///     hosts. The host's children are contained with it (issue #274): they do not survive
  ///     to re-attach anywhere, and their persisted rows are repaired to Interrupted by the
  ///     next session open. Best effort: a host that is already gone is the desired end
  ///     state, not a failure.</summary>
  public void StopHost()
  {
    Process? host = Host;
    // LiveProcessId, not HasExited (issue #287): a DISPOSED Process throws from the probe,
    // so the guard would become the exception on exactly the path where 'already gone' is
    // the desired end state.
    if (host is null || LiveProcessId(host) is null)
    {
      return;
    }

    try
    {
      host.Kill(entireProcessTree: false);
    }
    catch (InvalidOperationException)
    {
      // The process exited between the check and the kill.
    }
  }

  /// <summary>Launches this conversation's host (when this supervisor has none running),
  ///     connects, and starts the runtime's settle pump. Returns the declared live set the
  ///     repair pass consumes.
  ///     <para>There is deliberately NO reuse probe (issue #274): containment reaps a host
  ///     when the app dies, so a host from a previous app run cannot exist. Probing for one
  ///     would spend a connect attempt to learn what containment already guarantees.</para></summary>
  public async Task<IReadOnlyCollection<Guid>> AttachAsync(RemoteAgentRuntime runtime, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(runtime);
    // A caller who cancelled asked for NO work: refuse before launching, so a cancelled
    // attach never starts a host process that nothing will ever connect to.
    ct.ThrowIfCancellationRequested();
    NamedPipeChildTransport transport = await StartAndConnectAsync(ct).ConfigureAwait(false);
    runtime.ReplaceTransport(transport);
    _ = runtime.RunReceiveLoopAsync(CancellationToken.None);

    // The host declares its live set immediately on accepting the connection; give the
    // pump a bounded moment to observe it before callers repair orphans.
    for (int i = 0; i < 50 && runtime.DeclaredLiveChildren.Count == 0; i++)
    {
      await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
    }

    return runtime.DeclaredLiveChildren;
  }

  /// <summary>Launches this conversation's host and connects to it. The connect is
  ///     DEADLINE-bounded: a pipe whose only instance is already held leaves
  ///     <c>ConnectAsync</c> retrying forever, so an unbounded connect is a hang waiting to
  ///     happen. The deadline turns it into a named failure the caller can act on.</summary>
  private async Task<NamedPipeChildTransport> StartAndConnectAsync(CancellationToken ct)
  {
    await EnsureHostAsync(ct).ConfigureAwait(false);
    return await ConnectBoundedAsync(ConnectTimeout, ct).ConfigureAwait(false);
  }

  /// <summary>Connects under a DEADLINE, failing NAMED when it elapses. This is the bound
  ///     the dump analysis found (review round 2, finding 1): <c>ConnectToHostAsync</c>
  ///     retries forever while another instance holds the single-instance pipe, so without
  ///     the deadline a contended connect is the suite-wide hang. The timeout is a
  ///     parameter so a test can drive it short against a pipe nothing serves - the
  ///     production caller always passes <see cref="ConnectTimeout"/>.</summary>
  internal async Task<NamedPipeChildTransport> ConnectBoundedAsync(TimeSpan timeout, CancellationToken ct)
  {
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(timeout);
    try
    {
      return await NamedPipeChildTransport.ConnectToHostAsync(HostPipeName, deadline.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      throw new InvalidOperationException(
          $"could not connect to this conversation's host pipe '{HostPipeName}' within {timeout:c}; "
          + "another host instance likely holds it.");
    }
  }

  /// <summary>Starts the host when absent; a dead host from a previous app lifetime is
  ///     restarted here. Host death while attached is surfaced as a session notice. A host
  ///     that starts but never announces itself is killed and reported as a NAMED startup
  ///     failure: every conversation attaches now (spec D6/D7), so a silent host is a
  ///     routine condition to fail on, never a hang.</summary>
  private async Task EnsureHostAsync(CancellationToken ct)
  {
    // A tolerant liveness probe (issue #287): the previous host may have been DISPOSED by
    // a failed startup below, and HasExited on a disposed Process throws - so a retry would
    // fault in the guard instead of starting a fresh host.
    if (Host is { } existing && LiveProcessId(existing) is not null)
    {
      return;
    }

    Process started = StartHostProcess(ct);
    StartedLineOutcome outcome = await WaitForStartedLineAsync(started, ct).ConfigureAwait(false);
    if (outcome is StartedLineOutcome.Announced)
    {
      // The started line is read; from here on the host's output is DRAINED. An
      // unread pipe fills (64KB) and then blocks the host forever, and the host's
      // diagnostics are lost - the failure that made a published host's missing
      // dependency manifest invisible (2026-10-22). The drain starts HERE because
      // the started line and the drain would otherwise compete for the same stream.
      if (_hostOutputTail is { } tail)
      {
        _ = tail.DrainAsync(started);
      }

      return;
    }

    // Not announced: the host is unusable either way, and leaving a live-but-silent one
    // running would strand a process retrying against a pipe nobody dials. The reference is
    // cleared BEFORE the dispose for the same reason DisposeAsync clears it (issue #287): a
    // disposed Process left assigned throws from every later liveness probe.
    Host = null;
    KillQuietly(started);
    throw outcome is StartedLineOutcome.Exited
        ? new InvalidOperationException("child host exited during startup.")
        : new InvalidOperationException(
            $"child host did not announce itself within {StartedLineTimeout:c} of starting; "
            + "it is alive but its stdout never carried a host-starting line.");
  }

  /// <summary>Starts the host process for this conversation, records it as the current host,
  ///     and arms the death notice. A missing executable and a failed start are named
  ///     startup failures.</summary>
  /// <summary>Builds the host process's start info. The WORKING DIRECTORY is the
  ///     host's SCRATCH folder (under the temp root), never the workspace: a child
  ///     process whose working directory sits inside the workspace PINS that
  ///     directory, and deletes over it fail while the child lives - the documented
  ///     git -C hazard, which this would have reproduced for the whole workspace
  ///     (2026-10-22). The scratch folder is the same place the host's settings JSON
  ///     lives, so it is always present.</summary>
  internal static ProcessStartInfo BuildStartInfo(string exe, string pipeName, string settingsPath, string databasePath, string workingDirectory)
  {
    ProcessStartInfo psi = new(exe);
    psi.ArgumentList.Add(pipeName);
    psi.ArgumentList.Add(settingsPath);
    psi.ArgumentList.Add(databasePath);
    if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
    {
      psi.WorkingDirectory = workingDirectory;
    }

    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    psi.CreateNoWindow = true;
    return psi;
  }

  private Process StartHostProcess(CancellationToken ct)
  {
    string exe = _hostExePath();
    if (!File.Exists(exe))
    {
      throw new InvalidOperationException("child host executable not found at '" + exe + "'.");
    }

    ProcessStartInfo psi = BuildStartInfo(exe, HostPipeName, _settingsPath, _databasePath, _hostWorkingDirectory);
    Process started = Process.Start(psi) ?? throw new InvalidOperationException("child host failed to start.");

    // CONTAINMENT (issue #274): the host joins a kill-on-close job whose handle this
    // process holds, so the kernel reaps it when the app dies - a clean exit AND a crash
    // alike. Best-effort: a job that cannot be created leaves the explicit-kill paths
    // (dispose, StopHost) as the only guarantee, which still covers a clean exit.
    _hostJob = JobObject.Create();
    if (_hostJob != nint.Zero && JobObject.Configure(_hostJob))
    {
      _ = JobObject.Attach(_hostJob, started);
    }

    Host = started;
    HostOutputTail tail = new();
    _hostOutputTail = tail;
    _ = started.WaitForExitAsync(ct).ContinueWith((t, state) =>
    {
      // Host-health notice (R3.3): death of the host is surfaced, never silent.
      if (state is Action<string> report && !t.IsCanceled)
      {
        report("[host] the child host process exited; the children it ran were contained with it and are marked interrupted."
            + tail.Describe());
      }
    }, _reportNotice, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    return started;
  }

  /// <summary>The bounded tail of ONE host's output (one supervisor serves one
  ///     conversation, so the tail is per conversation). A fixed capacity, because the
  ///     tail exists to explain a death - not to archive a log.</summary>
  private sealed class HostOutputTail
  {
    /// <summary>How many lines per stream are kept.</summary>
    private const int Capacity = 40;

    /// <summary>The host's standard output, most recent last.</summary>
    private readonly List<string> _standard = [];

    /// <summary>The host's standard error, most recent last.</summary>
    private readonly List<string> _error = [];

    /// <summary>Reads the host's output to its end, keeping the last
    ///     <see cref="Capacity"/> lines per stream. The read ends when the host closes
    ///     the stream (its exit). The started line is read BEFORE this starts, so the
    ///     two never compete for the same stream.</summary>
    internal async Task DrainAsync(Process started)
    {
      await Task.WhenAll(Drain(started.StandardOutput, _standard), Drain(started.StandardError, _error))
          .ConfigureAwait(false);
    }

    private static async Task Drain(StreamReader reader, List<string> tail)
    {
      ArgumentNullException.ThrowIfNull(reader);
      ArgumentNullException.ThrowIfNull(tail);
      while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
      {
        lock (tail)
        {
          tail.Add(line);
          if (tail.Count > Capacity)
          {
            tail.RemoveRange(0, tail.Count - Capacity);
          }
        }
      }
    }

    /// <summary>The tail as one line, or an empty string when the host said nothing on
    ///     standard error (its diagnostics channel).</summary>
    internal string Describe()
    {
      List<string> lines;
      lock (_error)
      {
        lines = [.. _error];
      }

      return lines.Count == 0 ? string.Empty : " Last output: " + string.Join(" | ", lines);
    }
  }

  /// <summary>Waits for the host's started line under a DEADLINE, never a line counter.
  ///     The counter shape this replaced could not bound anything: the first read was
  ///     awaited BEFORE the loop bound could advance, so a host that printed nothing
  ///     blocked the attach forever (a counter is not a clock).</summary>
  private static Task<StartedLineOutcome> WaitForStartedLineAsync(Process started, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(started);
    return AwaitStartedLineAsync(
        started.StandardOutput.ReadLineAsync,
        () => started.HasExited,
        StartedLineTimeout,
        ct);
  }

  /// <summary>The deadline-bounded wait itself, over a caller-supplied line source so the
  ///     bound is testable without a real process. A null line means stdout closed - the
  ///     host will never announce itself, so that resolves at once instead of spinning.
  ///     The deadline, not the caller's token, is what ends a read that never returns.</summary>
  internal static async Task<StartedLineOutcome> AwaitStartedLineAsync(
      Func<CancellationToken, ValueTask<string?>> readLine,
      Func<bool> hasExited,
      TimeSpan timeout,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(readLine);
    ArgumentNullException.ThrowIfNull(hasExited);
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(timeout);
    try
    {
      while (!hasExited())
      {
        string? line = await readLine(deadline.Token).ConfigureAwait(false);
        if (line is null)
        {
          return hasExited() ? StartedLineOutcome.Exited : StartedLineOutcome.TimedOut;
        }

        if (line.StartsWith("host-starting", StringComparison.Ordinal))
        {
          return StartedLineOutcome.Announced;
        }
      }

      return StartedLineOutcome.Exited;
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      // The DEADLINE elapsed, not the caller: the host is alive and silent.
      return StartedLineOutcome.TimedOut;
    }
  }

  /// <summary>Best-effort kill of a host that will never serve: it is already a failure,
  ///     so cleanup must never mask the named startup error the caller is about to see.
  ///     <para>Also the DISPOSAL path's teardown (issue #287), where the same shape is what
  ///     makes that contract true: a running host is killed, an already-exited one is
  ///     tolerated, and the Process handle is DISPOSED in both cases - leaving an exited
  ///     host's handle open was the second half of that defect.</para></summary>
  private static void KillQuietly(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
        // Kill() only ASKS. Waiting for the exit here is what makes 'disposal' mean
        // 'the host is gone' rather than 'the kill was requested' - see DisposeAsync.
        // Bounded so a wedged host cannot hang teardown; a timeout is not a failure,
        // because the kill-on-close job still reaps it with this process.
        _ = process.WaitForExit((int)ExitWaitTimeout.TotalMilliseconds);
      }
    }
    // Named decision (CA1031, S108, S2486): the process is already written off, so a
    // kill failure has nothing left to act on - the named startup error follows either way.
#pragma warning disable CA1031, S108, S2486 // Do not catch general exception types
    catch (Exception)
    {
    }
#pragma warning restore CA1031, S108, S2486
    process.Dispose();
  }

  /// <summary>Default exe resolution: the locator picks the host that ships NEXT TO
  ///     the app (the same build, which the wire protocol requires), and falls back to
  ///     the repo's live build output (configuration-agnostic: Debug first, Release
  ///     fallback) for a caller that ships no host of its own. A DEPLOYED app has no
  ///     repo above it, so the walk ends at the filesystem root with no repo: that is an
  ///     ORDINARY case, and the host is then the copy shipped NEXT TO THE APP.
  ///     <para>The null repo was dereferenced as <c>dir!.FullName</c> — the
  ///     NullReferenceException that killed the app on workspace open (2026-10-21). A host
  ///     that cannot be found is a NAMED startup error from
  ///     <see cref="StartHostProcess"/>, never an exception out of this method.</para></summary>
  private static string DefaultHostExePath() =>
      ResolveDefaultHostExePath(typeof(RemoteHostSupervisor).Assembly.Location, AppContext.BaseDirectory);

  /// <summary>The resolution itself, over injected inputs so a test can reproduce the
  ///     DEPLOYED layout (an app that lives outside any repo) without a deployment.
  ///     A null repo root is the ordinary answer for a published app, never a fault.</summary>
  internal static string ResolveDefaultHostExePath(string assemblyLocation, string appBaseDirectory) =>
      ChildHostExeLocator.Resolve(
          ChildHostExeLocator.FindRepoRoot(assemblyLocation), appBaseDirectory);

  /// <summary>A stable, filesystem-safe pipe suffix for one conversation id (D6/D7).</summary>
  private static string PipeSuffix(string conversationId)
  {
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
    return Convert.ToHexString(hash, 0, 8);
  }

  public async ValueTask DisposeAsync()
  {
    // The reference is CLEARED before the process is touched (issue #287): a disposed
    // Process throws InvalidOperationException from HasExited, so a reference left
    // assigned turns every later read of HostProcessId - a public property whose whole
    // contract is an honest null - into an exception. Clearing first also makes a second
    // disposal a no-op instead of a fault.
    Process? host = Host;
    Host = null;

    // The app owns the host's lifetime: on shutdown the host goes too (its children
    // were the app's children; leaving it would orphan them against nothing).
    // KillQuietly is exactly the shape this needs: it kills a running host, tolerates one
    // that already exited, and DISPOSES the Process either way - the handle must be
    // released in BOTH cases (an exited host used to keep an undisposed handle for the
    // supervisor's lifetime), and a teardown must never turn container disposal into a
    // fault at shutdown.
    //
    // DISPOSAL DOES NOT COMPLETE UNTIL THE HOST IS GONE (issue #284 follow-up).
    // Kill() is a REQUEST: it returns as soon as the kernel accepts it, while the
    // process keeps running - and keeps holding its SQLite file, its pipe and its
    // scratch folder - for a window afterwards (measured on a loaded machine: a
    // test-spawned host stayed in the process table for seconds, still serving turns,
    // after its container's disposal had returned). A caller that treated 'dispose' as
    // 'the host is gone' then observed a live process it thought it had killed, and the
    // suite's leak guard reported a correctly finished test as a leak. The wait below
    // is bounded, so a host that ignores the kill cannot hang teardown.
    if (host is not null)
    {
      KillQuietly(host);
    }

    // Closing the kill-on-close job reaps anything the explicit kill missed (a host that
    // raced the HasExited check, or a descendant that escaped the tree walk). The handle
    // is closed on EVERY disposal path, because an open handle pins the job.
    if (_hostJob != nint.Zero)
    {
      _ = JobObject.Close(_hostJob);
      _hostJob = nint.Zero;
    }

    await ValueTask.CompletedTask.ConfigureAwait(false);
  }
}
