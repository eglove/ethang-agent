using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #307 D8: the grid splits the available space EVENLY, every cell holds a
///     comfortable minimum of 480 x 360 logical pixels, there is no cell maximum, and the
///     grid scrolls when the window cannot fit the cells at that minimum.
///     <para>The numbers asserted here are the ones D8 MEASURED from the current chrome
///     (1920 x 1080 with the workbench rail open leaves 1654 x 1024), not estimates: D8's
///     job is to argue the minimum is comfortable, so the plan has to produce the count the
///     decision states rather than a more flattering one.</para></summary>
public class AgentGridLayoutTests
{
  /// <summary>The measured viewport: 1920 wide less 266 of chrome, 1080 less ~56 of
  ///     resource bar and tab strip (D8's table).</summary>
  private const double MeasuredWidth = 1654;

  private const double MeasuredHeight = 1024;

  [Fact]
  public void OneAgent_TakesTheWholeViewport()
  {
    AgentGridPlan plan = AgentGridLayout.For(1, MeasuredWidth, MeasuredHeight);

    Assert.Equal(1, plan.Columns);
    Assert.Equal(1, plan.Rows);
    Assert.Equal(MeasuredWidth, plan.CellWidth, 0.5);
    Assert.Equal(MeasuredHeight, plan.CellHeight, 0.5);
    Assert.False(plan.ScrollsVertically);
    Assert.False(plan.ScrollsHorizontally);
  }

  [Fact]
  public void TwoAgents_SplitIntoTwoColumns()
  {
    AgentGridPlan plan = AgentGridLayout.For(2, MeasuredWidth, MeasuredHeight);

    Assert.Equal(2, plan.Columns);
    Assert.Equal(1, plan.Rows);
    // Even split: each cell is exactly half the viewport.
    Assert.Equal(MeasuredWidth / 2, plan.CellWidth, 0.5);
    Assert.Equal(MeasuredHeight, plan.CellHeight, 0.5);
  }

  [Fact]
  public void ThreeAgents_TileThreeColumns()
  {
    AgentGridPlan plan = AgentGridLayout.For(3, MeasuredWidth, MeasuredHeight);

    Assert.Equal(3, plan.Columns);
    Assert.Equal(1, plan.Rows);
    Assert.Equal(MeasuredWidth / 3, plan.CellWidth, 0.5);
  }

  /// <summary>D8's stated number, pinned: at 1920 x 1080 with the rail open the grid gives
  ///     THREE columns by TWO rows - six cells visible, not eight. A fourth column needs
  ///     roughly 2186 px of window, because the 480 px floor is what costs it, not the rail.
  ///     <para>This is the assertion that keeps D8 honest: if the layout ever flattered itself
  ///     into four columns here, the decision's own arithmetic would be wrong.</para></summary>
  [Fact]
  public void SixAgents_AreThreeColumnsByTwoRows_NotFourColumns()
  {
    AgentGridPlan plan = AgentGridLayout.For(6, MeasuredWidth, MeasuredHeight);

    Assert.Equal(3, plan.Columns);
    Assert.Equal(2, plan.Rows);
    Assert.False(plan.ScrollsVertically, "six cells fit the measured viewport exactly");
  }

  [Fact]
  public void SevenAgents_ScrollVertically_AtTheMinimumCellHeight()
  {
    AgentGridPlan plan = AgentGridLayout.For(7, MeasuredWidth, MeasuredHeight);

    Assert.Equal(3, plan.Columns);
    Assert.Equal(3, plan.Rows);
    Assert.Equal(AgentGridLayout.MinCellHeight, plan.CellHeight, 0.5);
    Assert.True(plan.ScrollsVertically, "a third row cannot fit 1024 px at the 360 px minimum");
  }

  /// <summary>There is no cell MAXIMUM (D8): past what fits, the grid scrolls rather than
  ///     refusing the cell. The number of agents a user starts is the user's decision.</summary>
  [Fact]
  public void ManyAgents_AreNeverRefused_TheyScroll()
  {
    AgentGridPlan plan = AgentGridLayout.For(24, MeasuredWidth, MeasuredHeight);

    Assert.Equal(3, plan.Columns);
    Assert.Equal(8, plan.Rows);
    Assert.True(plan.ScrollsVertically);
    Assert.Equal(AgentGridLayout.MinCellHeight, plan.CellHeight, 0.5);
  }

