using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #8 layout contract: the workbench is TWO columns - an icon strip and a
///     content pane - and each owns its own grid column.
///     <para>The defect this pins: the root grid declared three columns while the content
///     pane was placed in column 3, which does not exist. Avalonia clamps the index to the
///     last column, so the content pane rendered ON TOP of the icon strip (hiding it
///     entirely) and its fixed width overflowed the window edge, cutting the text off.
///     A column index past the definition list is a silent overlap, never a build error,
///     so the contract has to be asserted against measured geometry.</para>
///     <para>ISSUE #307 MOVED THE HOST: the rail is workspace-scoped (D6), so it lives in
///     <see cref="WorkspaceTabView"/> - the tab - rather than in one agent's cell. Every
///     test here therefore hosts the TAB VIEW and reads the rail through it. The contract
///     under test is unchanged; only the view that carries the rail is.</para></summary>
public class WorkbenchLayoutTests
{
  [AvaloniaFact]
  public void WorkbenchContent_OccupiesItsOwnColumn_NotTheIconStrip()
  {
    (WorkspaceTabView view, Grid root) = Show(1200);
    Border strip = Strip(view);
    Border content = Content(view);

    int stripColumn = Grid.GetColumn(strip);
    int contentColumn = Grid.GetColumn(content);

    Assert.True(contentColumn < root.ColumnDefinitions.Count,
        $"content column {contentColumn} is past the {root.ColumnDefinitions.Count} declared column(s): " +
        "Avalonia clamps it onto the last column, overlapping whatever lives there.");
    Assert.NotEqual(stripColumn, contentColumn);
  }

  [AvaloniaFact]
  public void WorkbenchIconStrip_IsVisible_NotCoveredByTheContentPane()
  {
    (WorkspaceTabView view, Grid _) = Show(1200);
    Border strip = Strip(view);
    Border content = Content(view);

    // Both panes live in the same grid, so their bounds are comparable directly.
    double stripRight = strip.Bounds.Right;
    double contentLeft = content.Bounds.Left;

    Assert.True(stripRight <= contentLeft + 0.5,
        $"icon strip ends at {stripRight} but the content pane starts at {contentLeft}: they overlap.");
    Assert.True(strip.Bounds.Width > 0, "the icon strip must occupy real width");
  }

  [AvaloniaFact]
  public void WorkbenchContent_IsNotClipped_ByTheWindowEdge()
  {
    const double windowWidth = 1200;
    (WorkspaceTabView view, Grid root) = Show(windowWidth);
    Border content = Content(view);

    Assert.True(content.Bounds.Right <= root.Bounds.Width + 0.5,
        $"content pane ends at {content.Bounds.Right}, past the {root.Bounds.Width} root width: it is cut off.");
  }

  [AvaloniaFact]
  public void WorkbenchContent_KeepsItsIntendedWidth()
  {
    (WorkspaceTabView view, Grid _) = Show(1200);
    Border content = Content(view);

    // The pane declares a 320 DIP width; a column that cannot honor it squeezes the text
    // into a sliver, which is what the clamped layout did.
    Assert.True(content.Bounds.Width >= 300,
        $"content pane rendered {content.Bounds.Width} DIP wide, not the declared 320.");
  }

