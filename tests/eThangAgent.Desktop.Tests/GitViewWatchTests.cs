using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #9's acceptance criteria, driven against a REAL directory and a real
///     <see cref="FileSystemWatcher"/>: an edit made by another program shows up within a
///     quarter second, a build under bin/obj does not cause a refresh storm, and a
///     workspace that is not a repository renders an empty state.
///     <para>The git seam is faked so the assertions measure the WATCH, not git's own
///     latency: a real git invocation per refresh would make the timing assertions
///     measure the wrong thing. The real ACL is covered by DirectGitAccessTests.</para></summary>
public sealed class GitViewWatchTests : IDisposable
{
  private readonly string _root;

  public GitViewWatchTests()
  {
    _root = Path.Combine(Path.GetTempPath(), "ethang-gitview-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_root);
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup: a watcher may still hold a handle
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }
  }

  /// <summary>Counts reads so the storm assertion can tell one refresh from fifty.</summary>
  private sealed class CountingGit : IGitQueryAccess
  {
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
      _ = Interlocked.Increment(ref _reads);
      return Task.FromResult(Result.Success(new GitStatus("master", [], [], ["a.txt"])));
    }

    public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new GitDiff(new GitDiffStats(0, 0, 0), string.Empty)));

    public Task<Result<string>> GetBranchAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult(Result.Success("master"));
  }

  private GitViewViewModel Build(IGitQueryAccess git)
      => new(_root, git, render => render());

  [Fact]
  public async Task EditingAFile_RefreshesTheViewWithinAQuarterSecond()
  {
    CountingGit git = new();
    using GitViewViewModel vm = Build(git);
    vm.Start();
    await WaitForAsync(() => git.Reads >= 1);
    int baseline = git.Reads;

    // Stand in for another editor: a plain write, no notification from this process.
    await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "edited elsewhere",
        TestContext.Current.CancellationToken);

    await WaitForAsync(() => git.Reads > baseline, TimeSpan.FromMilliseconds(2500));
    Assert.True(git.Reads > baseline, "an edit in another program must refresh the view.");
  }

  [Fact]
  public async Task ABuildUnderBinAndObj_DoesNotCauseARefreshStorm()
  {
    CountingGit git = new();
    using GitViewViewModel vm = Build(git);
    vm.Start();
    await WaitForAsync(() => git.Reads >= 1);
    int baseline = git.Reads;

    // A build writes many files under bin/ and obj/. None of them may refresh: one
    // compile would otherwise be dozens of git invocations.
    foreach (string dir in new[] { "bin", "obj", Path.Combine("node_modules", "pkg") })
    {
      string full = Path.Combine(_root, dir);
      _ = Directory.CreateDirectory(full);
      for (int i = 0; i < 20; i++)
      {
        await File.WriteAllTextAsync(Path.Combine(full, $"artifact{i}.dll"), "x",
            TestContext.Current.CancellationToken);
      }
    }

    // Give the watcher longer than the debounce to prove the ABSENCE of a refresh.
    await Task.Delay(700, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(baseline, git.Reads);
  }

  [Fact]
  public async Task ManyEditsInOneBurst_CollapseIntoOneRefresh()
  {
    CountingGit git = new();
    using GitViewViewModel vm = Build(git);
    vm.Start();
    await WaitForAsync(() => git.Reads >= 1);
    int baseline = git.Reads;

    for (int i = 0; i < 15; i++)
    {
      await File.WriteAllTextAsync(Path.Combine(_root, $"burst{i}.txt"), "x",
          TestContext.Current.CancellationToken);
    }

    await WaitForAsync(() => git.Reads > baseline);
    await Task.Delay(600, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.InRange(git.Reads - baseline, 1, 3);
  }

  [Fact]
  public async Task NotARepository_RendersAnEmptyState_NotAnError()
  {
    using GitViewViewModel vm = Build(new NotARepoGit());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal("This workspace is not a git repository.", vm.EmptyReason);
    Assert.Empty(vm.Changes);
    Assert.Null(vm.DiffError);
  }

  private sealed class NotARepoGit : IGitQueryAccess
  {
    public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<GitStatus>(
            new DomainError("NotAGitRepository", $"Not a git repository: {repoPath}")));

    public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<GitDiff>(
            new DomainError("NotAGitRepository", $"Not a git repository: {repoPath}")));

    public Task<Result<string>> GetBranchAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(
            new DomainError("NotAGitRepository", $"Not a git repository: {repoPath}")));
  }

  private static Task WaitForAsync(Func<bool> condition) => WaitForAsync(condition, TimeSpan.FromSeconds(10));

  private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (condition())
      {
        return;
      }

      await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
