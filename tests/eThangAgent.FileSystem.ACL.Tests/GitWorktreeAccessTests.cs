using System.Diagnostics;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// git child processes are short-lived and awaited to completion.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class GitWorktreeAccessTests : IDisposable
{
  private readonly string _repoDir;

  public GitWorktreeAccessTests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "ethang-worktree-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_repoDir);
    _ = RunGit("init", "-b", "main");
    _ = RunGit("config", "user.email", "test@example.com");
    _ = RunGit("config", "user.name", "Test");
    File.WriteAllText(Path.Combine(_repoDir, "seed.txt"), "seed");
    _ = RunGit("add", "seed.txt");
    _ = RunGit("commit", "-m", "seed");
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_repoDir, true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  private (int ExitCode, string StdOut, string StdErr) RunGit(params string[] args)
  {
    ProcessStartInfo psi = new("git")
    {
      WorkingDirectory = _repoDir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    foreach (string a in args)
    {
      psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    string stdout = p.StandardOutput.ReadToEnd();
    string stderr = p.StandardError.ReadToEnd();
    _ = p.WaitForExit(30000);
    return (p.ExitCode, stdout, stderr);
  }

  private string WorktreePath(string name) => Path.Combine(_repoDir, ".worktrees", name);

  [Fact]
  public async Task ListAsync_FreshRepo_ReturnsSingleMainWorktree()
  {
    GitWorktreeAccess access = new();

    Result<IReadOnlyList<WorktreeInfo>> r = await access.ListAsync(_repoDir, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    WorktreeInfo main = Assert.Single(r.Value);
    Assert.True(main.IsMain);
    Assert.False(main.IsDirty);
    Assert.Equal("main", main.Branch);
    Assert.Equal(7, main.HeadShortSha.Length);
    Assert.Equal(Path.GetFileName(_repoDir), main.Name);
  }

  [Fact]
  public async Task ListAsync_DetachedWorktreeWithChanges_ReportsDirty()
  {
    GitWorktreeAccess access = new();
    Result<WorktreeInfo> created = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess);
    Assert.Equal(0, RunGit("-C", WorktreePath("agent-fix"), "checkout", "--detach", "HEAD").ExitCode);
    await File.WriteAllTextAsync(Path.Combine(WorktreePath("agent-fix"), "dirty.txt"), "x", TestContext.Current.CancellationToken);

    Result<IReadOnlyList<WorktreeInfo>> list = await access.ListAsync(_repoDir, ct: TestContext.Current.CancellationToken);

    Assert.True(list.IsSuccess);
    WorktreeInfo detached = Assert.Single(list.Value, w => w.Branch == "(detached)");
    Assert.False(detached.IsMain);
    Assert.True(detached.IsDirty, "detached worktree reported IsDirty without a dirty check");
  }

  [Fact]
  public async Task ListAsync_DetachedMainTree_ReportsDirty()
  {
    Assert.Equal(0, RunGit("checkout", "--detach", "HEAD").ExitCode);
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "dirty.txt"), "x", TestContext.Current.CancellationToken);
    GitWorktreeAccess access = new();

    Result<IReadOnlyList<WorktreeInfo>> list = await access.ListAsync(_repoDir, ct: TestContext.Current.CancellationToken);

    Assert.True(list.IsSuccess);
    WorktreeInfo main = Assert.Single(list.Value, w => w.IsMain);
    Assert.Equal("(detached)", main.Branch);
    Assert.True(main.IsDirty, "detached main tree reported IsDirty without a dirty check");
  }

  [Fact]
  public async Task ListAsync_BareRepository_SkipsDirtyProbeAndListsOneEntry()
  {
    string bare = Path.Combine(Path.GetTempPath(), "ethang-bare-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(bare);
    try
    {
      Assert.Equal(0, RunGit("-C", bare, "init", "--bare").ExitCode);
      GitWorktreeAccess access = new();

      Result<IReadOnlyList<WorktreeInfo>> r = await access.ListAsync(bare, ct: TestContext.Current.CancellationToken);

      // 'git status' fails inside a bare repo: success here pins that the bare
      // entry alone skipped the dirty probe while still being listed.
      Assert.True(r.IsSuccess);
      WorktreeInfo entry = Assert.Single(r.Value);
      Assert.True(entry.IsMain);
      Assert.False(entry.IsDirty);
    }
    finally
    {
      Directory.Delete(bare, true);
    }
  }

  [Fact]
  public async Task CreateAsync_CreatesDirectoryBranchAndReportsWorktree()
  {
    GitWorktreeAccess access = new();

    Result<WorktreeInfo> r = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("agent-fix", r.Value.Name);
    Assert.Equal(WorktreePath("agent-fix"), r.Value.Path);
    Assert.Equal("worktree/agent-fix", r.Value.Branch);
    Assert.False(r.Value.IsMain);
    Assert.False(r.Value.IsDirty);
    Assert.Equal(7, r.Value.HeadShortSha.Length);
    Assert.True(Directory.Exists(r.Value.Path));
    Assert.Equal(0, RunGit("rev-parse", "--verify", "refs/heads/worktree/agent-fix").ExitCode);

    Result<IReadOnlyList<WorktreeInfo>> list = await access.ListAsync(_repoDir, ct: TestContext.Current.CancellationToken);
    Assert.True(list.IsSuccess);
    Assert.Equal(2, list.Value.Count);
    WorktreeInfo child = Assert.Single(list.Value, w => !w.IsMain);
    Assert.Equal("agent-fix", child.Name);
    Assert.Equal("worktree/agent-fix", child.Branch);
    Assert.False(child.IsDirty);
  }

  [Fact]
  public async Task CreateAsync_DuplicateName_ReturnsWorktreeExists()
  {
    GitWorktreeAccess access = new();
    Result<WorktreeInfo> first = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);
    Assert.True(first.IsSuccess);

    Result<WorktreeInfo> second = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);

    Assert.False(second.IsSuccess);
    Assert.Equal("WorktreeExists", second.Error.Code);
  }

  [Fact]
  public async Task CreateAsync_BranchAlreadyExists_ReturnsBranchExists()
  {
    Assert.Equal(0, RunGit("branch", "worktree/conflict").ExitCode);
    GitWorktreeAccess access = new();

    Result<WorktreeInfo> r = await access.CreateAsync(_repoDir, "conflict", ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("BranchExists", r.Error.Code);
  }

  [Fact]
  public async Task CreateAsync_InvalidName_ReturnsInvalidName()
  {
    GitWorktreeAccess access = new();

    Result<WorktreeInfo> r = await access.CreateAsync(_repoDir, "Agent_Fix", ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidName", r.Error.Code);
  }

  [Fact]
  public async Task CreateAsync_NullName_ReturnsInvalidNameInsteadOfThrowing()
  {
    GitWorktreeAccess access = new();

    Result<WorktreeInfo> r = await access.CreateAsync(_repoDir, null!, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidName", r.Error.Code);
  }

  [Fact]
  public async Task CreateAsync_StaleDirectoryOccupiesPath_PruneRunsAndCreateSucceeds()
  {
    _ = Directory.CreateDirectory(WorktreePath("stale"));
    await File.WriteAllTextAsync(Path.Combine(WorktreePath("stale"), "leftover.txt"), "remnant", TestContext.Current.CancellationToken);
    GitWorktreeAccess access = new();

    Result<WorktreeInfo> r = await access.CreateAsync(_repoDir, "stale", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.True(Directory.Exists(WorktreePath("stale")));
  }

  [Fact]
  public async Task CreateAsync_RegistersWorktreesInExclude()
  {
    GitWorktreeAccess access = new();

    Result<WorktreeInfo> r = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    string exclude = await File.ReadAllTextAsync(Path.Combine(_repoDir, ".git", "info", "exclude"), TestContext.Current.CancellationToken);
    Assert.Contains(".worktrees/", exclude, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RemoveAsync_CleanWorktree_SucceedsWithoutForce()
  {
    GitWorktreeAccess access = new();
    Result<WorktreeInfo> created = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess);

    Result<bool> r = await access.RemoveAsync(_repoDir, "agent-fix", force: false, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.True(r.Value);
    Assert.False(Directory.Exists(WorktreePath("agent-fix")));
  }

  [Fact]
  public async Task RemoveAsync_DirtyWorktree_WithoutForce_ReturnsWorktreeDirty()
  {
    GitWorktreeAccess access = new();
    Result<WorktreeInfo> created = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess);
    await File.WriteAllTextAsync(Path.Combine(WorktreePath("agent-fix"), "dirty.txt"), "x", TestContext.Current.CancellationToken);

    Result<bool> r = await access.RemoveAsync(_repoDir, "agent-fix", force: false, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("WorktreeDirty", r.Error.Code);
    Assert.True(Directory.Exists(WorktreePath("agent-fix")));
  }

  [Fact]
  public async Task RemoveAsync_DirtyWorktree_WithForce_Succeeds()
  {
    GitWorktreeAccess access = new();
    Result<WorktreeInfo> created = await access.CreateAsync(_repoDir, "agent-fix", ct: TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess);
    await File.WriteAllTextAsync(Path.Combine(WorktreePath("agent-fix"), "dirty.txt"), "x", TestContext.Current.CancellationToken);

    Result<bool> r = await access.RemoveAsync(_repoDir, "agent-fix", force: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.True(r.Value);
    Assert.False(Directory.Exists(WorktreePath("agent-fix")));
  }

  [Fact]
  public async Task RemoveAsync_UnknownName_ReturnsWorktreeNotFound()
  {
    GitWorktreeAccess access = new();

    Result<bool> r = await access.RemoveAsync(_repoDir, "ghost", force: false, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("WorktreeNotFound", r.Error.Code);
  }

  [Fact]
  public async Task ListAsync_OutsideRepo_ReturnsNotAGitRepository()
  {
    string plain = Path.Combine(Path.GetTempPath(), "ethang-not-a-repo-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(plain);
    try
    {
      GitWorktreeAccess access = new();
      Result<IReadOnlyList<WorktreeInfo>> r = await access.ListAsync(plain, ct: TestContext.Current.CancellationToken);
      Assert.False(r.IsSuccess);
      Assert.Equal("NotAGitRepository", r.Error.Code);
    }
    finally
    {
      Directory.Delete(plain, true);
    }
  }
}
