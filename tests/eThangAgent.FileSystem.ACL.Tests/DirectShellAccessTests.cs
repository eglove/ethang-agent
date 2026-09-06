using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Integration tests: run the real shell (resolved the same way production resolves it)
// for exit codes, stderr capture, cwd anchoring, and timeout kills. Best-effort temp
// cleanup is deliberate; Process handles transfer to the code under test.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by

public sealed class DirectShellAccessTests : IDisposable
{
  private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

  private readonly string _workDir;

  public DirectShellAccessTests()
  {
    _workDir = Path.Combine(Path.GetTempPath(), "ethang-shell-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_workDir);
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_workDir, true);
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

  // ---- Shell resolution ----

  [Fact]
  public void ResolveShell_FindsAnExistingExecutable()
  {
    string shell = DirectShellAccess.ResolveShellForTests();

    Assert.True(File.Exists(shell), $"resolved shell does not exist: {shell}");
  }

  // ---- Execution semantics ----

  [Fact]
  public async Task RunAsync_ZeroExitCommand_ReportsExitCodeZeroAndStdout()
  {
    DirectShellAccess access = new();

    Result<ShellRun> r = await access.RunAsync(_workDir, "Write-Output hello-world", TestTimeout, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(0, r.Value.ExitCode);
    Assert.Contains("hello-world", r.Value.Output, StringComparison.Ordinal);
    Assert.False(r.Value.TimedOut);
  }

  [Fact]
  public async Task RunAsync_NonZeroExit_PropagatesExitCodeAndStderr()
  {
    DirectShellAccess access = new();

    Result<ShellRun> r = await access.RunAsync(
        _workDir, "Write-Error boom; exit 7", TestTimeout, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess); // the RUN succeeded; the command failed inside it
    Assert.Equal(7, r.Value.ExitCode);
    Assert.Contains("boom", r.Value.Output, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunAsync_CommandsSeeWorkspaceAsWorkingDirectory()
  {
    DirectShellAccess access = new();

    Result<ShellRun> r = await access.RunAsync(_workDir, "(Get-Location).Path", TestTimeout, TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(
        _workDir.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(),
        r.Value.Output.Trim().TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant());
  }

  [Fact]
  public async Task RunAsync_Timeout_KillsAndReportsPartialOutputWithTimedOutFlag()
  {
    DirectShellAccess access = new();

    // Start- Sleep keeps the shell alive past the budget; the preceding Write-Output
    // must still arrive in the captured output (partial capture before the kill).
    Result<ShellRun> r = await access.RunAsync(
        _workDir, "Write-Output partial; Start-Sleep -Seconds 30", TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.True(r.Value.TimedOut);
    Assert.Contains("partial", r.Value.Output, StringComparison.Ordinal);
  }
}
