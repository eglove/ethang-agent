using System.Diagnostics;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class DirectGitAccessTests : IDisposable
{
  private readonly string _repoDir;

  public DirectGitAccessTests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "ethang-git-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_repoDir);
    RunGit("init");
    RunGit("config", "user.email", "test@test.com");
    RunGit("config", "user.name", "Test");
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

  private void RunGit(params string[] args)
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
    _ = p.WaitForExit(30000);
  }

  [Fact]
  public async Task GetStatusAsync_CleanRepo_ReturnsNoEntries()
  {
    DirectGitAccess access = new();

    Result<GitStatus> r = await access.GetStatusAsync(_repoDir, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Empty(r.Value.Staged);
    Assert.Empty(r.Value.Unstaged);
    Assert.Empty(r.Value.Untracked);
  }

  [Fact]
  public async Task GetStatusAsync_WithUntrackedFile_ReturnsEntry()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "test.txt"), "hello", TestContext.Current.CancellationToken);
    DirectGitAccess access = new();

    Result<GitStatus> r = await access.GetStatusAsync(_repoDir, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.NotEmpty(r.Value.Untracked);
  }

  [Fact]
  public async Task CommitAsync_WithStagedChange_ReturnsHash()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "staged.txt"), "content", TestContext.Current.CancellationToken);
    RunGit("add", "staged.txt");
    DirectGitAccess access = new();

    Result<GitCommitOutcome> r = await access.CommitAsync(_repoDir, "feat: test commit", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.NotEmpty(r.Value.Hash);
    Assert.Equal("feat: test commit", r.Value.Message);
  }

  [Fact]
  public async Task StageAsync_NamedFiles_AreStagedAndCommitExactlyThose()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "one.txt"), "1", TestContext.Current.CancellationToken);
    _ = Directory.CreateDirectory(Path.Combine(_repoDir, "sub"));
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "sub", "two.txt"), "2", TestContext.Current.CancellationToken);
    DirectGitAccess access = new();

    Result<bool> staged = await access.StageAsync(_repoDir, ["one.txt", "sub/two.txt"], ct: TestContext.Current.CancellationToken);
    Assert.True(staged.IsSuccess);

    Result<GitCommitOutcome> committed = await access.CommitAsync(_repoDir, "feat: two files", ct: TestContext.Current.CancellationToken);
    Assert.True(committed.IsSuccess);

    Result<GitStatus> status = await access.GetStatusAsync(_repoDir, ct: TestContext.Current.CancellationToken);
    Assert.True(status.IsSuccess);
    Assert.Empty(status.Value.Staged);
    Assert.Empty(status.Value.Unstaged);
    Assert.Empty(status.Value.Untracked);
  }

  [Fact]
  public async Task StageAsync_LeavesOtherModifiedFilesUnstaged()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "a.txt"), "a", TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "b.txt"), "b", TestContext.Current.CancellationToken);
    DirectGitAccess access = new();

    Result<bool> staged = await access.StageAsync(_repoDir, ["a.txt"], ct: TestContext.Current.CancellationToken);
    Assert.True(staged.IsSuccess);

    Result<GitStatus> status = await access.GetStatusAsync(_repoDir, ct: TestContext.Current.CancellationToken);
    Assert.True(status.IsSuccess);
    _ = Assert.Single(status.Value.Staged, e => e.Path == "a.txt");
    _ = Assert.Single(status.Value.Untracked, p => p == "b.txt");
  }

  [Fact]
  public async Task StageAsync_NonexistentPath_SurfacesGitError()
  {
    DirectGitAccess access = new();

    Result<bool> staged = await access.StageAsync(_repoDir, ["ghost.txt"], ct: TestContext.Current.CancellationToken);

    Assert.False(staged.IsSuccess);
    Assert.Equal("GitError", staged.Error.Code);
  }

  [Fact]
  public async Task StageAsync_OutsideRepo_SurfacesNotAGitRepository()
  {
    string plain = Path.Combine(Path.GetTempPath(), "ethang-not-a-repo-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(plain);
    try
    {
      DirectGitAccess access = new();
      Result<bool> staged = await access.StageAsync(plain, ["x.txt"], ct: TestContext.Current.CancellationToken);
      Assert.False(staged.IsSuccess);
      Assert.Equal("NotAGitRepository", staged.Error.Code);
    }
    finally
    {
      Directory.Delete(plain, true);
    }
  }
  [Fact]
  public async Task GetDiffAsync_CleanRepo_ReturnsEmptyDiff()
  {
    DirectGitAccess access = new();

    Result<GitDiff> r = await access.GetDiffAsync(_repoDir, "Unstaged", path: null, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(0, r.Value.Stats.Files);
  }
}
