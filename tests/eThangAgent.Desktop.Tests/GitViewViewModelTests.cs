using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #9: the workbench's Git view must show branch, status, and the
///     selected path's diff, and must never confuse 'not a repository' with 'a clean
///     repository'. The marshal callback runs inline here: these tests assert view-model
///     state, and Avalonia's dispatcher is not running in a unit test.</summary>
public sealed class GitViewViewModelTests
{
  private const string Workspace = @"C:\work\demo";

  /// <summary>A git seam the test drives directly, so a read is indistinguishable from
  ///     the repository changing underneath the view.</summary>
  private sealed class FakeGit : IGitQueryAccess
  {
    public Result<GitStatus> Status { get; set; } = Result.Success(
        new GitStatus("master", [], [], []));

    public Result<GitDiff> Diff { get; set; } = Result.Success(
        new GitDiff(new GitDiffStats(0, 0, 0), string.Empty));

    public List<string> DiffScopes { get; } = [];

    public List<string?> DiffPaths { get; } = [];

    public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult(Status);

    public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
    {
      DiffScopes.Add(scope);
      DiffPaths.Add(path);
      return Task.FromResult(Diff);
    }

    public Task<Result<string>> GetBranchAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult(Result.Success("master"));
  }

  private static GitViewViewModel Build(FakeGit? git) =>
      new(Workspace, git, render => render());

  [Fact]
  public async Task Refresh_RendersBranchDirtyAndHead()
  {
    FakeGit git = new()
    {
      Status = Result.Success(new GitStatus(
          "master",
          [new GitStatusEntry("M ", "staged.cs")],
          [new GitStatusEntry(" M", "unstaged.cs")],
          ["docs/new.md"],
          new GitHeadCommit("10bde14", "fix(exec): keep errors in-band"),
          "origin/master")),
    };
    using GitViewViewModel vm = Build(git);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal("master \u2192 origin/master", vm.BranchDisplay);
    Assert.Equal("2 changed, 1 untracked", vm.DirtyDisplay);
    Assert.Equal("10bde14 fix(exec): keep errors in-band", vm.HeadDisplay);
    Assert.False(vm.IsClean);
    Assert.Null(vm.EmptyReason);
    Assert.Equal("watched \u00B7 workspace", vm.StatusLine);
  }

