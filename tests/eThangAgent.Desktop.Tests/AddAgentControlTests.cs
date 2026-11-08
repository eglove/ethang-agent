using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #333, the RENDER half: a tab carries a "+" control that adds one agent by
///     hand, and the click really reaches the shell's launch.
///     <para>THE VISUAL TREE IS THE ASSERTION, not the view-model. The defect this issue closes
///     is a missing SURFACE - AddCellAsync had no caller and the grid had no control - so a
///     view-model test alone would be green while no button existed to click. Each test here
///     hosts the real <see cref="WorkspaceTabView"/> in a headless window and drives the
///     control the way a user does.</para>
///     <para>THE CLICK IS A REAL POINTER CLICK where it can be: raising Button.ClickEvent
///     directly bypasses hit-testing, so it passes even when the control is covered, clipped,
///     or zero-sized. The control sits outside the scrolling grid precisely so it stays
///     reachable, and only a pointer click proves that.</para></summary>
[Trait("Category", "Process")]
public class AddAgentControlTests
{
  /// <summary>The control is on the tab and reachable at ANY cell count: with a grid tall
  ///     enough to scroll, the "+" is still laid out inside the window and still answers a
  ///     pointer click.
  ///     <para>WHY THE SCROLLED CASE IS THE ONE THAT MATTERS: a control placed at the end of the
  ///     scrolling cell row would be the first thing a full grid pushes out of reach, and a
  ///     test that only ever shows one cell would never see it.</para></summary>
  [AvaloniaFact]
  public async Task TheAddControl_IsReachable_WhenTheGridScrolls()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    for (int i = 0; i < 8; i++)
    {
      Result<AgentCellViewModel> added = await shell.ViewModel.AddCellAsync(tab, prompt: null)
          .ConfigureAwait(true);
      Assert.True(added.IsSuccess, added.Error?.Message);
    }

    Window window = Show(tab, width: 900, height: 600);
    ScrollViewer grid = CellScroll(window);
    Assert.True(grid.Extent.Height > grid.Viewport.Height,
        $"precondition: nine cells at 900x600 must overflow the grid " +
        $"(extent {grid.Extent.Height:F0} vs viewport {grid.Viewport.Height:F0})");

