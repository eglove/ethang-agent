using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition;

/// <summary>Launches and supervises the out-of-process child host (R3.3): derives the
///     pipe name from the workspace id (stable across app restarts so a relaunching
///     app FINDS the still-running host instead of spawning a second one), writes the
///     settings JSON and scratch paths the host needs, and detects host death.
///     <see cref="AttachAsync"/> is the R3.1 re-attach entry: it connects to the pipe,
///     swaps the runtime's transport, starts the settle pump, and returns the freshly
///     declared live set for exact orphan repair. A host that died is restarted on
///     attach — its absence is a condition to repair, never a hang.</summary>
public sealed class RemoteHostSupervisor : IAsyncDisposable
{
  private readonly string _settingsPath;
  private readonly string _databasePath;
  private readonly Func<string> _hostExePath;
  private readonly Action<string> _reportNotice;
  private Process? Host { get; set; }

  public RemoteHostSupervisor(string workspaceId, string scratchDirectory,
      AgentSettings settings, string databasePath, Action<string> reportNotice)
      : this(workspaceId, scratchDirectory, settings, databasePath, reportNotice, DefaultHostExePath)
  {
  }

  public RemoteHostSupervisor(string workspaceId, string scratchDirectory,
      AgentSettings settings, string databasePath, Action<string> reportNotice, Func<string> hostExePath)
  {
    HostPipeName = "ethang-host-" + PipeSuffix(workspaceId);
    _settingsPath = Path.Combine(scratchDirectory, "childhost-settings.json");
    _databasePath = databasePath;
    _reportNotice = reportNotice;
    _hostExePath = hostExePath;
    File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
  }

  /// <summary>The pipe name (exposed for tests and diagnostics).</summary>
  public string HostPipeName { get; }

  /// <summary>Connects (or re-connects) to the host's pipe and starts the runtime's
  ///     settle pump. Returns the declared live set the repair pass consumes.</summary>
  public async Task<IReadOnlyCollection<Guid>> AttachAsync(RemoteAgentRuntime runtime, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(runtime);
    await EnsureHostAsync(ct).ConfigureAwait(false);
    NamedPipeChildTransport transport = await NamedPipeChildTransport.ConnectToHostAsync(HostPipeName, ct).ConfigureAwait(false);
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

  /// <summary>Starts the host when absent; a dead host from a previous app lifetime is
  ///     restarted here. Host death while attached is surfaced as a session notice.</summary>
  private async Task EnsureHostAsync(CancellationToken ct)
  {
    if (Host is { HasExited: false })
    {
      return;
    }

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

    // The host prints a started line once its pipe exists; bounded wait per deadlock vigilance.
    for (int i = 0; i < 100 && !started.HasExited; i++)
    {
      string? line = await started.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
      if (line is not null && line.StartsWith("host-starting", StringComparison.Ordinal))
      {
        return;
      }
    }

    started.Refresh();
    if (started.HasExited)
    {
      throw new InvalidOperationException("child host exited during startup.");
    }
  }

  /// <summary>Default exe resolution: walk up from the executing assembly to the repo root
  ///     (mirrors the E2E helper), then into the host's build output.</summary>
  private static string DefaultHostExePath()
  {
    DirectoryInfo? dir = new(typeof(RemoteHostSupervisor).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return Path.Combine(dir!.FullName, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0", "eThangAgent.ChildHost.exe");
  }

  /// <summary>A stable, filesystem-safe pipe suffix for the workspace id (R3.3).</summary>
  private static string PipeSuffix(string workspaceId)
  {
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId));
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
