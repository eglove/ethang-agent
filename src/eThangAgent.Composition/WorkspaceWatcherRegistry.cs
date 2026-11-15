using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Every workspace watcher this process holds, keyed by workspace root. Sessions over
///     one root share one watcher, and the watcher stops when the last of them gives its hold
///     back; a root nothing holds is forgotten.
///     <para>Acquiring a hold does not start the watcher: starting is the holder's decision
///     (<see cref="WorkspaceWatcherLease.EnsureStarted"/>), and it is idempotent.</para>
///     <para>A HOLD OWNS A SUBSCRIBER. Everything a session subscribes to the shared watcher is
///     dropped when that session's hold goes back, so a closed session stops being called for a
///     watcher the next session over the same root keeps running (issue #388). Without that the
///     registration was a stake in nothing: <c>Holders</c> counted leases while the subscriber
///     list kept every session that had ever subscribed, and a view-model lives in that list.</para>
///     </summary>
public static class WorkspaceWatcherRegistry
{
  private static readonly Dictionary<string, Entry> Held = [with(StringComparer.OrdinalIgnoreCase)];
  private static readonly Lock Gate = new();

  /// <summary>Holds the watcher for <paramref name="workspaceRoot"/>, creating it on the first
  ///     hold. The returned lease is the caller's hold: disposing it gives the hold back, and
  ///     the last release stops and forgets the watcher.</summary>
  /// <param name="workspaceRoot">The directory to watch; the key is its full path.</param>
  /// <param name="snapshots">The walker the watcher fingerprints the tree with.</param>
  public static WorkspaceWatcherLease Acquire(string workspaceRoot, IWorkspaceSnapshotAccess snapshots)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentNullException.ThrowIfNull(snapshots);
    string root = Path.GetFullPath(workspaceRoot);
    lock (Gate)
    {
      if (!Held.TryGetValue(root, out Entry? entry))
      {
        // The registry owns the watcher until the last hold goes, so ownership outlives this
        // scope by construction.
#pragma warning disable CA2000 // Dispose objects before losing scope
        WorkspaceFileWatcher created = new(root, snapshots, _ => Task.CompletedTask);
#pragma warning restore CA2000
        entry = new Entry(created);
        Held[root] = entry;
      }

      entry.Holders++;
      return new WorkspaceWatcherLease(entry.Watcher, owner => ReleaseAsync(owner, root, entry.Watcher));
    }
  }

  /// <summary>Gives one hold back, dropping that hold's subscriptions and stopping the watcher
  ///     when it was the last. A release for a watcher this process no longer holds is a
  ///     no-op.</summary>
  private static async ValueTask ReleaseAsync(WorkspaceWatcherLease owner, string root,
      WorkspaceFileWatcher watcher)
  {
    bool last;
    lock (Gate)
    {
      if (!Held.TryGetValue(root, out Entry? current) || !ReferenceEquals(current.Watcher, watcher))
      {
        return;
      }

      last = --current.Holders == 0;
      if (last)
      {
        _ = Held.Remove(root);
      }
    }

    // The hold's own subscribers go whatever the count says: this session is closed either way,
    // and a watcher the next session keeps running must not keep calling it.
    owner.DropSubscriptions();

    if (last)
    {
      await watcher.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>One root's watcher and how many leases are holding it. Guarded by
  ///     <see cref="Gate"/>.</summary>
  private sealed class Entry(WorkspaceFileWatcher watcher)
  {
    public WorkspaceFileWatcher Watcher { get; } = watcher;

    public int Holders { get; set; }
  }
}

/// <summary>One container's hold on a workspace's shared watcher. Disposing it gives the hold
///     back; the last release stops the watcher.
///     <para>The lease does not own the watcher - the registry does, and every session over the
///     same root holds the same instance - so a holder subscribes through the lease and never
///     disposes the watcher. The lease drops the subscriptions taken through it when it is
///     released, which is what makes the hold a stake in the subscriber list and not only a
///     count (issue #388).</para>
///     <para>Release is idempotent: a second release does not give back a hold already returned.
///     </para></summary>
public sealed class WorkspaceWatcherLease : IAsyncDisposable, IFileWatcherSubscription
{
  // The root is not held: the release captures it, so the lease names no registry key.
  private readonly Func<WorkspaceWatcherLease, ValueTask> _release;
  private readonly List<IDisposable> _subscriptions = [];
  private readonly Lock _subscriptionsGate = new();
  private int _released;

  internal WorkspaceWatcherLease(WorkspaceFileWatcher watcher,
      Func<WorkspaceWatcherLease, ValueTask> release)
  {
    Watcher = watcher;
    _release = release;
  }

  /// <summary>The workspace's shared watcher.</summary>
  public WorkspaceFileWatcher Watcher { get; }

  /// <summary>Starts the held watcher. Idempotent, and the only thing that begins a walk.</summary>
  public void EnsureStarted() => Watcher.Start();

  /// <summary>Subscribes a handler to the held watcher. The subscription is dropped when this
  ///     lease is released, even if the caller never disposed it, and subscribing on a released
  ///     lease hands back a subscription that is already gone - a closed session is never called
  ///     again.</summary>
  public IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler)
  {
    IDisposable subscription = Watcher.Subscribe(handler);
    lock (_subscriptionsGate)
    {
      // The release marks its intent under THIS lock, so a subscription is either recorded
      // before the release sees it or refused after - never added to a lease already released.
      if (_released == 0)
      {
        _subscriptions.Add(subscription);
        return subscription;
      }
    }

    subscription.Dispose();
    return subscription;
  }

  /// <summary>Releases every subscription taken through this lease. Idempotent.</summary>
  internal void DropSubscriptions()
  {
    IDisposable[] held;
    lock (_subscriptionsGate)
    {
      held = [.. _subscriptions];
      _subscriptions.Clear();
    }

    foreach (IDisposable subscription in held)
    {
      subscription.Dispose();
    }
  }

  /// <summary>Gives the hold back. The last release stops the watcher.</summary>
  public async ValueTask DisposeAsync()
  {
    lock (_subscriptionsGate)
    {
      if (_released != 0)
      {
        return;
      }

      _released = 1;
    }

    await _release(this).ConfigureAwait(false);
  }
}
