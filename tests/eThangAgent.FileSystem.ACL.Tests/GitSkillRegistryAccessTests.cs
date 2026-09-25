// Integration fixture: real git against local fixture repositories (offline);
// sync temp-dir IO and best-effort cleanup are deliberate.
using System.Diagnostics;
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>GitSkillRegistryAccess against real local git fixture repos
/// (plan #29 task 7): clone/promote/remove semantics, credential refusal,
/// stderr-tail errors, and the never-delete-a-configured-root guard.</summary>
public sealed class GitSkillRegistryAccessTests : IDisposable
{
  private readonly string _repoDir;
  private readonly string _stagingParent;
  private readonly string _targetDir;

  public GitSkillRegistryAccessTests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "regfix-" + Guid.NewGuid().ToString("N"));
    _stagingParent = Path.Combine(Path.GetTempPath(), "regfix-stage-" + Guid.NewGuid().ToString("N"));
    _targetDir = Path.Combine(Path.GetTempPath(), "regfix-target-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_repoDir);
    _ = Directory.CreateDirectory(_stagingParent);
    _ = Directory.CreateDirectory(_targetDir);
    _ = RunGit("init", "-b", "main");
    _ = RunGit("config", "user.email", "test@example.com");
    _ = RunGit("config", "user.name", "Test");
    _ = Directory.CreateDirectory(Path.Combine(_repoDir, "skills", "alpha"));
    File.WriteAllText(Path.Combine(_repoDir, "skills", "alpha", "SKILL.md"),
        "---\nname: alpha\ndescription: fixture skill\n---\nbody text");
    _ = Directory.CreateDirectory(Path.Combine(_repoDir, "skills", "alpha", "references"));
    File.WriteAllText(Path.Combine(_repoDir, "skills", "alpha", "references", "guide.md"), "# guide");
    _ = RunGit("add", ".");
    _ = RunGit("commit", "-m", "seed skill");
  }

  public void Dispose()
  {
    foreach (string dir in new[] { _repoDir, _stagingParent, _targetDir })
    {
      try
      {
        Directory.Delete(dir, true);
      }
      catch (IOException)
      {
        // best-effort temp cleanup
      }
      catch (UnauthorizedAccessException)
      {
        // best-effort temp cleanup
      }
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
  [Fact]
  public async Task CloneAsync_LocalFixture_ClonesIntoStaging()
  {
    GitSkillRegistryAccess access = new();
    string staging = Path.Combine(_stagingParent, "clone-1");
    Result<string> r = await access.CloneAsync(new CloneRequest(new Uri(_repoDir), staging), TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.True(File.Exists(Path.Combine(staging, "skills", "alpha", "SKILL.md")));
  }

  [Fact]
  public async Task CloneAsync_MissingSource_FailsWithGitTail()
  {
    GitSkillRegistryAccess access = new();
    string staging = Path.Combine(_stagingParent, "clone-2");
    Result<string> r = await access.CloneAsync(
        new CloneRequest(new Uri("https://example.invalid/no-such-repo.git"), staging), TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("CloneFailed", r.Error.Code);
    Assert.False(string.IsNullOrWhiteSpace(r.Error.Message));
  }

  [Fact]
  public async Task CloneAsync_UserInfoInUrl_RefusedWithoutSpawningGit()
  {
    GitSkillRegistryAccess access = new();
    string staging = Path.Combine(_stagingParent, "clone-3");
    Result<string> r = await access.CloneAsync(
        new CloneRequest(new Uri("https://user:pass@example.invalid/o/r.git"), staging), TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("CredentialsRejected", r.Error.Code);
  }

  [Fact]
  public async Task PromoteAsync_FreshTarget_CopiesRecursively()
  {
    GitSkillRegistryAccess access = new();
    string staging = Path.Combine(_stagingParent, "clone-4");
    _ = await access.CloneAsync(new CloneRequest(new Uri(_repoDir), staging), TestContext.Current.CancellationToken);
    Result<string> r = await access.PromoteAsync(
        new PromoteRequest(Path.Combine(staging, "skills", "alpha"), _targetDir, Force: false), TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.True(Directory.Exists(Path.Combine(_targetDir, "alpha", "references")));
    Assert.True(File.Exists(Path.Combine(_targetDir, "alpha", "references", "guide.md")));
  }

  [Fact]
  public async Task PromoteAsync_ExistingWithoutForce_FailsDestinationExists()
  {
    GitSkillRegistryAccess access = new();
    string staging = Path.Combine(_stagingParent, "clone-5");
    _ = await access.CloneAsync(new CloneRequest(new Uri(_repoDir), staging), TestContext.Current.CancellationToken);
    _ = await access.PromoteAsync(new PromoteRequest(Path.Combine(staging, "skills", "alpha"), _targetDir, Force: false), TestContext.Current.CancellationToken);
    Result<string> r = await access.PromoteAsync(
        new PromoteRequest(Path.Combine(staging, "skills", "alpha"), _targetDir, Force: false), TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("DestinationExists", r.Error.Code);
  }

  [Fact]
  public async Task PromoteAsync_MissingTarget_FailsTargetMissing()
  {
    GitSkillRegistryAccess access = new();
    string staging = Path.Combine(_stagingParent, "clone-6");
    _ = await access.CloneAsync(new CloneRequest(new Uri(_repoDir), staging), TestContext.Current.CancellationToken);
    Result<string> r = await access.PromoteAsync(
        new PromoteRequest(Path.Combine(staging, "skills", "alpha"), Path.Combine(_stagingParent, "missing-target"), Force: false),
        TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("TargetMissing", r.Error.Code);
  }

  [Fact]
  public async Task RemoveAsync_ExistingFolder_Removes()
  {
    GitSkillRegistryAccess access = new();
    string dir = Path.Combine(_targetDir, "alpha");
    _ = Directory.CreateDirectory(dir);
    Result<string> r = await access.RemoveAsync(dir, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.False(Directory.Exists(dir));
  }

  [Fact]
  public async Task RemoveAsync_DriveRoot_Refused()
  {
    GitSkillRegistryAccess access = new();
    string root = Path.GetPathRoot(Path.GetFullPath(_targetDir))!;
    Result<string> r = await access.RemoveAsync(root, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("ConfiguredRootRefused", r.Error.Code);
    Assert.True(Directory.Exists(root));
  }
}
