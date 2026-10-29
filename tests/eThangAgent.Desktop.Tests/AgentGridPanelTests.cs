using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #307 D8's layout contract, measured against REAL geometry: the panel
///     divides the available space evenly, every cell holds the 480 x 360 minimum, and past
///     what fits the grid scrolls rather than shrinking a cell.
///     <para><see cref="AgentGridLayoutTests"/> pins the arithmetic; this pins that the panel
///     actually APPLIES it, because a plan nobody arranges to is a plan that does nothing.</para></summary>
public class AgentGridPanelTests
{
  [AvaloniaFact]
  public void OneCell_FillsTheWholeViewport()
  {
    (AgentGridPanel panel, Window window) = Show(1, 1200, 800);

    Control cell = panel.Children[0];
    Assert.Equal(panel.Bounds.Width, cell.Bounds.Width, 1.0);
    Assert.Equal(panel.Bounds.Height, cell.Bounds.Height, 1.0);
    window.Close();
  }

  [AvaloniaFact]
  public void TwoCells_SplitIntoTwoEvenColumns()
  {
    (AgentGridPanel panel, Window window) = Show(2, 1200, 800);

    Assert.Equal(2, panel.LastPlan.Columns);
    Assert.Equal(panel.Bounds.Width / 2, panel.Children[0].Bounds.Width, 1.0);
    Assert.Equal(panel.Children[0].Bounds.Width, panel.Children[1].Bounds.Width, 1.0);
    // Side by side, not stacked: the second cell starts where the first ends.
    Assert.Equal(panel.Children[0].Bounds.Right, panel.Children[1].Bounds.Left, 1.0);
    window.Close();
  }

  [AvaloniaFact]
  public void ThreeCells_TileThreeColumns()
  {
    (AgentGridPanel panel, Window window) = Show(3, 1500, 800);

    Assert.Equal(3, panel.LastPlan.Columns);
    Assert.Equal(1, panel.LastPlan.Rows);
    Assert.Equal(panel.Bounds.Width / 3, panel.Children[2].Bounds.Width, 1.0);
    window.Close();
  }

  /// <summary>D8's measured number: a 1654 x 1024 grid viewport gives three columns by two
  ///     rows, so six agents are visible - not eight. The panel must produce that, not a more
  ///     flattering layout.</summary>
  [AvaloniaFact]
  public void SixCells_AreThreeColumnsByTwoRows()
  {
    (AgentGridPanel panel, Window window) = Show(6, 1654, 1024);

    Assert.Equal(3, panel.LastPlan.Columns);
    Assert.Equal(2, panel.LastPlan.Rows);
    // Row two sits below row one.
    Assert.True(panel.Children[3].Bounds.Top >= panel.Children[0].Bounds.Bottom - 0.5,
        "the fourth cell must start a second row");
    window.Close();
  }

  /// <summary>No cell maximum (D8): more agents than fit produce MORE ROWS at the minimum
  ///     cell height, and the surrounding scroll viewer is what makes the rest reachable.</summary>
  [AvaloniaFact]
  public void ManyCells_KeepTheMinimumCellHeight_AndGrowRows()
  {
    (AgentGridPanel panel, Window window) = Show(9, 1654, 1024);

    Assert.Equal(3, panel.LastPlan.Columns);
    Assert.Equal(3, panel.LastPlan.Rows);
    Assert.Equal(AgentGridLayout.MinCellHeight, panel.Children[0].Bounds.Height, 1.0);
    window.Close();
  }

  /// <summary>The panel must not oscillate. An earlier version set each cell's Width and
  ///     Height from a size-changed handler, feeding its own desired size back into the next
  ///     pass; a real pointer click then never completed because the control moved between
  ///     press and release. Arrange is a pure function of the final size, so repeated layout
  ///     passes must produce identical geometry.</summary>
  [AvaloniaFact]
  public void RepeatedLayout_ProducesIdenticalGeometry()
  {
    (AgentGridPanel panel, Window window) = Show(4, 1400, 900);
    Rect first = panel.Children[3].Bounds;

    panel.InvalidateMeasure();
    panel.InvalidateArrange();
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(first, panel.Children[3].Bounds);
    window.Close();
  }

  /// <summary>The tab view hosts the panel, so a tab really renders a grid rather than a
  ///     single chat: the cell frames are the panel's children and they are laid out.</summary>
  [AvaloniaFact]
  public void WorkspaceTabView_HostsTheGridPanel()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1200, Height = 800, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentGridPanel panel = window.GetVisualDescendants().OfType<AgentGridPanel>().First();
    Control cell = Assert.Single(panel.Children);
    Assert.True(cell.Bounds.Width > 0, "the cell must be arranged");
    window.Close();
  }

  private static (AgentGridPanel Panel, Window Window) Show(int cells, double width, double height)
  {
    AgentGridPanel panel = new();
    for (int i = 0; i < cells; i++)
    {
      panel.Children.Add(new Border { Name = "CellFrame" });
    }

    Window window = new() { Width = width, Height = height, Content = panel };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return (panel, window);
  }
}
