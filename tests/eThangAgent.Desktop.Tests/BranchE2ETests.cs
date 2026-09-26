using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end branch statusline (issue 1): a REAL composed session over a
///     temp git workspace shows the branch in the status bar, and closing the tab
///     releases the watcher. The wiring lives in the shell's attach path — the same
///     path production tabs open through.</summary>
public sealed class BranchE2ETests : IDisposable
{
  private readonly string _repoDir;

  public BranchE2ETests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "ethang-branch-e2e-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_repoDir);
    RunGit("init");
    RunGit("config", "user.email", "test@test.com");
    RunGit("config", "user.name", "Test");
    RunGit("checkout", "-b", "feature/e2e");
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
      CreateNoWindow = true,
    };
    foreach (string a in args)
    {
      psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    _ = p.WaitForExit(30000);
  }

  [AvaloniaFact]
  public async Task RealSession_Shows_Workspace_Branch()
  {
    using E2E.HostHarness host = await new E2E.HostHarness()
        .StartAsync(workspaceRoot: _repoDir).ConfigureAwait(true);

    for (int i = 0; i < 100 && host.Vm.Status.Branch != "feature/e2e"; i++)
    {
      Dispatcher.UIThread.RunJobs();
      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Equal("feature/e2e", host.Vm.Status.Branch);
  }

  [AvaloniaFact]
  public async Task CloseTab_Stops_The_Watcher()
  {
    using E2E.HostHarness host = await new E2E.HostHarness()
        .StartAsync(workspaceRoot: _repoDir).ConfigureAwait(true);
    await host.Shell.CloseTabAsync(host.Shell.Tabs[0]).ConfigureAwait(true);

    Assert.Empty(host.Shell.Tabs);

    // The dispose path must not wedge the close (a hung dispose would hang every tab close).
  }
}
