using eThangAgent.FileSystem.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #381: the workspace watcher belongs to the workspace ROOT, not to the session
///     that happened to reach the language server first.
///     <para>Every assertion here is about a count a user pays for: how many watchers walk one
///     tree, and whether the walk stops when the sessions that wanted it are gone.</para>
///     <para>These tests hold the registry directly, so they spawn nothing and build no
///     composed container - the fast tier runs them.</para></summary>
public class WorkspaceWatcherRegistryTests
{
  private static string NewTree() => TestTempRoot.CreateDirectory("wswatch-registry").FullName;

  [Fact]
  public async Task TwoHoldsOverOneRoot_ShareOneWatcher()
  {
    string root = NewTree();
    try
    {
      await using WorkspaceWatcherLease first = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      await using WorkspaceWatcherLease second = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());

      Assert.Same(first.Watcher, second.Watcher);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task HoldsOverDifferentRoots_GetDifferentWatchers()
  {
    string one = NewTree();
    string two = NewTree();
    try
    {
      await using WorkspaceWatcherLease first = WorkspaceWatcherRegistry.Acquire(one, new DirectWorkspaceSnapshotAccess());
      await using WorkspaceWatcherLease second = WorkspaceWatcherRegistry.Acquire(two, new DirectWorkspaceSnapshotAccess());

      Assert.NotSame(first.Watcher, second.Watcher);
    }
    finally
    {
      Directory.Delete(one, true);
      Directory.Delete(two, true);
    }
  }

  [Fact]
  public async Task ReleasingOneOfTwoHolds_LeavesTheWatcherRunning()
  {
    string root = NewTree();
    try
    {
      WorkspaceWatcherLease first = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      await using WorkspaceWatcherLease second = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      second.EnsureStarted();

      await first.DisposeAsync();

      // The surviving holder still has a started watcher, and the registry still hands out
      // that same instance rather than minting a second walker over the same tree.
      Assert.True(second.Watcher.IsStarted);
      await using WorkspaceWatcherLease third = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      Assert.Same(second.Watcher, third.Watcher);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task TheLastRelease_ForgetsTheRoot_SoTheNextHoldStartsAFreshWatcher()
  {
    string root = NewTree();
    try
    {
      WorkspaceWatcherLease only = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      WorkspaceFileWatcher held = only.Watcher;
      await only.DisposeAsync();

      await using WorkspaceWatcherLease next = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());

      // A forgotten root holds no watcher, so the session that opens next gets a fresh one
      // rather than a stopped instance from a session that has closed.
      Assert.NotSame(held, next.Watcher);
      Assert.False(next.Watcher.IsStarted);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task ReleasingTwice_IsANoOp()
  {
    string root = NewTree();
    try
    {
      WorkspaceWatcherLease first = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      WorkspaceWatcherLease second = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());

      await first.DisposeAsync();
      await first.DisposeAsync();

      // The double release must not have given back the SECOND holder's hold: that hold is
      // still live, so no new watcher is minted for the same root.
      await using WorkspaceWatcherLease third = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      Assert.Same(second.Watcher, third.Watcher);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A hold that goes back drops the subscriptions it made (issue #388).
  ///     <para>THE DEFECT THIS PINS: <c>Holders</c> counted leases while the watcher's subscriber
  ///     list kept every session that had ever subscribed, so a closed session's view-model - the
  ///     language-prompt handler captures it - stayed reachable for as long as the next session
  ///     over the same root kept the watcher running. A session that released its hold and a
  ///     session still holding it must therefore disagree about who is called.</para></summary>
  [Fact]
  public async Task ReleasingAHold_DropsTheSubscriptionsItMade_WithoutStoppingTheWatcher()
  {
    string root = NewTree();
    try
    {
      WorkspaceWatcherLease closed = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      await using WorkspaceWatcherLease live = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      closed.EnsureStarted();

      int throughClosed = 0;
      int throughLive = 0;
      _ = closed.Subscribe(_ =>
      {
        throughClosed++;
        return Task.CompletedTask;
      });
      _ = live.Subscribe(_ =>
      {
        throughLive++;
        return Task.CompletedTask;
      });

      // A pass that finds a change announces it to the subscribers that are still held.
      await File.WriteAllTextAsync(Path.Combine(root, "before.rs"), "fn main() {}",
          TestContext.Current.CancellationToken);
      await closed.DisposeAsync();
      await File.WriteAllTextAsync(Path.Combine(root, "after.rs"), "fn main() {}",
          TestContext.Current.CancellationToken);
      await live.Watcher.CheckOnceAsync(TestContext.Current.CancellationToken);

      // The live hold keeps the watcher running and keeps hearing it; the closed hold's
      // subscriber heard nothing at all.
      Assert.True(throughLive > 0, "the session still holding the watcher must keep hearing it");
      Assert.Equal(0, throughClosed);
      await using WorkspaceWatcherLease next = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      Assert.Same(live.Watcher, next.Watcher);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A subscription taken on a released hold is dead on arrival, so a session that closes
  ///     while its first detection pass is still in flight cannot start listening again.</summary>
  [Fact]
  public async Task SubscribingOnAReleasedHold_NeverCallsTheHandler()
  {
    string root = NewTree();
    try
    {
      // The shape is the real one: the session closes while ANOTHER session over the same root
      // keeps the watcher running, so the closed session's late subscription has a live watcher
      // to attach to and must still be dropped.
      await using WorkspaceWatcherLease live = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      WorkspaceWatcherLease released = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());
      released.EnsureStarted();
      await released.DisposeAsync();

      int calls = 0;
      using IDisposable subscription = released.Subscribe(_ =>
      {
        calls++;
        return Task.CompletedTask;
      });
      await File.WriteAllTextAsync(Path.Combine(root, "after-close.rs"), "fn main() {}",
          TestContext.Current.CancellationToken);
      await live.Watcher.CheckOnceAsync(TestContext.Current.CancellationToken);

      Assert.Equal(0, calls);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task AcquiringAHold_DoesNotStartAWatcher()
  {
    string root = NewTree();
    try
    {
      await using WorkspaceWatcherLease lease = WorkspaceWatcherRegistry.Acquire(root, new DirectWorkspaceSnapshotAccess());

      // A session that never reaches the language server must never walk the tree.
      Assert.False(lease.Watcher.IsStarted);

      lease.EnsureStarted();
      Assert.True(lease.Watcher.IsStarted);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
