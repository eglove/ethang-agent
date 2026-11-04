using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Issue #349: polling sweep catches what FileSystemWatcher misses.
#pragma warning disable CA2007
public class WorkspaceFileWatcherPollingTests
{
  private static string NewTree() => Directory.CreateTempSubdirectory("wspoll").FullName;

  private static void Write(string root, string relative, string content)
  {
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  [Fact]
  public async Task SweepOnceAsync_CatchesMissedChange()
  {
    string root = NewTree();
    try
    {
      WorkspaceChangeSet? received = null;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(),
          changes =>
          {
            received = changes;
            return Task.CompletedTask;
          });
      watcher.Start();
      Write(root, "missed.cs", "content");
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.NotNull(received);
      Assert.Contains("missed.cs", received.Added);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task SweepOnceAsync_AtomicSaveRename()
  {
    string root = NewTree();
    try
    {
      Write(root, "old.cs", "x");
      WorkspaceChangeSet? received = null;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(),
          changes =>
          {
            received = changes;
            return Task.CompletedTask;
          });
      watcher.Start();
      string tmp = Path.Combine(root, ".tmp-save");
      await File.WriteAllTextAsync(tmp, "x", TestContext.Current.CancellationToken);
      File.Move(tmp, Path.Combine(root, "new.cs"), overwrite: true);
      File.Delete(Path.Combine(root, "old.cs"));
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.NotNull(received);
      Assert.Contains("old.cs", received.Removed);
      Assert.Contains("new.cs", received.Added);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task SweepOnceAsync_NoChange_StaysSilent()
  {
    string root = NewTree();
    try
    {
      int passes = 0;
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(),
          _ =>
          {
            passes++;
            return Task.CompletedTask;
          });
      watcher.Start();
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      await watcher.SweepOnceAsync(TestContext.Current.CancellationToken);
      Assert.Equal(0, passes);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task SimulateBufferOverflow_ForcesSweep()
  {
    string root = NewTree();
    try
    {
      WorkspaceChangeSet? received = null;
      TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(),
          changes =>
          {
            received = changes;
            _ = tcs.TrySetResult();
            return Task.CompletedTask;
          });
      watcher.Start();
      Write(root, "after-overflow.cs", "y");
      watcher.SimulateBufferOverflow();
      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
      Assert.NotNull(received);
      Assert.Contains("after-overflow.cs", received.Added);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task PollingSweep_AutoCatchesMissedChange()
  {
    string root = NewTree();
    try
    {
      WorkspaceChangeSet? received = null;
      TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(),
          changes =>
          {
            received = changes;
            _ = tcs.TrySetResult();
            return Task.CompletedTask;
          },
          pollingInterval: TimeSpan.FromMilliseconds(50));
      watcher.Start();
      Write(root, "polled.cs", "z");
      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
      Assert.NotNull(received);
      Assert.Contains("polled.cs", received.Added);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
