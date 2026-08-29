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

    Result<GitStatus> r = await access.GetStatusAsync(_repoDir);

    Assert.True(r.IsSuccess);
    Assert.Empty(r.Value.Staged);
    Assert.Empty(r.Value.Unstaged);
    Assert.Empty(r.Value.Untracked);
  }

  [Fact]
  public async Task GetStatusAsync_WithUntrackedFile_ReturnsEntry()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "test.txt"), "hello");
    DirectGitAccess access = new();

    Result<GitStatus> r = await access.GetStatusAsync(_repoDir);

    Assert.True(r.IsSuccess);
    Assert.NotEmpty(r.Value.Untracked);
  }

  [Fact]
  public async Task CommitAsync_WithStagedChange_ReturnsHash()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "staged.txt"), "content");
    RunGit("add", "staged.txt");
    DirectGitAccess access = new();

    Result<GitCommitOutcome> r = await access.CommitAsync(_repoDir, "feat: test commit");

    Assert.True(r.IsSuccess);
    Assert.NotEmpty(r.Value.Hash);
    Assert.Equal("feat: test commit", r.Value.Message);
  }

  [Fact]
  public async Task GetDiffAsync_CleanRepo_ReturnsEmptyDiff()
  {
    DirectGitAccess access = new();

    Result<GitDiff> r = await access.GetDiffAsync(_repoDir, "Unstaged", path: null);

    Assert.True(r.IsSuccess);
    Assert.Equal(0, r.Value.Stats.Files);
  }
}
