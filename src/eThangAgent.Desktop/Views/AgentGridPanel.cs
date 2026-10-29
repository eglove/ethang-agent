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
///     <para>Past what fits at the minimum cell size the panel reports a desired size larger
///     than the viewport, which is what makes the surrounding scroll viewer scroll rather
///     than shrink a transcript below its readability floor.</para></summary>
internal sealed class AgentGridPanel : Panel
{
  /// <summary>The plan the last arrange used. Exposed for tests: the layout contract is a
  ///     claim about measured geometry, so a test must be able to read what was applied.</summary>
  internal AgentGridPlan LastPlan { get; private set; }

  protected override Size MeasureOverride(Size availableSize)
  {
    // The plan needs a viewport to divide; an unmeasured pass plans one column at the
    // minimum, which is the honest answer before a real layout exists.
    AgentGridPlan plan = AgentGridLayout.For(Children.Count, availableSize.Width, availableSize.Height);
    LastPlan = plan;
    foreach (Control child in Children)
    {
      child.Measure(new Size(plan.CellWidth, plan.CellHeight));
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
}
