namespace eThangAgent.ToolDomain;

/// <summary>Pub/sub surface for watcher announcements (issue #350). Subscribers are
///     invoked sequentially per pass; one throwing subscriber never blocks others.</summary>
public interface IFileWatcher
{
  /// <summary>The current generation: 0 at start, +1 per pass that found changes.</summary>
  long Generation { get; }

  /// <summary>Subscribes a handler invoked sequentially per changing pass.
  ///     Disposing the returned subscription stops further announcements to it.</summary>
  IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler);
}
