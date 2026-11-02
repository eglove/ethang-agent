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

/// <summary>Issue #332, the RENDER half: a tab that starts a second agent shows BOTH cards, and
///     the card the user was reading still shows the conversation they were reading.
///     <para>THE VISUAL TREE IS THE ASSERTION, not the view-model. The defect this issue closes
///     is a missing SURFACE - one cell that morphed through every agent a workspace ever had -
///     so a view-model test alone would be green while the grid rendered one card. Each test
///     here hosts the real <see cref="WorkspaceTabView"/> in a headless window and counts the
///     rendered frames.</para>
///     <para>AND NOTHING STEALS THE VIEW (D2): a handoff that arrived mid-task is exactly the
///     case where a focus grab or a scroll would be worst, so the scroll offset and the focus
///     are asserted too.</para></summary>
public class CellAppendRenderingTests
{
  /// <summary>Starting a new session renders a SECOND card and leaves the first one's
  ///     transcript on screen.
  ///     <para>THE TEXT IS THE ASSERTION, not the cell count: a retired card that kept its
  ///     outline while rendering an empty transcript is the 'work is unreachable' failure in
  ///     its subtler form, and a count would have been green through it.</para></summary>
  [AvaloniaFact]
  public async Task StartingASecondAgent_RendersBothCards_AndKeepsTheFirstOnesTranscript()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    AgentCellViewModel original = Assert.Single(tab.Cells);
    original.ViewModel.Transcript.AddUser("the question the first agent was asked");

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go")
        .ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);

    Window window = Show(tab);

    // TWO CARDS ARE ON SCREEN: the new agent beside the one it superseded.
    Assert.Equal(2, RenderedFrames(window).Count);
    Assert.NotEmpty(Blocks(window, tab.Cells[^1]));
    // AND THE SUPERSEDED CARD STILL RENDERS WHAT WAS SAID IN IT.
    Assert.Contains(Blocks(window, original),
        block => block.Text == "the question the first agent was asked");
    window.Close();
  }

  /// <summary>The new card never takes the view (D2): the grid is still scrolled to its
  ///     origin and no cell's input holds focus, so an agent that arrives mid-task cannot pull
  ///     the reader away from the card they were reading.</summary>
  [AvaloniaFact]
  public async Task TheNewCard_NeitherScrollsNorFocuses()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);

    Result<AgentTabViewModel> started = await shell.ViewModel.StartNewSessionAsync(tab, "go")
        .ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);

    Window window = Show(tab);
    Dispatcher.UIThread.RunJobs();

    ScrollViewer grid = window.GetVisualDescendants().OfType<ScrollViewer>()
        .First(view => view.Name == "CellScroll");
    Assert.Equal(0, grid.Offset.X);
    Assert.Equal(0, grid.Offset.Y);
    Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(),
        input => input.IsFocused);
    window.Close();
  }

  /// <summary>Every rendered cell frame, found by the template's own name.
  ///     <para>COUNTED FROM THE VISUAL TREE, not from <c>tab.Cells</c>: the collection can hold
  ///     two cards while the grid renders one, which is the defect this file exists for.
  ///     </para></summary>
  /// <param name="window">The hosting window.</param>
  private static List<Border> RenderedFrames(Window window)
      => [.. window.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "CellFrame")];

  /// <summary>Every text block inside ONE cell's frame.
  ///     <para>SCOPED TO THE CELL, NEVER TO THE WINDOW: a tab holds several cells, so a
  ///     window-wide search would read the wrong card.</para></summary>
  /// <param name="window">The hosting window.</param>
  /// <param name="cell">The cell whose frame to search.</param>
  private static IEnumerable<SelectableTextBlock> Blocks(Window window, AgentCellViewModel cell)
      => window.GetVisualDescendants().OfType<Border>()
          .First(border => border.Name == "CellFrame" && ReferenceEquals(border.DataContext, cell))
          .GetVisualDescendants().OfType<SelectableTextBlock>();

  /// <summary>Shows the tab in a headless window so the cell template is really built.</summary>
  /// <param name="tab">The tab to host.</param>
  private static Window Show(AgentTabViewModel tab)
  {
    Window window = new() { Width = 900, Height = 600, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return window;
  }

  /// <summary>The shell under test, with a session factory that answers every launch, so the
  ///     append path under test is the shell's own.</summary>
  private sealed class Shell
  {
    public FakeRootTurnChannel Host { get; } = new([], holdOpen: false);

    public MainViewModel ViewModel { get; }

    public Shell()
      => ViewModel = new MainViewModel((root, provider) =>
          Task.FromResult(Result.Success(Session(root, provider))));

    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
      => (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;

    private AgentSession Session(string root, string provider)
    {
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentStore>(new TestFixtures.StubStore())
          .AddSingleton<IRootTurnChannel>(Host)
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
}
