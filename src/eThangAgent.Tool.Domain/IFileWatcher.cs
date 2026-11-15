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

/// <summary>One holder's subscription to a workspace's shared watcher (issue #388).
///     <para>A watcher outlives the sessions over its root - it is released when the last hold
///     goes, and stops then - so a subscriber cannot be handed the watcher itself: doing that
///     made every closed session's view-model hold the process-wide registry entry alive, and
///     the view-model owns the whole transcript. The subscription is the stake instead: it
///     reaches the watcher's own subscription semantics without naming the watcher.</para>
///     <para>A subscriber takes one and never disposes the watcher; disposing the subscription
///     is all it does.</para></summary>
public interface IFileWatcherSubscription
{
  /// <summary>Subscribes a handler invoked sequentially per changing pass. Disposing the
  ///     returned subscription stops further announcements to it.</summary>
  IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler);
}
