using Avalonia;
using Avalonia.Controls;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>Lays out a workspace tab's agent cells in an even grid (issue #307 D8).
///     <para>The geometry comes from <see cref="AgentGridLayout"/> - a pure function - so the
///     arithmetic is testable without a window and this panel only applies it.</para>
///     <para>IT DIVIDES SPACE DURING ARRANGE AND MUTATES NOTHING. An earlier version set each
///     cell's Width and Height from a size-changed handler instead, which fed the panel's own
///     desired size back into the next layout pass: the cells oscillated, and a real pointer
///     click on the rail's collapse control never completed because the button moved between
///     press and release (measured - the click landed on the button and still did not fire).
///     Arrange is a pure function of the final size, so it cannot loop.</para>
///     <para>MEASURE AND ARRANGE MUST AGREE ABOUT THE CELL SIZE, and getting that wrong is
///     what made the transcript render badly. This panel sits inside the tab's scroll viewer,
///     whose auto scrollbars hand it an INFINITE available size; answering that with the
///     480 x 360 minimum and then arranging the same cells at the real viewport made every
///     cell compute its internal layout for one width and draw it at another. A markdown
///     paragraph's line breaks were measured at ~450 px and painted at ~810, so the wrapped
///     height was roughly double what the text needed - the large gaps between paragraphs -
///     and a code block was measured narrow while its card was wide. The available size is
///     therefore replaced with the panel's OWN arranged size when the parent offers no bound,
///     which is the size the cells are about to be arranged at.</para>
///     <para>Past what fits at the minimum cell size the panel reports a desired size larger
///     than the viewport, which is what makes the surrounding scroll viewer scroll rather
///     than shrink a transcript below its readability floor.</para></summary>
internal sealed class AgentGridPanel : Panel
{
  // The viewport the cells are measured against comes from the panel's own arranged size,
  // so a new arranged size is a new measure input. Without this the first pass would measure
  // at the minimum and never be told the real viewport had arrived.
  public AgentGridPanel() =>
      BoundsProperty.Changed.AddClassHandler<AgentGridPanel>((panel, _) => panel.InvalidateMeasure());

  /// <summary>The plan the last arrange used. Exposed for tests: the layout contract is a
  ///     claim about measured geometry, so a test must be able to read what was applied.</summary>
  internal AgentGridPlan LastPlan { get; private set; }

  protected override Size MeasureOverride(Size availableSize)
  {
    Size viewport = ViewportFor(availableSize);
    AgentGridPlan plan = AgentGridLayout.For(Children.Count, viewport.Width, viewport.Height);
    LastPlan = plan;
    Size cell = new(plan.CellWidth, plan.CellHeight);
    foreach (Control child in Children)
    {
      child.Measure(cell);
    }

    return new Size(plan.Columns * plan.CellWidth, plan.Rows * plan.CellHeight);
  }

  protected override Size ArrangeOverride(Size finalSize)
  {
    AgentGridPlan plan = AgentGridLayout.For(Children.Count, finalSize.Width, finalSize.Height);
    LastPlan = plan;
    for (int index = 0; index < Children.Count; index++)
    {
      int column = index % plan.Columns;
      int row = index / plan.Columns;
      Children[index].Arrange(new Rect(
          column * plan.CellWidth, row * plan.CellHeight, plan.CellWidth, plan.CellHeight));
    }

    return new Size(plan.Columns * plan.CellWidth, plan.Rows * plan.CellHeight);
  }

  /// <summary>The size the cells will actually be arranged at.
  ///     <para>A scroll viewer measures its content unbounded on an axis it can scroll, so an
  ///     infinite or absent bound is not an unknown viewport - it is the parent declining to
  ///     constrain this axis, and the panel's own arranged extent is what it will be given.
  ///     Falling back to the arranged size keeps measure and arrange describing the same
  ///     rectangle; falling back to the MINIMUM CELL instead (what this used to do) made them
  ///     disagree by however much the viewport exceeded 480 x 360, and every cell laid its
  ///     content out for the smaller one.</para>
  ///     <para>A panel that has never been arranged has no extent yet, so the minimum stands
  ///     until the first arrange pass supplies one - one extra layout pass, not a wrong one.</para></summary>
  private Size ViewportFor(Size availableSize)
  {
    double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
        ? availableSize.Width
        : Bounds.Width;
    double height = double.IsFinite(availableSize.Height) && availableSize.Height > 0
        ? availableSize.Height
        : Bounds.Height;
    return new Size(width, height);
  }
}
