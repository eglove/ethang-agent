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

  /// <summary>The connected client, or null when no daemon is reachable.</summary>
  public LspPipeClient? Client => _connection?.Client;

  public int? DaemonProcessId => _connection?.DaemonProcessId;

  /// <summary>True once the workspace watcher is publishing to this daemon.</summary>
  public bool IsForwarding => _forwarder?.IsForwarding == true;

  /// <summary>Connects once per host and reuses the connection afterwards. Returns
  /// null when the workspace holds no code or no daemon can be reached; the caller then
  /// reports Unavailable. Detection runs once per host: a workspace that held no code at
  /// open is never re-scanned, so a vault or document workspace never starts a daemon.</summary>
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
      if (_connection is not null)
      {
        return _connection.Client;
      }

      _connection = await LspDaemonConnection.ConnectAsync(
          _workspaceId, _workspaceRoot, _appInstanceId, StartDaemon, ct).ConfigureAwait(false);
      LspPipeClient? client = _connection?.Client;
      if (client is null)
      {
        return null;
      }

      StartForwarding(client);
      return client;
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  private void StartForwarding(LspPipeClient client)
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

    _forwarder = new LspWatcherForwarder(watcher, _workspaceId, _ => Task.FromResult<LspPipeClient?>(client));
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
    _ = started.StandardOutput.ReadToEndAsync();
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
