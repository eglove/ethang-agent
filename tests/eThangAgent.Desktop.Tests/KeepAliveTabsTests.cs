using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Regression for the tab-switch delay (grand-plan BUG 2): switching tabs
///     must not rebuild the transcript view. Tab content is keep-alive: one AgentView
///     per open tab, created at open, retained across selection changes — switches are
///     visibility toggles, so no re-render storm and no spinner/timer restart.
/// </summary>
public class KeepAliveTabsTests
{
  private static AgentSession FakeSession(string root)
  {
    return new AgentSession(
        new ServiceCollection().BuildServiceProvider(),
        AgentDomain.AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelDomain.ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: "openrouter",
        Inbox: new AgentDomain.BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }

  private static MainViewModel CreateShell()
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root)));
    return new MainViewModel(create);
  }

  private static AgentView? FindViewFor(Avalonia.Visual root, AgentSessionViewModel vm)
      => root.GetVisualDescendants().OfType<AgentView>()
          .FirstOrDefault(v => ReferenceEquals(v.DataContext, vm));

  [AvaloniaFact]
  public async Task Tab_Switch_Retains_The_Same_AgentView_Instance()
  {
    MainViewModel shell = CreateShell();
    MainWindow window = new(shell);
    window.Show();
    _ = await shell.OpenAgentAsync(@"C:\work\one", "openrouter").ConfigureAwait(true);
    _ = await shell.OpenAgentAsync(@"C:\work\two", "openrouter").ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel one = shell.Tabs[0].ViewModel;
    AgentView? viewOne = FindViewFor(window, one);
    Assert.NotNull(viewOne);

    // Switch away and back: the SAME view instance must still render the tab.
    shell.SelectedTab = shell.Tabs[1];
    Dispatcher.UIThread.RunJobs();
    shell.SelectedTab = shell.Tabs[0];
    Dispatcher.UIThread.RunJobs();

    AgentView? viewAfter = FindViewFor(window, one);
    Assert.NotNull(viewAfter);
    Assert.True(ReferenceEquals(viewOne, viewAfter),
        "tab content must be keep-alive: the same AgentView instance survives selection changes");
  }

  [AvaloniaFact]
  public async Task Background_Tab_Spinner_Keeps_Ticking_While_Busy()
  {
    MainViewModel shell = CreateShell();
    MainWindow window = new(shell);
    window.Show();
    _ = await shell.OpenAgentAsync(@"C:\work\one", "openrouter").ConfigureAwait(true);
    _ = await shell.OpenAgentAsync(@"C:\work\two", "openrouter").ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    // Tab 0 runs a busy turn (thinking phase); the user watches tab 1.
    AgentSessionViewModel one = shell.Tabs[0].ViewModel;
    shell.SelectedTab = shell.Tabs[1];
    Dispatcher.UIThread.RunJobs();

    one.Status.Phase = TurnPhase.Thinking;
    one.IsBusy = true;
    string frameBefore = one.Status.Spinner;
    Assert.NotEqual(string.Empty, frameBefore);

    // The keep-alive view's timer must still tick the background tab's spinner. The
    // assertion is that the frame MOVED, so the loop waits for a frame that differs
    // rather than for a duration: a fixed delay can land on a frame count that wraps the
    // ten-frame cycle back to where it started, and on a loaded machine it can land
    // before the timer has fired at all.
    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    string frameAfter = frameBefore;
    while (frameAfter == frameBefore && elapsed.Elapsed < TestCeiling.Value)
    {
      await Task.Delay(20).ConfigureAwait(true);
      Dispatcher.UIThread.RunJobs();
      frameAfter = one.Status.Spinner;
    }

    Assert.NotEqual(frameBefore, frameAfter);
  }
}
