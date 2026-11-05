using eThangAgent.ToolDomain;

namespace eThangAgent.Lsp.ACL;

/// <summary>Per-workspace language-server daemon ownership: resolves the daemon
/// executable, starts it when no daemon is listening, and holds the one connection
/// every advisory query, symbol query, claim, and watcher announcement rides on.
/// Connecting starts the workspace watcher's forwarding subscription, so a workspace
/// whose daemon is never reached also never starts a watcher.</summary>
public sealed class LspDaemonHost : IDisposable, IAsyncDisposable
{
  private readonly string _workspaceId;
  private readonly string _workspaceRoot;
  private readonly string _appInstanceId;
  private readonly Func<string> _exePath;
  private readonly Func<string, IFileWatcher?> _watcherFactory;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private LspDaemonConnection? _connection;
  private LspWatcherForwarder? _forwarder;
  private bool? _hasCode;

  public LspDaemonHost(
      string workspaceId,
      string workspaceRoot,
      string appInstanceId,
      Func<string> exePath,
      Func<string, IFileWatcher?>? watcherFactory = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(appInstanceId);
    _workspaceId = workspaceId;
    _workspaceRoot = workspaceRoot;
    _appInstanceId = appInstanceId;
    _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
    _watcherFactory = watcherFactory ?? (_ => null);
  }

  /// <summary>What the daemon announced it is doing with the language server: no
  ///     announcement yet, downloading, ready, or unavailable.
  ///     <para>PRESENT FROM CONSTRUCTION, not created when a daemon starts: a reader binds
  ///     this before the workspace has done anything, and the first announcement has to land
  ///     somewhere it is already watching.</para></summary>
  public LspServerStatusMonitor ServerStatus { get; } = new();

  /// <summary>The connected client, or null when no daemon is reachable.</summary>
  public LspPipeClient? Client => _connection?.Client;

  public int? DaemonProcessId => _connection?.DaemonProcessId;

  /// <summary>True once the workspace watcher is publishing to this daemon.</summary>
  public bool IsForwarding => _forwarder?.IsForwarding == true;

  /// <summary>Returns a client for the workspace daemon, connecting when there is no
  /// usable connection. A connection whose daemon has exited is discarded and replaced
  /// rather than handed out, so a daemon restart is recovered on the next query instead
  /// of failing every query for the life of the process. Returns null when the workspace
  /// holds no code or no daemon can be reached; the caller then reports Unavailable.
  /// Detection runs once per host: a workspace that held no code at open is never
  /// re-scanned, so a vault or document workspace never starts a daemon.</summary>
  public async Task<LspPipeClient?> EnsureConnectedAsync(CancellationToken ct = default)
  {
    _hasCode ??= CodeDetector.Detect(_workspaceRoot) != CodeDetectionResult.NotFound;
    if (_hasCode == false)
    {
      return null;
    }

    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (_connection is not null && _connection.IsAlive)
      {
        return _connection.Client;
      }

      await DiscardConnectionAsync().ConfigureAwait(false);
      _connection = await LspDaemonConnection.ConnectAsync(
          _workspaceId, _workspaceRoot, _appInstanceId, StartDaemon, ct).ConfigureAwait(false);
      LspPipeClient? client = _connection?.Client;
      if (client is null)
      {
        return null;
      }

      StartForwarding();
      return client;
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  /// <summary>Drops the current connection, releasing the daemon this host started.
  ///     Called when the daemon behind it is gone, so the next query reconnects.</summary>
  private async Task DiscardConnectionAsync()
  {
    LspDaemonConnection? dead = _connection;
    _connection = null;
    if (dead is not null)
    {
      await dead.DisposeAsync().ConfigureAwait(false);
    }
  }

  private void StartForwarding()
  {
    if (_forwarder is not null)
    {
      return;
    }

    IFileWatcher? watcher = _watcherFactory(_workspaceRoot);
    if (watcher is null)
    {
      return;
    }

    // The forwarder resolves the current client per announcement: capturing the client
    // here would pin the watcher to a daemon that a reconnect has since replaced.
    _forwarder = new LspWatcherForwarder(watcher, _workspaceId, EnsureConnectedAsync);
    _forwarder.Start();
  }

  private System.Diagnostics.Process StartDaemon()
  {
    System.Diagnostics.ProcessStartInfo psi = new(_exePath())
    {
      CreateNoWindow = true,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    psi.ArgumentList.Add(LspPipeNames.ForWorkspaceId(_workspaceId));
    psi.ArgumentList.Add(_workspaceId);
    psi.ArgumentList.Add(_workspaceRoot);
    System.Diagnostics.Process started = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("the language-server daemon could not be started");
    // The daemon announces its install work on stdout, so the drain feeds the monitor
    // rather than discarding what it reads. A redirected stream nobody drains would fill
    // its pipe buffer and block the daemon, so the read is required either way.
    _ = LspDaemonOutputPump.DrainAsync(started.StandardOutput, ServerStatus);
    _ = started.StandardError.ReadToEndAsync();
    return started;
  }

  /// <summary>Releases the daemon this host started and stops forwarding watcher
  ///     announcements to it.</summary>
  public void Dispose()
  {
    _forwarder?.Dispose();
    _forwarder = null;
    _connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    _connection = null;
    _gate.Dispose();
  }

  public async ValueTask DisposeAsync()
  {
    _forwarder?.Dispose();
    _forwarder = null;
    if (_connection is not null)
    {
      await _connection.DisposeAsync().ConfigureAwait(false);
      _connection = null;
    }

    _gate.Dispose();
  }
}
