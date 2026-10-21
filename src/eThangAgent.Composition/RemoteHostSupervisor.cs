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
///     host death. <see cref="AttachAsync"/> is the R3.1 re-attach entry: it connects to the
///     pipe, swaps the runtime's transport, starts the settle pump, and returns the freshly
///     declared live set for exact orphan repair. A host that died is restarted on attach —
///     its absence is a condition to repair, never a hang.</summary>
public sealed class RemoteHostSupervisor : IAsyncDisposable
{
  /// <summary>The startup bound (a CLOCK, never a line count): a host that has not
  ///     announced itself by now is treated as failed rather than waited on.</summary>
  internal static readonly TimeSpan StartedLineTimeout = TimeSpan.FromSeconds(60);

  /// <summary>The connect bound (a CLOCK): a pipe held by another instance must fail named.</summary>
  internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

  /// <summary>The reuse probe (a CLOCK, and deliberately short): how long to look for a
  ///     host already serving this conversation before launching one.</summary>
  internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

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
  private readonly Func<string> _hostExePath;
  private readonly Action<string> _reportNotice;
  private Process? Host { get; set; }

  public RemoteHostSupervisor(string conversationId, string scratchDirectory,
      AgentSettings settings, string databasePath, Action<string> reportNotice)
      : this(conversationId, scratchDirectory, settings, databasePath, reportNotice, DefaultHostExePath)
  {
  }

