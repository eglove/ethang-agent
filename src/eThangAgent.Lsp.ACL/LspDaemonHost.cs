namespace eThangAgent.Lsp.ACL;

/// <summary>Per-workspace language-server daemon ownership (issue #361): resolves the
/// daemon executable, starts it when no daemon is listening, and holds the one
/// connection every advisory query rides on. A workspace with no code never reaches
/// this type, so no daemon and no language server is ever started there.</summary>
public sealed class LspDaemonHost : IDisposable, IAsyncDisposable
{
  private readonly string _workspaceId;
  private readonly string _workspaceRoot;
  private readonly string _appInstanceId;
  private readonly Func<string> _exePath;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private LspDaemonConnection? _connection;

  public LspDaemonHost(string workspaceId, string workspaceRoot, string appInstanceId, Func<string> exePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(appInstanceId);
    _workspaceId = workspaceId;
    _workspaceRoot = workspaceRoot;
    _appInstanceId = appInstanceId;
    _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
  }

  /// <summary>The connected client, or null when no daemon is reachable.</summary>
  public LspPipeClient? Client => _connection?.Client;

  public int? DaemonProcessId => _connection?.DaemonProcessId;

  /// <summary>Connects once per host and reuses the connection afterwards. Returns
  /// null when no daemon can be reached; the caller then reports Unavailable.</summary>
  public async Task<LspPipeClient?> EnsureConnectedAsync(CancellationToken ct = default)
  {
    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (_connection is not null)
      {
        return _connection.Client;
      }

      _connection = await LspDaemonConnection.ConnectAsync(
          _workspaceId, _workspaceRoot, _appInstanceId, StartDaemon, ct).ConfigureAwait(false);
      return _connection?.Client;
    }
    finally
    {
      _ = _gate.Release();
    }
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
    return System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("the language-server daemon could not be started");
  }

  /// <summary>Releases the daemon this host started. A container that disposes
  ///     synchronously uses this path; the connection's own teardown is synchronous.
  ///     </summary>
  public void Dispose()
  {
    _connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    _connection = null;
    _gate.Dispose();
  }

  public async ValueTask DisposeAsync()
  {
    if (_connection is not null)
    {
      await _connection.DisposeAsync().ConfigureAwait(false);
      _connection = null;
    }

    _gate.Dispose();
  }
}
