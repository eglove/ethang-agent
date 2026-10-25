using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
///     so the contract has to be asserted against measured geometry.</para></summary>
public class WorkbenchLayoutTests
{
  [AvaloniaFact]
  public void WorkbenchContent_OccupiesItsOwnColumn_NotTheIconStrip()
  {
    (AgentView view, Grid root) = Show(1200);
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
    (AgentView view, Grid _) = Show(1200);
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
    (AgentView view, Grid root) = Show(windowWidth);
    Border content = Content(view);

    Assert.True(content.Bounds.Right <= root.Bounds.Width + 0.5,
        $"content pane ends at {content.Bounds.Right}, past the {root.Bounds.Width} root width: it is cut off.");
  }

  [AvaloniaFact]
  public void WorkbenchContent_KeepsItsIntendedWidth()
  {
    (AgentView view, Grid _) = Show(1200);
    Border content = Content(view);

    // The pane declares a 320 DIP width; a column that cannot honor it squeezes the text
    // into a sliver, which is what the clamped layout did.
    Assert.True(content.Bounds.Width >= 300,
        $"content pane rendered {content.Bounds.Width} DIP wide, not the declared 320.");
  }

  [AvaloniaFact]
  public void CollapsingTheWorkbench_GivesTheChatTheFullWidth()
  {
    (AgentView view, Grid root) = Show(1200);
    AgentSessionViewModel vm = (AgentSessionViewModel)view.DataContext!;
    double openChat = ChatWidth(view);

    vm.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(new GridLength(0), root.ColumnDefinitions[2].Width);
    Assert.True(ChatWidth(view) > openChat,
        $"collapsing must widen the chat (was {openChat}, now {ChatWidth(view)})");
  }

  [AvaloniaFact]
  public void ReopeningTheWorkbench_RestoresItsWidth()
  {
    (AgentView view, Grid root) = Show(1200);
    AgentSessionViewModel vm = (AgentSessionViewModel)view.DataContext!;
    double openWidth = root.ColumnDefinitions[2].Width.Value;

    vm.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    vm.Workbench.IsOpen = true;
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(openWidth, root.ColumnDefinitions[2].Width.Value, 0.5);
  }

  [AvaloniaFact]
  public void CollapseButton_ClosesTheWorkbench_AndReopenButtonRestoresIt()
  {
    (AgentView view, Grid root) = Show(1200);
    AgentSessionViewModel vm = (AgentSessionViewModel)view.DataContext!;
    Button collapse = view.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchCollapseButton");
    Button reopen = view.GetVisualDescendants().OfType<Button>()
        .First(b => b.Name == "WorkbenchReopenButton");

    Assert.False(reopen.IsVisible, "reopen must stay hidden while the workbench is open");

    collapse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();

    Assert.False(vm.Workbench.IsOpen, "the collapse control must close the workbench");
    Assert.Equal(new GridLength(0), root.ColumnDefinitions[2].Width);
    Assert.True(reopen.IsVisible, "reopen must appear once the workbench is collapsed");

    reopen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();

    Assert.True(vm.Workbench.IsOpen, "reopen must bring the workbench back");
    Assert.True(root.ColumnDefinitions[2].Width.Value > 0);
  }

  /// <summary>The chat column's width, read from the chat DockPanel itself so the
  ///     assertion never re-derives layout.</summary>
  private static double ChatWidth(AgentView view) => view.GetVisualDescendants().OfType<DockPanel>()
      .First(d => Grid.GetColumn(d) == 0 && d.Parent is Grid g && g.Name == "RootSplit").Bounds.Width;

  private static Border Strip(AgentView view) => view.GetVisualDescendants().OfType<Border>()
      .First(b => b.Name == "WorkbenchStrip");

  private static Border Content(AgentView view) => view.GetVisualDescendants().OfType<Border>()
      .First(b => b.Name == "WorkbenchContent");

  private static (AgentView View, Grid Root) Show(double width)
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Width = width, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AgentView view = (AgentView)window.Content;
    Grid root = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RootSplit");
    return (view, root);
  }
}