  public RemoteHostSupervisor(string conversationId, string scratchDirectory,
      AgentSettings settings, string databasePath, Action<string> reportNotice, Func<string> hostExePath)
  {
    ArgumentNullException.ThrowIfNull(settings);
    HostPipeName = PipeNameForConversation(conversationId);
    _ = Directory.CreateDirectory(scratchDirectory); // the settings file below needs the directory
    _settingsPath = Path.Combine(scratchDirectory, "childhost-settings.json");
    _databasePath = databasePath;
    _reportNotice = reportNotice;
    _hostExePath = hostExePath;
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
  ///     than counting some other process's memory as its own.</summary>
  public int? HostProcessId
  {
    get
    {
      Process? host = Host;
      if (host is null || host.HasExited)
      {
        return null;
      }

      try
      {
        return host.Id;
      }
      catch (InvalidOperationException)
      {
        // The process was reaped between the check and the read.
        return null;
      }
    }
  }

  /// <summary>Stops this conversation's host process (spec D3's kill rung). The host's
  ///     children keep running - they re-attach to the next host that serves this
  ///     conversation - but the host's OWN memory (the model runtimes, the exec engine,
  ///     and everything else it hosts) is released. Best effort: a host that is already
  ///     gone is the desired end state, not a failure.</summary>
  public void StopHost()
  {
    Process? host = Host;
    if (host is null || host.HasExited)
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

  /// <summary>Connects (or re-connects) to the host's pipe and starts the runtime's
  ///     settle pump. Returns the declared live set the repair pass consumes.</summary>
  public async Task<IReadOnlyCollection<Guid>> AttachAsync(RemoteAgentRuntime runtime, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(runtime);
    // FIND before spawning (the class contract): a host already serving this
    // conversation - the one a resumed session's PREVIOUS container launched, or one a
    // relaunching app left running - is reused. Spawning a second host would collide on
    // the single-instance pipe and leave the connect retrying forever.
    NamedPipeChildTransport transport = await TryConnectAsync(ProbeTimeout, ct).ConfigureAwait(false)
        ?? await StartAndConnectAsync(ct).ConfigureAwait(false);
    runtime.ReplaceTransport(transport);
    _ = runtime.RunReceiveLoopAsync(CancellationToken.None);

    // The host declares its live set immediately on accepting the connection (R3.1);
    // give the pump a bounded moment to observe it before callers repair orphans.
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

  /// <summary>Test seam over <see cref="TryConnectAsync"/>: the reuse probe's bound is a
  ///     CLOCK, and a test must be able to drive it with a short deadline against a pipe
  ///     nothing serves. Production always probes with <see cref="ProbeTimeout"/>.</summary>
  internal Task<NamedPipeChildTransport?> ProbeForTestAsync(TimeSpan timeout, CancellationToken ct) =>
      TryConnectAsync(timeout, ct);
  /// <summary>Connects to an ALREADY-RUNNING host, or null when none answers inside the
  ///     probe window. A probe, not a failure path: absence is the ordinary cold-start
  ///     answer and must never surface as an error.</summary>
  private async Task<NamedPipeChildTransport?> TryConnectAsync(TimeSpan timeout, CancellationToken ct)
  {
    // A caller who cancelled asked for NO work: refuse before probing, so a cancelled
    // attach never reaches StartAndConnectAsync and never launches a host process
    // (review finding 9: the broad catch below swallowed the cancellation and fell
    // through to a spawn).
    ct.ThrowIfCancellationRequested();
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(timeout);
    try
    {
      return await NamedPipeChildTransport.ConnectToHostAsync(HostPipeName, deadline.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      // The CALLER cancelled: propagate, never treat it as "no host here".
      throw;
    }
    // Named decision (CA1031): no host answering inside the probe window is the expected
    // cold-start answer - the caller's own cancellation is handled above, not here.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>Starts the host when absent; a dead host from a previous app lifetime is
  ///     restarted here. Host death while attached is surfaced as a session notice. A host
  ///     that starts but never announces itself is killed and reported as a NAMED startup
  ///     failure: every conversation attaches now (spec D6/D7), so a silent host is a
  ///     routine condition to fail on, never a hang.</summary>
  private async Task EnsureHostAsync(CancellationToken ct)
  {
    if (Host is { HasExited: false })
    {
      return;
    }

    Process started = StartHostProcess(ct);
    StartedLineOutcome outcome = await WaitForStartedLineAsync(started, ct).ConfigureAwait(false);
    if (outcome is StartedLineOutcome.Announced)
    {
      return;
    }

    // Not announced: the host is unusable either way, and leaving a live-but-silent one
    // running would strand a process retrying against a pipe nobody dials.
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
  private Process StartHostProcess(CancellationToken ct)
  {
    string exe = _hostExePath();
    if (!File.Exists(exe))
    {
      throw new InvalidOperationException("child host executable not found at '" + exe + "'.");
    }

    ProcessStartInfo psi = new(exe);
    psi.ArgumentList.Add(HostPipeName);
    psi.ArgumentList.Add(_settingsPath);
    psi.ArgumentList.Add(_databasePath);
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    psi.CreateNoWindow = true;
    Process started = Process.Start(psi) ?? throw new InvalidOperationException("child host failed to start.");
    Host = started;
    _ = started.WaitForExitAsync(ct).ContinueWith(static (t, state) =>
    {
      // Host-health notice (R3.3): death of the host is surfaced, never silent.
      if (state is Action<string> report && !t.IsCanceled)
      {
        report("[host] the child host process exited; children it ran keep running and will re-attach on the next connection.");
      }
    }, _reportNotice, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    return started;
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
  ///     so cleanup must never mask the named startup error the caller is about to see.</summary>
  private static void KillQuietly(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
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

  /// <summary>Default exe resolution: walk up from the executing assembly to the repo
  ///     root (mirrors the E2E helper), then ask the locator for the host's build
  ///     output (configuration-agnostic: Debug first, Release fallback).</summary>
  private static string DefaultHostExePath()
  {
    DirectoryInfo? dir = new(typeof(RemoteHostSupervisor).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return ChildHostExeLocator.ResolveFromRepoRoot(dir!.FullName);
  }

  /// <summary>A stable, filesystem-safe pipe suffix for one conversation id (D6/D7).</summary>
  private static string PipeSuffix(string conversationId)
  {
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
    return Convert.ToHexString(hash, 0, 8);
  }

  public async ValueTask DisposeAsync()
  {
    if (Host is { HasExited: false })
    {
      // The app owns the host's lifetime: on shutdown the host goes too (its children
      // were the app's children; leaving it would orphan them against nothing).
      Host.Kill(entireProcessTree: true);
      Host.Dispose();
    }

    await ValueTask.CompletedTask.ConfigureAwait(false);
  }
}
