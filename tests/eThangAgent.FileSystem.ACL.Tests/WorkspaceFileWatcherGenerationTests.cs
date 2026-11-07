using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Issue #350: generation counter, pub/sub announcements, and re-sync.
#pragma warning disable CA2007
public class WorkspaceFileWatcherGenerationTests
{
  private static string NewTree() => TestTempRoot.CreateDirectory("wsgeneration").FullName;

  private static void Write(string root, string relative, string content)
  {
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  [Fact]
  public async Task Generation_StartsAtZero_AdvancesExactlyOnePerChangingPass()
  {
    string root = NewTree();
    try
    {
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      Assert.Equal(0, watcher.Generation);
      Write(root, "a.cs", "one");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(1, watcher.Generation);
      Write(root, "b.cs", "two");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(2, watcher.Generation);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Generation_MultipleFilesInOnePass_AdvancesByExactlyOne()
  {
    string root = NewTree();
    try
    {
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      Write(root, "a.cs", "one");
      Write(root, "b.cs", "two");
      Write(root, "c.cs", "three");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(1, watcher.Generation);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Generation_NoChangePass_AdvancesNothing_AnnouncesNothing()
  {
    string root = NewTree();
    try
    {
      int announcements = 0;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      using IDisposable sub = watcher.Subscribe(_ =>
      {
        announcements++;
        return Task.CompletedTask;
      });
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(0, watcher.Generation);
      Assert.Equal(0, announcements);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Announcement_CarriesPathsDeletedPathsGenerationAndTimestamp()
  {
    string root = NewTree();
    try
    {
      Write(root, "gone.cs", "x");
      FileChangeAnnouncement? received = null;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      using IDisposable sub = watcher.Subscribe(a =>
      {
        received = a;
        return Task.CompletedTask;
      });
      Write(root, "new.cs", "y");
      File.Delete(Path.Combine(root, "gone.cs"));
      DateTimeOffset before = DateTimeOffset.UtcNow;
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      DateTimeOffset after = DateTimeOffset.UtcNow;
      Assert.NotNull(received);
      Assert.Contains("new.cs", received.Paths);
      Assert.Contains("gone.cs", received.DeletedPaths);
      Assert.Equal(1, received.Generation);
      Assert.InRange(received.UtcTimestamp, before, after);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Subscribe_ThrowingSubscriber_DoesNotBlockOthers()
  {
    string root = NewTree();
    try
    {
      List<string> order = [];
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      using IDisposable bad = watcher.Subscribe(_ =>
      {
        order.Add("bad");
        throw new InvalidOperationException("subscriber blew up");
      });
      using IDisposable good = watcher.Subscribe(_ =>
      {
        order.Add("good");
        return Task.CompletedTask;
      });
      Write(root, "a.cs", "one");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(["bad", "good"], order);
      Assert.Equal(1, watcher.Generation);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Subscribe_Dispose_StopsAnnouncements()
  {
    string root = NewTree();
    try
    {
      int announcements = 0;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      IDisposable sub = watcher.Subscribe(_ =>
      {
        announcements++;
        return Task.CompletedTask;
      });
      Write(root, "a.cs", "one");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(1, announcements);
      sub.Dispose();
      Write(root, "b.cs", "two");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(1, announcements);
      Assert.Equal(2, watcher.Generation);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Resync_CorrectsMissedEvent()
  {
    string root = NewTree();
    try
    {
      FileChangeAnnouncement? received = null;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(), _ => Task.CompletedTask);
      watcher.Start();
      using IDisposable sub = watcher.Subscribe(a =>
      {
        received = a;
        return Task.CompletedTask;
      });
      Write(root, "missed.cs", "content");
      await watcher.ResyncOnceAsync(TestContext.Current.CancellationToken);
      Assert.NotNull(received);
      Assert.Contains("missed.cs", received.Paths);
      Assert.Equal(1, received.Generation);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