  [AvaloniaFact]
  public void CollapsingTheWorkbench_GivesTheGridTheFullWidth()
  {
    (WorkspaceTabView view, Grid root) = Show(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;
    double openGrid = GridWidth(view);

    tab.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(new GridLength(0), root.ColumnDefinitions[2].Width);
    Assert.True(GridWidth(view) > openGrid,
        $"collapsing must widen the grid (was {openGrid}, now {GridWidth(view)})");
  }

  [AvaloniaFact]
  public void ReopeningTheWorkbench_RestoresItsWidth()
  {
    (WorkspaceTabView view, Grid root) = Show(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;
    double openWidth = root.ColumnDefinitions[2].Width.Value;

    tab.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    tab.Workbench.IsOpen = true;
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(openWidth, root.ColumnDefinitions[2].Width.Value, 0.5);
  }

  [AvaloniaFact]
  public void CollapseButton_ClosesTheWorkbench_AndReopenButtonRestoresIt()
  {
    (WorkspaceTabView view, Grid root) = Show(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;
    Button collapse = view.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchCollapseButton");
    Button reopen = view.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchReopenButton");

    Assert.False(reopen.IsVisible, "reopen must stay hidden while the workbench is open");

    collapse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();

    Assert.False(tab.Workbench.IsOpen, "the collapse control must close the workbench");
    Assert.Equal(new GridLength(0), root.ColumnDefinitions[2].Width);
    Assert.True(reopen.IsVisible, "reopen must appear once the workbench is collapsed");

    reopen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();

    Assert.True(tab.Workbench.IsOpen, "reopen must bring the workbench back");
    Assert.True(root.ColumnDefinitions[2].Width.Value > 0);
  }

  /// <summary>A REAL pointer click on the collapse control must close the workbench.
  ///     <para>The sibling test above raises <c>Button.ClickEvent</c> directly, which
  ///     bypasses hit-testing entirely - so it passes even when nothing on screen can
  ///     reach the button. This test drives the headless platform's pointer instead:
  ///     press and release at the control's own center, translated into window
  ///     coordinates. If the button is covered, clipped, or zero-sized, the click lands
  ///     somewhere else and the workbench stays open.</para></summary>
  [AvaloniaFact]
  public void CollapseButton_ClosesTheWorkbench_OnARealPointerClick()
  {
    (WorkspaceTabView view, Grid _, Window window) = ShowWindow(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;
    Button collapse = view.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchCollapseButton");

    Point? center = collapse.TranslatePoint(
        new Point(collapse.Bounds.Width / 2, collapse.Bounds.Height / 2), window);
    Assert.True(center is not null, "the collapse control must be laid out inside the window");

    window.MouseDown(center.Value, MouseButton.Left);
    window.MouseUp(center.Value, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Assert.False(tab.Workbench.IsOpen, "a real pointer click on the collapse control must close the workbench");
  }

  /// <summary>The collapse control's glyph must be the character it means, not the
  ///     escape sequence that spells it.
  ///     <para>The defect this pins: the XAML carried <c>Content="\u00BB"</c>. AXAML is
  ///     XML, not C# - it does not interpret <c>\uXXXX</c> escapes (XML spells characters
  ///     as <c>&amp;#187;</c> or as the character itself), so the button rendered the six
  ///     literal characters <c>\u00BB</c> instead of a chevron. The tooltip worked, which
  ///     is why the control looked functional while its icon was unreadable.</para></summary>
  [AvaloniaFact]
  public void CollapseButton_Content_IsTheGlyphItself_NotTheRawEscape()
  {
    (WorkspaceTabView view, Grid _) = Show(1200);
    Button collapse = view.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchCollapseButton");

    string content = collapse.Content as string ?? "";
    Assert.DoesNotContain("\\u", content, StringComparison.Ordinal);
    Assert.Equal("\u00BB", content);
  }

  /// <summary>The collapse control must still receive a real pointer click when the view is
  ///     hosted the way PRODUCTION hosts it - one WorkspaceTabView per tab inside a
  ///     keep-alive ItemsControl, each wrapped in a Panel whose IsVisible follows tab
  ///     selection - at production window size.
  ///     <para>Every other test here builds a bare view in a bare Window, which is not
  ///     the shape the user clicks in. If the hosting wrapper swallowed pointer input, the
  ///     bare-view tests would all pass while the real app's button stayed dead.</para></summary>
  [AvaloniaFact]
  public void CollapseButton_ClosesTheWorkbench_WhenHostedLikeProduction()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    ItemsControl host = new()
    {
      ItemsSource = new[] { tab },
      ItemTemplate = new FuncDataTemplate<AgentTabViewModel>((_, _) =>
          new Panel { Children = { new WorkspaceTabView() } }, supportsRecycling: false),
    };
    Window window = new() { Width = 1920, Height = 1017, Content = host };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Button collapse = window.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchCollapseButton");
    Point? center = collapse.TranslatePoint(
        new Point(collapse.Bounds.Width / 2, collapse.Bounds.Height / 2), window);
    Assert.True(center is not null, "the collapse control must be laid out");

    window.MouseDown(center.Value, MouseButton.Left);
    window.MouseUp(center.Value, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Assert.False(tab.Workbench.IsOpen,
        "the collapse control must close the workbench when hosted like production");
  }

  /// <summary>Issue #10: selecting the GitHub icon renders the pull-request pane and hides the
  ///     other views. Like the Git test above, the assertion is against real visibility in the
  ///     SAME content border - a view-model flag alone never proved a pane drew.</summary>
  [AvaloniaFact]
  public void SelectingTheGitHubIcon_RendersThePullRequestPane_AndHidesTheOthers()
  {
    (WorkspaceTabView view, Grid _, Window window) = ShowWindow(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;

    tab.Workbench.Select(WorkbenchView.GitHub);
    Dispatcher.UIThread.RunJobs();

    ItemsControl pullRequests = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "PullRequestList");
    // The plans list is always realized (it is never gated on a count), so it is the reliable
    // witness that a DIFFERENT view is not the one on screen.
    ItemsControl planList = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "PlanCardList");

    Assert.True(pullRequests.IsEffectivelyVisible, "the pull-request list must render when the GitHub icon is selected");
    Assert.False(planList.IsEffectivelyVisible, "the plans list must not render at the same time");
    window.Close();
  }

  /// <summary>The GitHub icon is AVAILABLE: a dimmed icon is how the workbench says a view is
  ///     not built yet, and issue #10 built this one.</summary>
  [AvaloniaFact]
  public void TheGitHubIcon_IsAvailable_AndSelectable()
  {
    (WorkspaceTabView view, Grid _, Window window) = ShowWindow(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;

    WorkbenchIconViewModel icon = tab.Workbench.Icons.First(i => i.View == WorkbenchView.GitHub);
    Assert.True(icon.IsAvailable, "the GitHub view is built, so its icon must not be dimmed");

    tab.Workbench.Select(WorkbenchView.GitHub);
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(WorkbenchView.GitHub, tab.Workbench.SelectedView);
    Assert.True(tab.Workbench.IsGitHubSelected);
    window.Close();
  }

  /// <summary>Issue #9: selecting the Git icon renders the git pane and hides the other
  ///     views. The pane is one of several in the SAME content border, so the assertion
  ///     is against real visibility, not against the view-model flag alone.</summary>
  [AvaloniaFact]
  public void SelectingTheGitIcon_RendersTheGitPane_AndHidesTheOthers()
  {
    (WorkspaceTabView view, Grid _, Window window) = ShowWindow(1200);
    AgentTabViewModel tab = (AgentTabViewModel)view.DataContext!;

    tab.Workbench.Select(WorkbenchView.Git);
    Dispatcher.UIThread.RunJobs();

    ItemsControl changeList = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "GitChangeList");
    ItemsControl diffList = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "GitDiffList");
    ItemsControl planList = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "PlanCardList");

    Assert.True(changeList.IsEffectivelyVisible, "the git change list must render when the Git icon is selected");
    Assert.True(diffList.IsEffectivelyVisible, "the git diff must render when the Git icon is selected");
    Assert.False(planList.IsEffectivelyVisible, "the plans list must not render at the same time");
    window.Close();
  }

  /// <summary>The grid column's width, read from the cell scroll host itself so the
  ///     assertion never re-derives layout.</summary>

  /// <summary>Issue #312 D5: the Chains view is its OWN rail entry BESIDE Plans, so selecting it
  ///     renders the chain list and hides the plan list. The assertion is against real visibility
  ///     in the SAME content border - a view-model flag alone never proved a pane drew.</summary>
  [AvaloniaFact]
  public void SelectingTheChainsIcon_RendersTheChainPane_AndHidesThePlans()
  {
    AgentTabViewModel tab = TestFixtures.CreateTabWithChains([ParkedChain()]);
    Window window = new() { Width = 1200, Height = 600, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    WorkspaceTabView view = (WorkspaceTabView)window.Content;

    tab.Workbench.Select(WorkbenchView.Chains);
    Dispatcher.UIThread.RunJobs();

    ItemsControl chainList = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "ChainCardList");
    ItemsControl planList = view.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "PlanCardList");

    Assert.True(chainList.IsEffectivelyVisible, "the chain list must render when the Chains icon is selected");
    Assert.False(planList.IsEffectivelyVisible, "the plans list must not render at the same time");
    window.Close();
  }

  /// <summary>The Chains icon is AVAILABLE: a dimmed icon is how the workbench says a view is
  ///     not built yet, and issue #312 built this one.</summary>
  [AvaloniaFact]
  public void TheChainsIcon_IsAvailable_AndSelectable()
  {
    AgentTabViewModel tab = TestFixtures.CreateTabWithChains([ParkedChain()]);
    Window window = new() { Width = 1200, Height = 600, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    WorkbenchIconViewModel icon = tab.Workbench.Icons.First(i => i.View == WorkbenchView.Chains);
    Assert.True(icon.IsAvailable, "the Chains view is built, so its icon must not be dimmed");

    tab.Workbench.Select(WorkbenchView.Chains);
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(WorkbenchView.Chains, tab.Workbench.SelectedView);
    Assert.True(tab.Workbench.IsChainsSelected);
    window.Close();
  }

  /// <summary>Issue #312 D2, the criterion inherited from #303, asserted against the REAL
  ///     RENDERED CARD rather than the view-model: a parked chain's card must carry the parked
  ///     style class, and a running chain's must not. A card that looked identical in both cases
  ///     would undo the whole reason the record has a Parked status.</summary>
  [AvaloniaFact]
  public void AParkedChainCard_CarriesItsOwnStyleClass_ARunningOneDoesNot()
  {
    AgentTabViewModel tab = TestFixtures.CreateTabWithChains([ParkedChain(), RunningChain()]);
    Window window = new() { Width = 1200, Height = 900, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    WorkspaceTabView view = (WorkspaceTabView)window.Content;

    tab.Workbench.Select(WorkbenchView.Chains);
    Dispatcher.UIThread.RunJobs();

    // The class is asserted through the CARD it decorates rather than by counting borders: a
    // template realizes several borders per item, and a count would be a fact about Avalonia's
    // templating rather than about the distinction under test.
    List<Border> parkedCards = [.. view.GetVisualDescendants().OfType<Border>()
        .Where(b => b.Classes.Contains("chain-parked"))];
    Border parked = Assert.Single(parkedCards);
    Assert.True(((ChainCardViewModel)parked.DataContext!).IsParked,
        "only a parked chain's card may carry the parked class");
    Assert.DoesNotContain(
        view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is ChainCardViewModel { IsParked: false }),
        b => b.Classes.Contains("chain-parked"));
    window.Close();
  }

  /// <summary>A parked chain for the layout fixtures: one step claimed by a session, the chain
  ///     waiting at it with a reason.</summary>
  private static AgentDomain.Chain ParkedChain()
  {
    DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    AgentDomain.Chain chain = AgentDomain.Chain.New("ws", 3, 1, "parked chain", "sess-abcdef12", [1], now)
        with
    { Id = 1 };
    return chain with
    {
      Status = AgentDomain.ChainStatus.Parked,
      ParkReason = "step 1 failed 3 time(s), reaching the 3-attempt cap",
      ParkedAtPosition = 1,
    };
  }

  /// <summary>A running chain for the same fixtures, so the parked class can be shown to be a
  ///     DIFFERENCE rather than something every card carries.</summary>
  private static AgentDomain.Chain RunningChain()
  {
    DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    return AgentDomain.Chain.New("ws", 4, 1, "running chain", "sess-abcdef34", [1], now) with { Id = 2 };
  }

  private static double GridWidth(WorkspaceTabView view) => view.GetVisualDescendants()
      .OfType<ScrollViewer>().First(s => s.Name == "CellScroll").Bounds.Width;

  private static Border Strip(WorkspaceTabView view) => view.GetVisualDescendants().OfType<Border>()
      .First(b => b.Name == "WorkbenchStrip");

  private static Border Content(WorkspaceTabView view) => view.GetVisualDescendants().OfType<Border>()
      .First(b => b.Name == "WorkbenchContent");

  private static (WorkspaceTabView View, Grid Root) Show(double width)
  {
    (WorkspaceTabView view, Grid root, Window _) = ShowWindow(width);
    return (view, root);
  }

  /// <summary>Shows the view in a real (headless) window and hands back the window too, so
  ///     a test can drive pointer input through the platform the way a user does.</summary>
  private static (WorkspaceTabView View, Grid Root, Window Window) ShowWindow(double width)
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = width, Height = 600, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    WorkspaceTabView view = (WorkspaceTabView)window.Content;
    Grid root = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "TabRoot");
    return (view, root, window);
  }
}
