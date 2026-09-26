using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// DirectGitAccess ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Integration for the child-report contract's cleanliness seam (grand-plan
///     BUG 3): the adapter lists untracked paths over a real git repository and
///     reports a non-repository directory as a typed failure the spawner stands
///     down on.</summary>
public sealed class WorkspaceCleanlinessCheckTests : IDisposable
{
  private readonly string _repoDir;
  private readonly string _plainDir;

  public WorkspaceCleanlinessCheckTests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "ethang-clean-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_repoDir);
    RunGit("init");
    RunGit("config", "user.email", "test@test.com");
    RunGit("config", "user.name", "Test");
    _plainDir = Path.Combine(Path.GetTempPath(), "ethang-clean-plain-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_plainDir);
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

    try
    {
      Directory.Delete(_plainDir, true);
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
    System.Diagnostics.ProcessStartInfo psi = new("git")
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

    using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
    _ = p.WaitForExit(30000);
  }

  [Fact]
  public async Task UntrackedFilesAsync_CleanRepo_ReturnsEmpty()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "tracked.txt"), "c", TestContext.Current.CancellationToken);
    RunGit("add", "tracked.txt");
    RunGit("commit", "-m", "init");
    WorkspaceCleanlinessCheck check = new(new DirectGitAccess());

    Result<IReadOnlyList<string>> r = await check.UntrackedFilesAsync(_repoDir, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Empty(r.Value);
  }

  [Fact]
  public async Task UntrackedFilesAsync_ScratchFilesPresent_ListsThemOnly()
  {
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "tracked.txt"), "c", TestContext.Current.CancellationToken);
    RunGit("add", "tracked.txt");
    RunGit("commit", "-m", "init");
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "stage_1.txt"), "scratch", TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(_repoDir, "notes.md"), "scratch", TestContext.Current.CancellationToken);
    WorkspaceCleanlinessCheck check = new(new DirectGitAccess());

    Result<IReadOnlyList<string>> r = await check.UntrackedFilesAsync(_repoDir, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.Value.Count);
    Assert.Contains("stage_1.txt", r.Value);
    Assert.Contains("notes.md", r.Value);
  }

  [Fact]
  public async Task UntrackedFilesAsync_NonRepositoryDirectory_FailsTyped()
  {
    WorkspaceCleanlinessCheck check = new(new DirectGitAccess());

    Result<IReadOnlyList<string>> r = await check.UntrackedFilesAsync(_plainDir, TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.NotNull(r.Error);
  }
}
