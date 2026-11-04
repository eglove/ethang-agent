using System.Diagnostics;

namespace eThangAgent.Lsp.ACL;

/// <summary>One live connection to a workspace's language-server daemon (issue #361).
/// Holds the daemon process this app started (null when it joined a daemon another
/// instance already owns) and the pipe client every query rides on.</summary>
public sealed class LspDaemonConnection : IAsyncDisposable
{
  private static readonly TimeSpan JoinTimeout = TimeSpan.FromMilliseconds(250);
  private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

  private readonly Process? _process;
  private readonly LspPipeClient? _client;

  private LspDaemonConnection(Process? process, LspPipeClient? client)
  {
    _process = process;
    _client = client;
  }

  /// <summary>The connected pipe client, or null when no daemon answered.</summary>
  public LspPipeClient? Client => _client;

  /// <summary>The daemon process this app started, or null when it joined an existing one.</summary>
  public int? DaemonProcessId => _process is null || _process.HasExited ? null : _process.Id;

  /// <summary>Connects to the daemon for one workspace, starting it first when the pipe
  /// has no listener. Returns null when no daemon can be reached, which every caller
  /// treats as the silent Unavailable no-op.</summary>
  public static async Task<LspDaemonConnection?> ConnectAsync(
      string workspaceId, string workspaceRoot, string appInstanceId,
      Func<Process> startDaemon, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(appInstanceId);
    ArgumentNullException.ThrowIfNull(startDaemon);

    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    LspPipeClient? joined = await TryConnectAsync(pipeName, workspaceId, appInstanceId, JoinTimeout, ct).ConfigureAwait(false);
    if (joined is not null)
    {
      return new LspDaemonConnection(null, joined);
    }

    Process process;
    try
    {
      process = startDaemon();
    }
    // Named decision (CA1031): a daemon that cannot be started is the Unavailable
    // state, never a failure of the edit that asked for diagnostics.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return null;
    }
#pragma warning restore CA1031

    LspPipeClient? client = await TryConnectAsync(pipeName, workspaceId, appInstanceId, StartTimeout, ct).ConfigureAwait(false);
    if (client is null)
    {
      KillQuietly(process);
      return null;
    }

    return new LspDaemonConnection(process, client);
  }

  private static async Task<LspPipeClient?> TryConnectAsync(
      string pipeName, string workspaceId, string appInstanceId, TimeSpan timeout, CancellationToken ct)
  {
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout);
    try
    {
      return await LspPipeClient.ConnectAsync(pipeName, workspaceId, appInstanceId, cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      return null;
    }
    catch (TimeoutException)
    {
      return null;
    }
    catch (IOException)
    {
      return null;
    }
    catch (LspWorkspaceMismatchException)
    {
      return null;
    }
  }

  private static void KillQuietly(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch (InvalidOperationException)
    {
    }

    process.Dispose();
  }

  public ValueTask DisposeAsync()
  {
    _client?.Dispose();
    if (_process is not null)
    {
      KillQuietly(_process);
    }

    return ValueTask.CompletedTask;
  }
}
