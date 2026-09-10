using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>The provisioner adapter delegates to the real git machinery: a provision
///     lands as a real worktree on disk (at root/.worktrees/name, branch worktree/name),
///     duplicate names refuse with WorktreeExists, and invalid names refuse with
///     InvalidName - the same contracts GitWorktreeAccessTests pin on the access.</summary>
public sealed class GitWorktreeProvisionerTests : IDisposable
{
  private readonly string _repoDir;
  private readonly GitWorktreeProvisioner _provisioner;

  public GitWorktreeProvisionerTests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "ethang-provisioner-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_repoDir);
    _ = RunGit("init", "-b", "main");
    _ = RunGit("config", "user.email", "test@example.com");
    _ = RunGit("config", "user.name", "Test");
    File.WriteAllText(Path.Combine(_repoDir, "seed.txt"), "seed");
    _ = RunGit("add", "seed.txt");
    _ = RunGit("commit", "-m", "seed");
    _provisioner = new GitWorktreeProvisioner(new GitWorktreeAccess());
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

  [Fact]
  public async Task CreateAsync_ProvisionsARealWorktree_AtTheDerivedPath()
  {
    Result<WorktreeProvision> result = await _provisioner.CreateAsync(_repoDir, "child-a", TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    WorktreeProvision provision = result.Value;
    Assert.Equal("child-a", provision.Name);
    Assert.Equal(Path.Combine(_repoDir, ".worktrees", "child-a"), provision.Path);
    Assert.Equal("worktree/child-a", provision.Branch);
    Assert.True(Directory.Exists(provision.Path));
    Assert.True(File.Exists(Path.Combine(provision.Path, "seed.txt")));
  }

  [Fact]
  public async Task CreateAsync_DuplicateName_RefusesWithWorktreeExists()
  {
    _ = await _provisioner.CreateAsync(_repoDir, "child-a", TestContext.Current.CancellationToken);

    Result<WorktreeProvision> again = await _provisioner.CreateAsync(_repoDir, "child-a", TestContext.Current.CancellationToken);

    Assert.False(again.IsSuccess);
    Assert.Equal("WorktreeExists", again.Error.Code);
  }

  [Fact]
  public async Task CreateAsync_InvalidName_RefusesWithInvalidName()
  {
    Result<WorktreeProvision> bad = await _provisioner.CreateAsync(_repoDir, "Bad Name!", TestContext.Current.CancellationToken);

    Assert.False(bad.IsSuccess);
    Assert.Equal("InvalidName", bad.Error.Code);
  }

  private (int ExitCode, string StdOut, string StdErr) RunGit(params string[] args)
  {
    ProcessStartInfo psi = new("git")
    {
      WorkingDirectory = _repoDir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (string a in args)
    {
      psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    string stdout = p.StandardOutput.ReadToEnd();
    string stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, stdout, stderr);
  }
}
