using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.FileSystem.ACL;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Process ownership transfers to the using scope
#pragma warning disable CA1416 // Platform compatibility (DPAPI-free file IO here)
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Desktop.Tests;

/// <summary>Statusline branch (issue 1): the status bar shows the workspace's current
///     git branch and updates when the branch changes. The watcher is event-driven —
///     a FileSystemWatcher over .git/HEAD feeds StatusViewModel.Branch; a non-git
///     workspace shows nothing.</summary>
public sealed class GitBranchStatusTests : IDisposable
{
  private readonly string _repoDir;

  public GitBranchStatusTests()
  {
    _repoDir = Path.Combine(Path.GetTempPath(), "ethang-branch-tests-" + Guid.NewGuid().ToString("N"));
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
      CreateNoWindow = true,
    };
    foreach (string a in args)
    {
      psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    _ = p.WaitForExit(30000);
  }

  [Fact]
  public void StatusViewModel_Branch_StartsEmpty()
  {
    StatusViewModel status = new("OpenRouter", "test/model", "Model default");

    Assert.Equal("", status.Branch);
  }

  [Fact]
  public void StatusViewModel_SetBranch_UpdatesDisplay()
  {
#pragma warning disable IDE0017 // The test mutates after construction on purpose: the setter's change notification is the behavior under test.
    StatusViewModel status = new("OpenRouter", "test/model", "Model default");
    status.Branch = "feature/demo";
#pragma warning restore IDE0017 // The test mutates after construction on purpose: the setter's change notification is the behavior under test.


    Assert.Equal("feature/demo", status.Branch);
  }

  [AvaloniaFact]
  public async Task Watcher_ReportsInitialBranch()
  {
    RunGit("checkout", "-b", "feature/demo");
    StatusViewModel status = new("OpenRouter", "test/model", "Model default");
    DirectGitAccess git = new();

    using GitBranchWatcher watcher = new(_repoDir, git, status);
    bool settled = await WaitAsync(() => status.Branch == "feature/demo").ConfigureAwait(true);

    Assert.True(settled, $"branch never became feature/demo; was '{status.Branch}'");
  }

  [AvaloniaFact]
  public async Task Watcher_UpdatesOnBranchChange()
  {
    StatusViewModel status = new("OpenRouter", "test/model", "Model default");
    DirectGitAccess git = new();

    using GitBranchWatcher watcher = new(_repoDir, git, status);
    _ = await WaitAsync(() => status.Branch.Length > 0).ConfigureAwait(true);

    RunGit("checkout", "-b", "next/branch");
    bool changed = await WaitAsync(() => status.Branch == "next/branch").ConfigureAwait(true);

    Assert.True(changed, $"branch never became next/branch; was '{status.Branch}'");
  }

  [AvaloniaFact]
  public async Task Watcher_NonGitWorkspace_ShowsNothing()
  {
    string plain = Path.Combine(Path.GetTempPath(), "ethang-plain-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(plain);
    try
    {
      StatusViewModel status = new("OpenRouter", "test/model", "Model default");
      DirectGitAccess git = new();

      using GitBranchWatcher watcher = new(plain, git, status);
      await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Equal("", status.Branch);
    }
    finally
    {
      Directory.Delete(plain, true);
    }
  }

  [AvaloniaFact]
  public void Status_Bar_Shows_Branch_And_Hides_When_Empty()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    TextBlock branch = view.GetControl<TextBlock>("BranchText");

    Assert.False(branch.IsVisible);

    vm.Status.Branch = "feature/demo";

    Assert.True(branch.IsVisible);
    Assert.Equal("feature/demo", branch.Text);
  }

  /// <summary>Waits up to five seconds for the condition, pumping the dispatcher so
  ///     marshaled property updates land.</summary>
  private static async Task<bool> WaitAsync(Func<bool> condition)
  {
    for (int i = 0; i < 100; i++)
    {
      if (condition())
      {
        return true;
      }

      Dispatcher.UIThread.RunJobs();
      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    return condition();
  }
}