  [Fact]
  public async Task Refresh_ListsUntrackedThenUnstagedThenStaged()
  {
    FakeGit git = new()
    {
      Status = Result.Success(new GitStatus(
          "master",
          [new GitStatusEntry("M ", "staged.cs")],
          [new GitStatusEntry(" M", "unstaged.cs")],
          ["docs/new.md"])),
    };
    using GitViewViewModel vm = Build(git);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["docs/new.md", "unstaged.cs", "staged.cs"],
        vm.Changes.Select(c => c.Path));
    Assert.Equal(["new", "modified", "staged"], vm.Changes.Select(c => c.Label));
  }

  [Fact]
  public async Task Refresh_CleanTree_SaysClean()
  {
    using GitViewViewModel vm = Build(new FakeGit());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.IsClean);
    Assert.Equal("clean", vm.DirtyDisplay);
    Assert.Empty(vm.Changes);
  }

  [Fact]
  public async Task Refresh_NoCommitsYet_NamesTheAbsence()
  {
    using GitViewViewModel vm = Build(new FakeGit());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal("(no commits yet)", vm.HeadDisplay);
  }

  [Fact]
  public async Task Refresh_NotARepository_RendersAnEmptyStateNotAnError()
  {
    FakeGit git = new()
    {
      Status = Result.Failure<GitStatus>(new DomainError("NotAGitRepository", "Not a git repository: x")),
    };
    using GitViewViewModel vm = Build(git);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal("This workspace is not a git repository.", vm.EmptyReason);
    Assert.Equal("no repository", vm.StatusLine);
    Assert.Empty(vm.Changes);
  }

  [Fact]
  public async Task Refresh_NoGitSeam_RendersUnavailable()
  {
    using GitViewViewModel vm = Build(git: null);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal("git is unavailable in this session.", vm.EmptyReason);
    Assert.Equal("unavailable", vm.StatusLine);
  }

  [Fact]
  public async Task SelectAsync_RendersTheDiffWithKinds()
  {
    FakeGit git = new()
    {
      Status = Result.Success(new GitStatus("master", [], [], ["docs/new.md"])),
      Diff = Result.Success(new GitDiff(new GitDiffStats(1, 2, 0),
          "diff --git a/docs/new.md b/docs/new.md\n" +
          "--- /dev/null\n" +
          "+++ b/docs/new.md\n" +
          "@@ -0,0 +1,2 @@\n" +
          "+line one\n" +
          "+line two\n")),
    };
    using GitViewViewModel vm = Build(git);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync("docs/new.md", TestContext.Current.CancellationToken);

    Assert.Equal("docs/new.md", vm.SelectedPath);
    Assert.Equal("DIFF \u00B7 docs/new.md", vm.DiffHeading);
    Assert.Null(vm.DiffError);
    Assert.Equal(2, vm.DiffLines.Count(l => l.IsAdded));
    Assert.Equal(1, vm.DiffLines.Count(l => l.IsHunk));
    // '---'/'+++' are file HEADERS, not a removal and an addition.
    Assert.DoesNotContain(vm.DiffLines, l => l.IsRemoved);
    Assert.Equal("All", Assert.Single(git.DiffScopes));
  }

  [Fact]
  public async Task SelectAsync_SamePathTwice_DoesNotReReadTheDiff()
  {
    FakeGit git = new();
    using GitViewViewModel vm = Build(git);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync("a.cs", TestContext.Current.CancellationToken);
    await vm.SelectAsync("a.cs", TestContext.Current.CancellationToken);

    _ = Assert.Single(git.DiffPaths);
  }

  [Fact]
  public async Task SelectAsync_DiffFailure_RendersTheReason()
  {
    FakeGit git = new()
    {
      Diff = Result.Failure<GitDiff>(new DomainError("GitError", "bad revision")),
    };
    using GitViewViewModel vm = Build(git);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync("a.cs", TestContext.Current.CancellationToken);

    Assert.Equal("diff unavailable: bad revision", vm.DiffError);
    Assert.Empty(vm.DiffLines);
  }

  [Theory]
  [InlineData("bin/Debug/x.dll", true)]
  [InlineData("obj/project.assets.json", true)]
  [InlineData("node_modules/pkg/index.js", true)]
  [InlineData(".git/HEAD", true)]
  [InlineData("src/App.cs", false)]
  [InlineData("objects/notes.md", false)]
  [InlineData("src/bin/notes.md", true)]
  public void IsExcluded_MatchesWholePathSegments(string relative, bool excluded)
  {
    using GitViewViewModel vm = Build(new FakeGit());

    Assert.Equal(excluded, vm.IsExcluded(Path.Combine(Workspace, relative)));
  }

  [Fact]
  public void IsExcluded_PathOutsideTheWorkspace_IsExcluded()
  {
    using GitViewViewModel vm = Build(new FakeGit());

    Assert.True(vm.IsExcluded(Path.Combine(@"C:\elsewhere", "notes.md")));
  }

  [Fact]
  public async Task Start_IsIdempotent_AndRunsTheFirstRead()
  {
    FakeGit git = new()
    {
      Status = Result.Success(new GitStatus("master", [], [], ["a.cs"])),
    };
    using GitViewViewModel vm = Build(git);

    vm.Start();
    vm.Start();

    // The watcher's debounce means the read lands asynchronously; poll briefly rather
    // than sleeping a fixed interval, so a fast machine is not slowed down.
    await WaitForAsync(() => vm.Changes.Count == 1);
    _ = Assert.Single(vm.Changes);
  }

  private static async Task WaitForAsync(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (condition())
      {
        return;
      }

      await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Fail("the view never reached the expected state within 10 seconds.");
  }
}
