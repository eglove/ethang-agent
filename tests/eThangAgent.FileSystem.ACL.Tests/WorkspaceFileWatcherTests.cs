using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Issue #348: event-driven detection with debounce and serialized passes.
// Real file system, real FSW - timing tests use generous timeouts.
#pragma warning disable CA2007 // tests use ConfigureAwait(true) to match existing test style
#pragma warning disable S1144 // Calls property is part of the test double surface
public class WorkspaceFileWatcherTests
{
  private static string NewTree() => Directory.CreateTempSubdirectory("wswatch").FullName;

  private static void Write(string root, string relative, string content)
  {
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  private sealed class CountingSnapshotAccess : IWorkspaceSnapshotAccess
  {
    private readonly DirectWorkspaceSnapshotAccess _inner = new();
    private int _current;
    private int _maxConcurrent;
    private int _calls;

    public int MaxConcurrent => _maxConcurrent;

    public int Calls => _calls; // S1144 suppressed: used by test assertions

    public async Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default)
    {
      int now = Interlocked.Increment(ref _current);
      int observed = Volatile.Read(ref _maxConcurrent);
      while (now > observed)
      {
        int prev = Interlocked.CompareExchange(ref _maxConcurrent, now, observed);
        if (prev == observed)
        {
          break;
        }

        observed = prev;
      }

      try
      {
        _ = Interlocked.Increment(ref _calls);
        await Task.Delay(10, ct).ConfigureAwait(false);
        return await _inner.CaptureAsync(workspaceRoot, ct).ConfigureAwait(false);
      }
      finally
      {
        _ = Interlocked.Decrement(ref _current);
      }
    }
  }

  [Fact]
  public async Task FileWrittenOutsideAgent_TriggersPass()
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
      Write(root, "new.cs", "content");
      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.NotNull(received);
      Assert.Contains("new.cs", received.Added);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task BurstOfWrites_ProducesOnePass()
  {
    string root = NewTree();
    try
    {
      int passes = 0;
      WorkspaceChangeSet? last = null;
      TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
      await using WorkspaceFileWatcher watcher = new(root, new DirectWorkspaceSnapshotAccess(),
          changes =>
          {
            _ = Interlocked.Increment(ref passes); // count
            last = changes;
            _ = tcs.TrySetResult();
            return Task.CompletedTask;
          });
      watcher.Start();
      for (int i = 0; i < 5; i++)
      {
        Write(root, $"f{i}.cs", "x");
      }

      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
      await Task.Delay(600, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(1, passes);
      Assert.NotNull(last);
      Assert.Equal(5, last.Added.Count);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task ExcludedPaths_NeverAppearInPass()
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
      Write(root, "bin/Debug/b.dll", "binary");
      Write(root, "obj/project.assets.json", "{}");
      Write(root, ".git/HEAD", "ref");
      Write(root, "node_modules/pkg/index.js", "js");
      await Task.Delay(800, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(0, passes);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task TwoPasses_NeverRunConcurrently()
  {
    string root = NewTree();
    try
    {
      CountingSnapshotAccess snapshots = new();
      await using WorkspaceFileWatcher watcher = new(root, snapshots, _ => Task.CompletedTask);
      watcher.Start();
      Write(root, "a.cs", "one");
      Task a = watcher.CheckOnceAsync(TestContext.Current.CancellationToken);
      Task b = watcher.CheckOnceAsync(TestContext.Current.CancellationToken);
      await a.ConfigureAwait(true);
      await b.ConfigureAwait(true);
      Assert.Equal(1, snapshots.MaxConcurrent);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task DeletedFile_AppearsInDeletedSet()
  {
    string root = NewTree();
    try
    {
      Write(root, "gone.cs", "x");
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
      File.Delete(Path.Combine(root, "gone.cs"));
      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.NotNull(received);
      Assert.Contains("gone.cs", received.Removed);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Rename_SurfacesAsDeletePlusAdd()
  {
    string root = NewTree();
    try
    {
      Write(root, "old.cs", "x");
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
      File.Move(Path.Combine(root, "old.cs"), Path.Combine(root, "new.cs"));
      await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
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
  public async Task NoChange_NoAnnouncement()
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
      await Task.Delay(600, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(0, passes);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
