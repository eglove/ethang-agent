using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Every workspace watcher this process holds, keyed by workspace root. Sessions over
///     one root share one watcher, and the watcher stops when the last of them gives its hold
///     back; a root nothing holds is forgotten.
///     <para>Acquiring a hold does not start the watcher: starting is the holder's decision
///     (<see cref="WorkspaceWatcherLease.EnsureStarted"/>), and it is idempotent.</para></summary>
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
      return new WorkspaceWatcherLease(root, entry.Watcher, ReleaseAsync);
    }
  }

  /// <summary>Gives one hold back, stopping the watcher when it was the last. A release for a
  ///     watcher this process no longer holds is a no-op.</summary>
  private static async ValueTask ReleaseAsync(string root, WorkspaceFileWatcher watcher)
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
///     same root holds the same instance - so a holder subscribes to it and never disposes it.</para>
///     <para>Release is idempotent: a second release does not give back a hold already returned.
///     </para></summary>
public sealed class WorkspaceWatcherLease : IAsyncDisposable
{
  private readonly string _root;
  private readonly Func<string, WorkspaceFileWatcher, ValueTask> _release;
  private int _released;

  internal WorkspaceWatcherLease(string root, WorkspaceFileWatcher watcher,
      Func<string, WorkspaceFileWatcher, ValueTask> release)
  {
    _root = root;
    Watcher = watcher;
    _release = release;
  }

  /// <summary>The workspace's shared watcher.</summary>
  public WorkspaceFileWatcher Watcher { get; }

  /// <summary>Starts the held watcher. Idempotent, and the only thing that begins a walk.</summary>
  public void EnsureStarted() => Watcher.Start();

  /// <summary>Gives the hold back. The last release stops the watcher.</summary>
  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref _released, 1) != 0)
    {
      return;
    }

    await _release(_root, Watcher).ConfigureAwait(false);
  }
}
