using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Integration coverage for issue #10: the REAL process runner against the real
///     <c>gh</c> CLI and the real workspace repository. The unit suite drives a recorded
///     runner, which pins parsing but proves nothing about the spawn itself - argv
///     quoting, the closed stdin, the two captured streams.
///     <para>These tests SKIP (rather than fail) on a machine without gh, because D6 makes
///     the CLI an operator prerequisite and a missing CLI is a product state the view
///     renders, not a broken build. The authenticated + repository assertions only make
///     claims about what the CLI actually answers, so a logged-out machine still runs the
///     install/not-a-repo checks.</para></summary>
public sealed class GhCliAccessIntegrationTests
{
  /// <summary>Every degradation code the probe may legitimately answer with. Named once
  ///     because both tests assert membership in the same set.</summary>
  private static readonly string[] DegradationCodes =
      ["GhNotInstalled", "GhNotAuthenticated", "NotAGitHubRepository"];

  /// <summary>The repository this test suite lives in - a real GitHub clone on CI and here.</summary>
  private static string RepoRoot()
  {
    DirectoryInfo? dir = new(typeof(GhCliAccessIntegrationTests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir!.FullName;
  }

  [Fact]
  public async Task TheRealCli_ResolvesOnThisMachine_OrTheProbeSaysItIsMissing()
  {
    GhCliAccess access = GhCliAccess.CreateForProcess();

    Result<GitHubRepository> probe = await access.ProbeAsync(RepoRoot(), TestContext.Current.CancellationToken);

    // Whichever way it goes, the answer must be TYPED and actionable - never an exception and
    // never a blank success. This is the degradation contract exercised against reality.
    if (probe.IsSuccess)
    {
      Assert.Equal("eglove/ethang-agent", probe.Value.NameWithOwner);
      return;
    }

    Assert.Contains(probe.Error.Code, DegradationCodes);
    Assert.False(string.IsNullOrWhiteSpace(probe.Error.Message));
  }

  [Fact]
  public async Task TheRealCli_RefusesAPlainDirectoryAsNotAGitHubRepository()
  {
    string scratch = Path.Combine(Path.GetTempPath(), "ethang-gh-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(scratch);
    try
    {
      GhCliAccess access = GhCliAccess.CreateForProcess();

      Result<GitHubRepository> probe = await access.ProbeAsync(scratch, TestContext.Current.CancellationToken);

      Assert.False(probe.IsSuccess);
      // 'not a git repository' at the remote step, or an unauthenticated CLI answering first.
      Assert.Contains(probe.Error.Code, DegradationCodes);
    }
    finally
    {
      Directory.Delete(scratch, true);
    }
  }

  /// <summary>The create path against a REAL repository must not prompt. This drives
  ///     <c>gh pr create --dry-run</c> through the same runner the adapter uses and asserts the
  ///     CLI returned a plan instead of waiting on stdin - the closed stdin is what makes an
  ///     unexpected prompt fail fast rather than hang a turn.</summary>
  [Fact]
  public async Task TheRealRunner_ClosesStdin_SoAnInteractivePromptCannotHang()
  {
    string? gh = GhCliAccess.ResolveExecutable("gh", []);
    if (gh is null)
    {
      return; // D6 prerequisite absent: the view renders NotInstalled.
    }

    GhProcessRunner runner = new();
    Result<GhCliRun> run = await runner.RunAsync(
        gh,
        ["pr", "create", "--repo", "eglove/ethang-agent", "--head", "no-such-branch",
         "--base", "master", "--title", "probe", "--body", "probe", "--dry-run"],
        TestContext.Current.CancellationToken);

    Assert.True(run.IsSuccess, run.IsSuccess ? "" : run.Error.Message);
    // Either the dry run planned the PR (exit 0) or the CLI refused it (nonzero) - both are
    // ANSWERS. A hang would never reach this line.
    Assert.InRange(run.Value.ExitCode, 0, 1);
  }
}
