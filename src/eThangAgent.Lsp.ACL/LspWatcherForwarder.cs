using eThangAgent.ToolDomain;

namespace eThangAgent.Lsp.ACL;

/// <summary>Publishes the workspace watcher's announcements to the daemon pipe so the
/// daemon can drive document sync on the language server. The watcher runs in the app
/// process; this is the leg that carries its announcements across the pipe. A pipe
/// that is unreachable drops the announcement and the next watcher pass re-sends the
/// change, so a daemon restart never loses a file permanently.</summary>
public sealed class LspWatcherForwarder : IDisposable
{
  private readonly IFileWatcher _watcher;
  private readonly string _workspaceId;
  private readonly Func<CancellationToken, Task<LspPipeClient?>> _clientFactory;
  private IDisposable? _subscription;

  public LspWatcherForwarder(
      IFileWatcher watcher,
      string workspaceId,
      Func<CancellationToken, Task<LspPipeClient?>> clientFactory)
  {
    _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    _workspaceId = workspaceId;
  }

  /// <summary>True once the watcher subscription is live.</summary>
  public bool IsForwarding => _subscription is not null;

  /// <summary>Subscribes to the watcher. Idempotent: a second call is a no-op.</summary>
  public void Start() => _subscription ??= _watcher.Subscribe(PublishAsync);

  private async Task PublishAsync(FileChangeAnnouncement announcement)
  {
    ArgumentNullException.ThrowIfNull(announcement);
    LspPipeClient? client = await _clientFactory(CancellationToken.None).ConfigureAwait(false);
    if (client is null)
    {
      return;
    }

    WatcherAnnouncement wire = new(
        _workspaceId,
        announcement.Paths,
        announcement.DeletedPaths,
        announcement.Generation,
        announcement.UtcTimestamp);
    await client.SendWatcherAnnouncementAsync(wire, CancellationToken.None).ConfigureAwait(false);
  }

  public void Dispose()
  {
    _subscription?.Dispose();
    _subscription = null;
  }
}
