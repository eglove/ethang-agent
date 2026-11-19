using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>One workspace window shows one agent grid: no tab strip, no rail New Agent entry,
///     and no unread or parked-chain counters. The + above the grid is the only way to start an
///     agent, and it exists before any agent does.</summary>
[Trait("Category", "Process")]
public class SingleGridWindowTests
{
  /// <summary>The strip is GONE: the window renders no TabControl at all, so there is no
  ///     header strip to read an agent's name from and no tab to select.</summary>
  [AvaloniaFact]
  public void TheWindow_RendersNoTabStrip()
  {
    MainWindow window = new(CreateShellVm(@"C:\work\alpha"));
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Null(window.FindControl<TabControl>("AgentTabs"));
    Assert.Empty(window.GetVisualDescendants().OfType<TabControl>());
    window.Close();
  }

  /// <summary>The rail carries no New Agent entry: the + above the grid is the only way to
  ///     start an agent, so there is one control rather than two that can drift.</summary>
  [AvaloniaFact]
  public void TheRail_CarriesNoNewAgentEntry()
  {
    MainWindow window = new(CreateShellVm(@"C:\work\alpha"));
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Null(window.FindControl<Button>("OpenAgentMenuItem"));
    Border rail = window.GetControl<Border>("SideMenu");
    Assert.DoesNotContain(rail.GetVisualDescendants().OfType<Button>(),
        button => (button.Content as string) == "\u2795");
    window.Close();
  }

  /// <summary>The + exists BEFORE any agent does. A fresh window opens on its workspace with
  ///     the control already there, because it is the only way to start the first agent.</summary>
  [AvaloniaFact]
  public void TheAddControl_Exists_WithNoAgentOpen()
  {
    MainViewModel shell = CreateShellVm(@"C:\work\alpha");
    MainWindow window = new(shell);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Empty(shell.Tabs);
    Button add = window.GetControl<Button>("AddAgentButton");
    Assert.True(add.IsEffectivelyVisible, "the + must render on a window holding no agent");
    Assert.True(add.IsEffectivelyEnabled, "the + must be clickable on an empty window");
    window.Close();
  }

  /// <summary>Closing the last agent card leaves the window OPEN on its empty pane, with the +
  ///     still there: the card's x closes an agent, and the window's own close button is the exit.</summary>
  [AvaloniaFact]
  public async Task ClosingTheLastCard_LeavesTheWindowOpen()
  {
    MainViewModel shell = CreateShellVm(@"C:\work\alpha");
    MainWindow window = new(shell);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    _ = await shell.OpenAgentInWorkspaceAsync().ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();
    AgentTabViewModel grid = Assert.Single(shell.Tabs);
    AgentCellViewModel only = Assert.Single(grid.Cells);

    Assert.True(grid.DismissCell(only), "the last card is closable");
    Dispatcher.UIThread.RunJobs();

    Assert.True(window.IsVisible, "closing the last agent must not close the window");
    Assert.True(window.GetControl<Button>("AddAgentButton").IsEffectivelyVisible,
        "the + must still be there once the last agent is gone");
    window.Close();
  }

  /// <summary>Starting a second agent adds a CARD to the window's one grid, not a tab: the
  ///     window holds one grid however many agents run in it.</summary>
  [AvaloniaFact]
  public async Task ASecondAgent_AddsACard_NotATab()
  {
    MainViewModel shell = CreateShellVm(@"C:\work\alpha");
    _ = await shell.OpenAgentInWorkspaceAsync().ConfigureAwait(true);
    AgentTabViewModel grid = Assert.Single(shell.Tabs);

    Result<AgentCellViewModel> added = await shell.AddCellAsync(grid, prompt: null).ConfigureAwait(true);

    Assert.True(added.IsSuccess, added.Error?.Message);
    _ = Assert.Single(shell.Tabs);
    Assert.Equal(2, grid.Cells.Count);
  }

  /// <summary>Resuming a session brings it back as a CARD in the window's grid, the same act as
  ///     + but carrying the old transcript - there is no strip for it to open into.</summary>
  [AvaloniaFact]
  public async Task ResumingASession_AppendsACard()
  {
    MainViewModel shell = CreateShellVm(@"C:\work\alpha");
    _ = await shell.OpenAgentInWorkspaceAsync().ConfigureAwait(true);
    AgentTabViewModel grid = Assert.Single(shell.Tabs);

    Result<AgentTabViewModel> resumed = await shell.ResumeSessionAsync(AgentId.NewId()).ConfigureAwait(true);

    Assert.True(resumed.IsSuccess, resumed.Error?.Message);
    _ = Assert.Single(shell.Tabs);
    Assert.Equal(2, grid.Cells.Count);
  }

  /// <summary>Every open agent is reported to the Sessions dialog, not only the first card of
  ///     the window's grid: a resumed session must be greyed out too, or resuming it forks it.</summary>
  [AvaloniaFact]
  public async Task OpenSessionIds_ListEveryCard()
  {
    MainViewModel shell = CreateShellVm(@"C:\work\alpha");
    _ = await shell.OpenAgentInWorkspaceAsync().ConfigureAwait(true);
    AgentTabViewModel grid = Assert.Single(shell.Tabs);
    _ = await shell.AddCellAsync(grid, prompt: null).ConfigureAwait(true);

    Assert.Equal(2, grid.Cells.Count);
    Assert.Equal(2, shell.OpenSessionIds.Count);
    Assert.Contains(grid.Cells[0].RootId, shell.OpenSessionIds);
    Assert.Contains(grid.Cells[1].RootId, shell.OpenSessionIds);
  }

  private static MainViewModel CreateShellVm(string workspaceRoot)
    => new((root, provider) => Task.FromResult(Result.Success(Session(root, provider))),
        new MainViewModelOptions
        {
          WorkspaceRoot = workspaceRoot,
          ResumeSessionCreator = _ => Task.FromResult(Result.Success(Session(workspaceRoot, "openrouter"))),
        });

  private static AgentSession Session(string root, string provider)
  {
    ServiceProvider services = new ServiceCollection()
        .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
        .BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
  }
}
