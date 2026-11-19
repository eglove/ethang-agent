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

/// <summary>Every agent in the window renders LIVE at once: the grid shows all its cards
///     simultaneously, so there is no selection to switch and no background tab whose view
///     could be torn down and rebuilt. Each card's view is built once and retained for as long
///     as the card exists.</summary>
[Trait("Category", "Process")]
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
    return new MainViewModel(create, new MainViewModelOptions { WorkspaceRoot = @"C:\work\one" });
  }

  private static AgentView? FindViewFor(Avalonia.Visual root, AgentSessionViewModel vm)
      => root.GetVisualDescendants().OfType<AgentView>()
          .FirstOrDefault(v => ReferenceEquals(v.DataContext, vm));

  /// <summary>Pumps the dispatcher until the view for <paramref name="vm"/> is in the visual
  ///     tree, or the ceiling is spent. A single <c>RunJobs</c> only guarantees the queued jobs
  ///     ran; the layout pass that builds the card is scheduled behind them, so on a loaded
  ///     machine the view is not there yet and the lookup answers null for a correct shell.</summary>
  /// <param name="root">The window to search.</param>
  /// <param name="vm">The session view-model whose view is awaited.</param>
  private static AgentView? AwaitViewFor(Avalonia.Visual root, AgentSessionViewModel vm)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    AgentView? view = FindViewFor(root, vm);
    while (view is null && DateTimeOffset.UtcNow < deadline)
    {
      Dispatcher.UIThread.RunJobs();
      Thread.Sleep(5);
      view = FindViewFor(root, vm);
    }

    return view;
  }

  /// <summary>Every card renders at once: two agents in the window are two live views in the
  ///     visual tree, with no selection needed to make either one appear.</summary>
  [AvaloniaFact]
  public async Task EveryCard_RendersAtOnce()
  {
    MainViewModel shell = CreateShell();
    MainWindow window = new(shell);
    window.Show();
    _ = await shell.AddAgentInWorkspaceAsync().ConfigureAwait(true);
    _ = await shell.AddAgentInWorkspaceAsync().ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    AgentTabViewModel grid = Assert.Single(shell.Tabs);
    Assert.Equal(2, grid.Cells.Count);
    Assert.NotNull(AwaitViewFor(window, grid.Cells[0].ViewModel));
    Assert.NotNull(AwaitViewFor(window, grid.Cells[1].ViewModel));
    window.Close();
  }

  /// <summary>A card's view is RETAINED: adding a second agent must not rebuild the first
  ///     one's view, so a reader's transcript is never torn down and re-rendered underneath
  ///     them.</summary>
  [AvaloniaFact]
  public async Task AddingACard_DoesNotRebuildTheExistingViews()
  {
    MainViewModel shell = CreateShell();
    MainWindow window = new(shell);
    window.Show();
    _ = await shell.AddAgentInWorkspaceAsync().ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    AgentTabViewModel grid = Assert.Single(shell.Tabs);
    AgentSessionViewModel first = grid.Cells[0].ViewModel;
    AgentView? viewBefore = AwaitViewFor(window, first);
    Assert.NotNull(viewBefore);

    _ = await shell.AddAgentInWorkspaceAsync().ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    AgentView? viewAfter = AwaitViewFor(window, first);
    Assert.NotNull(viewAfter);
    Assert.True(ReferenceEquals(viewBefore, viewAfter),
        "a card's view must survive another agent arriving beside it");
    window.Close();
  }

  /// <summary>A busy card keeps its spinner ticking while the user works in another one:
  ///     every card renders live, so nothing pauses because it is not the focused one.</summary>
  [AvaloniaFact]
  public async Task ABusyCard_KeepsTicking_WhileAnotherIsWorkedIn()
  {
    MainViewModel shell = CreateShell();
    MainWindow window = new(shell);
    window.Show();
    _ = await shell.AddAgentInWorkspaceAsync().ConfigureAwait(true);
    _ = await shell.AddAgentInWorkspaceAsync().ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    AgentTabViewModel grid = Assert.Single(shell.Tabs);
    AgentSessionViewModel busy = grid.Cells[0].ViewModel;
    busy.Status.Phase = TurnPhase.Thinking;
    busy.IsBusy = true;
    string frameBefore = busy.Status.Spinner;
    Assert.NotEqual(string.Empty, frameBefore);

    // The assertion is that the frame MOVED, so the loop waits for a frame that differs
    // rather than for a duration: a fixed delay can land on a frame count that wraps the
    // ten-frame cycle back to where it started, and on a loaded machine it can land
    // before the timer has fired at all.
    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    string frameAfter = frameBefore;
    while (frameAfter == frameBefore && elapsed.Elapsed < TestCeiling.Value)
    {
      await Task.Delay(20).ConfigureAwait(true);
      Dispatcher.UIThread.RunJobs();
      frameAfter = busy.Status.Spinner;
    }

    Assert.NotEqual(frameBefore, frameAfter);
    window.Close();
  }
}