  /// <summary>A collapsed rail is NOT what costs the fourth column (D8): 1872 / 480 is still
  ///     3.9, so the floor decides. Asserted so a future 'the rail is the problem' theory has
  ///     to argue with the arithmetic.</summary>
  [Fact]
  public void CollapsedRail_StillGivesThreeColumns()
  {
    AgentGridPlan plan = AgentGridLayout.For(4, 1872, MeasuredHeight);

    Assert.Equal(3, plan.Columns);
  }

  /// <summary>Columns are capped at the cell count: two agents in a wide window split into
  ///     two columns, never three with an empty third slot.</summary>
  [Fact]
  public void Columns_NeverExceedTheCellCount()
  {
    AgentGridPlan plan = AgentGridLayout.For(2, 4000, 1000);

    Assert.Equal(2, plan.Columns);
    Assert.Equal(2000, plan.CellWidth, 0.5);
  }

  /// <summary>A viewport narrower than one minimum cell scrolls horizontally rather than
  ///     squeezing a transcript below its readability floor.</summary>
  [Fact]
  public void NarrowViewport_ScrollsHorizontally()
  {
    AgentGridPlan plan = AgentGridLayout.For(2, 400, 1000);

    Assert.Equal(1, plan.Columns);
    Assert.True(plan.ScrollsHorizontally);
    Assert.Equal(AgentGridLayout.MinCellWidth, plan.CellWidth, 0.5);
  }

  /// <summary>A viewport that fits ONE minimum cell does not scroll horizontally: the single
  ///     column simply takes the width it has. Only a viewport below the 480 px floor does.</summary>
  [Fact]
  public void ViewportFittingOneMinimumCell_DoesNotScrollHorizontally()
  {
    AgentGridPlan plan = AgentGridLayout.For(2, 700, 1000);

    Assert.Equal(1, plan.Columns);
    Assert.False(plan.ScrollsHorizontally);
    Assert.Equal(700, plan.CellWidth, 0.5);
  }

  /// <summary>A viewport that has not been measured yet plans one column at the minimum and
  ///     claims no scrolling: there is nothing to overflow until a real layout pass.</summary>
  /// <summary>A NON-FINITE viewport is an unmeasured one (a panel inside a ScrollViewer is
  ///     measured with infinite available size). The plan must stay TOTAL: dividing infinity
  ///     by the minimum cell width overflows the column count, and the panel then measures
  ///     every cell at an infinite width - a layout that never settles. Measured: it hung the
  ///     whole test run.</summary>
  [Theory]
  [InlineData(0, 0)]
  [InlineData(-1, 500)]
  [InlineData(500, 0)]
  [InlineData(double.PositiveInfinity, 500)]
  [InlineData(500, double.PositiveInfinity)]
  [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
  [InlineData(double.NaN, 500)]
  public void UnmeasuredViewport_PlansOneColumn_WithoutClaimingAScroll(double width, double height)
  {
    AgentGridPlan plan = AgentGridLayout.For(3, width, height);

    Assert.Equal(1, plan.Columns);
    Assert.Equal(3, plan.Rows);
    Assert.False(plan.ScrollsVertically);
    Assert.False(plan.ScrollsHorizontally);
  }

  /// <summary>A count below one is one cell: a workspace tab always shows at least the agent
  ///     it was opened for, so an empty cell list can never plan a zero-column grid.</summary>
  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  public void NonPositiveCount_PlansASingleCell(int count)
  {
    AgentGridPlan plan = AgentGridLayout.For(count, MeasuredWidth, MeasuredHeight);

    Assert.Equal(1, plan.Columns);
    Assert.Equal(1, plan.Rows);
  }

  /// <summary>The plan is a pure function of its inputs: the view re-derives it on every
  ///     layout pass, so two calls with the same viewport must never disagree.</summary>
  [Fact]
  public void Planning_IsPure()
  {
    AgentGridPlan first = AgentGridLayout.For(5, MeasuredWidth, MeasuredHeight);
    AgentGridPlan second = AgentGridLayout.For(5, MeasuredWidth, MeasuredHeight);

    Assert.Equal(first, second);
  }
}