    Button add = AddButton(window);
    Assert.True(add.IsEffectivelyVisible, "the + control must render on a full grid");
    Point? center = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window);
    Assert.True(center is not null, "the + control must be laid out inside the window");
    Assert.True(window.Bounds.Contains(center.Value),
        $"the + control at {center.Value} must sit inside the window, not off its edge");
    window.Close();
  }

  /// <summary>Clicking "+" appends EXACTLY ONE agent cell, idle, after the ones already
  ///     there - and the click path is what produced it, not a direct call.
  ///     <para>COUNTED FROM THE VISUAL TREE: the collection can hold two cards while the grid
  ///     renders one, which is the failure mode this file exists for.</para></summary>
  [AvaloniaFact]
  public async Task ClickingAdd_AppendsOneIdleAgentCell()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    AgentCellViewModel original = Assert.Single(tab.Cells);
    Window window = Show(tab, width: 1200, height: 800);
    Assert.True(RenderedFrames(window).Count == 1, "precondition: one cell renders one frame");

    ClickAdd(window);
    PumpUntil(() => tab.Cells.Count == 2);
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(2, tab.Cells.Count);
    Assert.Equal(2, RenderedFrames(window).Count);
    AgentCellViewModel added = tab.Cells[^1];
    // APPENDED AFTER, never in place of: the agent that was there keeps its cell.
    Assert.Same(original, tab.Cells[0]);
    Assert.False(added.IsFinished, "a hand-added agent starts live, not as a finished card");
    Assert.False(added.IsStreaming, "a hand-added agent is idle until the user types");
    Assert.Equal(tab.Container.WorkspaceRoot, added.ViewModel.WorkspaceRoot);
    window.Close();
  }

  /// <summary>The click starts nothing by itself: no prompt is submitted, so the new agent's
  ///     transcript holds no user turn and its status shows no running turn.
  ///     <para>THE PROMPT IS THE WHOLE DIFFERENCE between this path and every other launch -
  ///     a "+" that fired a turn would be the harness inventing work.</para></summary>
  [AvaloniaFact]
  public async Task TheAddedAgent_StartsNoTurn()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    Window window = Show(tab, width: 1200, height: 800);

    ClickAdd(window);
    PumpUntil(() => tab.Cells.Count == 2);

    AgentSessionViewModel added = tab.Cells[^1].ViewModel;
    Assert.Empty(added.Transcript.Entries.OfType<UserMessageEntry>());
    Assert.False(added.IsBusy, "a hand-added agent must not be running a turn");
    Assert.Equal(0, shell.TurnsRun);
    window.Close();
  }

  /// <summary>The click never steals the view (parent D2): the grid is still scrolled to its
  ///     origin and no cell's input holds focus.
  ///     <para>THE SCROLL OFFSET IS SET DELIBERATELY FIRST. Asserting zero on a grid that was
  ///     never scrolled proves nothing: the defect is a click that MOVES a reader, so the test
  ///     puts the grid where a reader would be and then checks the click left it there.</para>
  ///     </summary>
  [AvaloniaFact]
  public async Task ClickingAdd_NeitherScrollsNorFocuses()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    for (int i = 0; i < 8; i++)
    {
      _ = await shell.ViewModel.AddCellAsync(tab, prompt: null).ConfigureAwait(true);
    }

    Window window = Show(tab, width: 900, height: 600);
    ScrollViewer grid = CellScroll(window);
    grid.Offset = grid.Offset.WithY(240);
    Dispatcher.UIThread.RunJobs();
    double before = grid.Offset.Y;
    Assert.True(before > 0, "precondition: the grid must really be scrolled");

    ClickAdd(window);
    PumpUntil(() => tab.Cells.Count == 10);
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(before, grid.Offset.Y, 0.5);
    Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), input => input.IsFocused);
    window.Close();
  }

  /// <summary>A creation that fails renders a notice ON THE TAB rather than doing nothing
  ///     visible: the click produced no cell, so there is no transcript to carry the reason.
  ///     <para>THE NOTICE MUST BE ABSENT BEFORE THE CLICK AND PRESENT AFTER IT, and that pair
  ///     is the assertion. A test that only reads the block after the failure is green through
  ///     a block that is ALWAYS visible: the text would be there, on a tab with nothing wrong
  ///     with it, and the 'visible notice' claim would be satisfied by a permanent label.
  ///     </para>
  ///     <para>THE BLOCK EXISTS IN THE TREE EVEN WHEN HIDDEN, which is why the visibility is
  ///     what is asserted rather than its presence.</para></summary>
  [AvaloniaFact]
  public async Task AFailedAdd_RendersAVisibleNotice()
  {
    Shell shell = new() { FailLaunches = true };
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    Window window = Show(tab, width: 1200, height: 800);
    Assert.True(tab.Cells.Count == 1, "precondition: the tab holds its one agent");

    SelectableTextBlock notice = window.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(block => block.Name == "AddAgentNoticeText");
    Assert.False(notice.IsEffectivelyVisible, "a tab with no refusal must render no notice");

    ClickAdd(window);
    PumpUntil(() => tab.HasAddAgentNotice);
    Dispatcher.UIThread.RunJobs();

    Assert.True(tab.Cells.Count == 1, "a refused click must not append a cell");
    Assert.True(notice.IsEffectivelyVisible, "the refusal must render, not merely be assigned");
    Assert.Contains("SessionLaunchFailed", notice.Text, StringComparison.Ordinal);
    window.Close();
  }

  /// <summary>A later successful click clears the earlier refusal: a stale reason beside the
  ///     agent it does not describe would read as that agent's failure.</summary>
  [AvaloniaFact]
  public async Task ASuccessfulAdd_ClearsTheEarlierNotice()
  {
    Shell shell = new() { FailLaunches = true };
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    Window window = Show(tab, width: 1200, height: 800);

    ClickAdd(window);
    PumpUntil(() => tab.HasAddAgentNotice);

    shell.FailLaunches = false;
    ClickAdd(window);
    PumpUntil(() => tab.Cells.Count == 2);
    Dispatcher.UIThread.RunJobs();

    Assert.False(tab.HasAddAgentNotice, "a successful click must not leave the old refusal up");
    SelectableTextBlock notice = window.GetVisualDescendants().OfType<SelectableTextBlock>()
        .First(block => block.Name == "AddAgentNoticeText");
    Assert.False(notice.IsEffectivelyVisible, "the cleared refusal must not render");
    window.Close();
  }

  /// <summary>A tab with no shell attached renders NO control: a button whose click goes
  ///     nowhere is worse than an absent one.</summary>
  [AvaloniaFact]
  public void ATabWithNoShell_RendersNoAddControl()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 900, Height = 600, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.False(tab.CanAddAgent);
    // A collapsed control is not in the visual tree at all, so this looks for it either way
    // and then requires that nothing renders: an absent control and a hidden one are the same
    // fact to a user, and either is acceptable - a VISIBLE one is not.
    Button? add = window.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(button => button.Name == "AddAgentButton");
    Assert.True(add is null || !add.IsEffectivelyVisible,
        "a + with nothing behind it must not render");
    window.Close();
  }

  /// <summary>The control's glyph is the character it means. AXAML is XML, not C#: a
  ///     <c>\u002B</c> escape would render six literal characters, so the button would look
  ///     functional while reading as garbage - the defect the rail's chevron already shipped
  ///     once.</summary>
  [AvaloniaFact]
  public async Task TheAddControl_Content_IsTheGlyphItself()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(@"C:\work\alpha").ConfigureAwait(true);
    Window window = Show(tab, width: 1200, height: 800);

    string content = AddButton(window).Content as string ?? "";
    Assert.Equal("+", content);
    window.Close();
  }

  /// <summary>Drives the control the way a user does: a real pointer press and release at its
  ///     own center, translated into window coordinates. A covered, clipped, or zero-sized
  ///     control receives nothing and the click lands elsewhere.</summary>
  /// <param name="window">The hosting window.</param>
  private static void ClickAdd(Window window)
  {
    Button add = AddButton(window);
    Assert.True(add.IsEffectivelyVisible, "the + control must be on screen before it is clicked");
    Point? center = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window);
    Assert.True(center is not null, "the + control must be laid out inside the window");
    window.MouseDown(center.Value, MouseButton.Left);
    window.MouseUp(center.Value, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
  }

  /// <summary>The "+" control, found by its own name so a rail button cannot be mistaken
  ///     for it.</summary>
  /// <param name="window">The hosting window.</param>
  private static Button AddButton(Window window)
      => window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "AddAgentButton");

  /// <summary>Every rendered cell frame, found by the template's own name.</summary>
  /// <param name="window">The hosting window.</param>
  private static List<Border> RenderedFrames(Window window)
      => [.. window.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "CellFrame")];

  /// <summary>The tab's cell grid's scroll viewer, which is what a click could move.</summary>
  /// <param name="window">The hosting window.</param>
  private static ScrollViewer CellScroll(Window window)
      => window.GetVisualDescendants().OfType<ScrollViewer>().First(view => view.Name == "CellScroll");

  /// <summary>Pumps the dispatcher until a click's launch has landed. The launch builds a
  ///     session off the UI thread, so a single <c>RunJobs</c> would race it.</summary>
  /// <param name="condition">The outcome to wait for.</param>
  private static void PumpUntil(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (!condition() && DateTimeOffset.UtcNow < deadline)
    {
      Dispatcher.UIThread.RunJobs();
      Thread.Sleep(5);
    }

    Dispatcher.UIThread.RunJobs();
    Assert.True(condition(), "the click path never completed.");
  }

  /// <summary>Shows the tab in a headless window so the cell template and the control are
  ///     really built.</summary>
  /// <param name="tab">The tab to host.</param>
  /// <param name="width">Window width.</param>
  /// <param name="height">Window height.</param>
  private static Window Show(AgentTabViewModel tab, double width, double height)
  {
    Window window = new()
    {
      Width = width,
      Height = height,
      Content = new WorkspaceTabView { DataContext = tab },
    };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return window;
  }

  /// <summary>The shell under test: a session factory that answers every launch, so the click
  ///     path under test is the shell's own. <see cref="FailLaunches"/> turns every launch
  ///     AFTER the tab is open into a typed refusal, which is what the notice path needs -
  ///     refusing the OPEN as well would leave no tab to click on.</summary>
  private sealed class Shell
  {
    public FakeRootTurnChannel Host { get; } = new([], holdOpen: false);

    public MainViewModel ViewModel { get; }

    /// <summary>When true every launch is refused with SessionLaunchFailed.</summary>
    public bool FailLaunches { get; set; }

    public Shell()
      => ViewModel = new MainViewModel((root, provider) =>
          Task.FromResult(FailLaunches
              ? Result.Failure<AgentSession>(new DomainError("SessionLaunchFailed",
                  "the workspace could not be opened."))
              : Result.Success(Session(root, provider))));

    /// <summary>Opens the tab with launches answering, so the tab exists to click in.
    ///     </summary>
    /// <param name="root">The workspace root.</param>
    /// <param name="provider">The provider name.</param>
    public async Task<AgentTabViewModel> OpenAsync(string root, string provider = "openrouter")
    {
      bool refusing = FailLaunches;
      FailLaunches = false;
      try
      {
        return (await ViewModel.OpenAgentAsync(root, provider).ConfigureAwait(true)).Value!;
      }
      finally
      {
        FailLaunches = refusing;
      }
    }

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

    /// <summary>How many turns reached the wire: a "+" must never fire one. Counted at the
    ///     HOST rather than at a runner, because a turn this shell starts travels there.</summary>
    public int TurnsRun => Host.TurnsSent;
  }
}
