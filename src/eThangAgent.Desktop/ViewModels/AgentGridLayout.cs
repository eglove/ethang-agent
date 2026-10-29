namespace eThangAgent.Desktop.ViewModels;

/// <summary>How a workspace tab's agent cells divide the available space (issue #307 D8).
///     <para>A PURE value: the same count and viewport always produce the same plan, so the
///     layout can be asserted without a window and the view never re-derives it.</para></summary>
/// <param name="Columns">How many cells sit side by side.</param>
/// <param name="Rows">How many rows the cells occupy at that column count.</param>
/// <param name="CellWidth">Every cell's width in logical pixels.</param>
/// <param name="CellHeight">Every cell's height in logical pixels.</param>
/// <param name="ScrollsVertically">True when the rows cannot fit the viewport at the
///     minimum cell height, so the grid scrolls instead of shrinking below it.</param>
/// <param name="ScrollsHorizontally">True when the viewport is narrower than one minimum
///     cell, so even a single column cannot fit.</param>
internal readonly record struct AgentGridPlan(
    int Columns,
    int Rows,
    double CellWidth,
    double CellHeight,
    bool ScrollsVertically,
    bool ScrollsHorizontally);

/// <summary>The agent grid's geometry (issue #307 D8): cells split the available space
///     EVENLY, and every cell holds a comfortable MINIMUM of 480 x 360 logical pixels.
///     <para>The minimum is a readability floor rather than a policy: below roughly 480 px a
///     transcript line wraps often enough that scanning it costs more than switching cells.
///     There is no cell MAXIMUM - the number of agents a user starts is the user's decision,
///     so everything past what fits is one scroll away.</para>
///     <para>Measured against the current chrome (D8): a 1920 x 1080 window with the workbench
///     rail open leaves 1654 x 1024 for the grid, which is three columns by two rows - six
///     cells visible, not eight. That number is what this function produces, not an estimate.</para></summary>
internal static class AgentGridLayout
{
  /// <summary>The comfortable minimum cell width (D8).</summary>
  public const double MinCellWidth = 480;

  /// <summary>The comfortable minimum cell height (D8).</summary>
  public const double MinCellHeight = 360;

  /// <summary>Floating-point slack for the scroll decisions: a viewport that fits the rows
  ///     to within a sub-pixel must not report a scrollbar.</summary>
  private const double Epsilon = 0.5;

  /// <summary>Plans <paramref name="count"/> cells into a <paramref name="width"/> by
  ///     <paramref name="height"/> viewport. A count below one is one cell: a workspace tab
  ///     always shows at least the agent it was opened for.
  ///     <para>A viewport that has not been measured yet (zero or negative) plans a single
  ///     column at the minimum cell size and claims no scrolling: there is nothing to
  ///     overflow until a real layout pass happens.</para></summary>
  public static AgentGridPlan For(int count, double width, double height)
  {
    int cells = Math.Max(1, count);
    // A NON-FINITE viewport is an unmeasured one, not an enormous one. A panel inside a
    // ScrollViewer is measured with infinite available size, and dividing infinity by the
    // minimum cell width overflows the column count - the plan must stay total, so an
    // infinite axis is treated exactly like a zero one: one column at the minimum, and the
    // real geometry comes from the finite size the arrange pass supplies.
    if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
    {
      return new AgentGridPlan(1, cells, MinCellWidth, MinCellHeight, false, false);
    }

    // Columns are capped at the cell count: two agents in a wide window split into two
    // columns, never three with an empty slot.
    int columns = Math.Max(1, Math.Min(cells, (int)Math.Floor(width / MinCellWidth)));
    int rows = (int)Math.Ceiling(cells / (double)columns);
    // The EVEN split, before any floor is applied. The floors below raise a cell that the
    // viewport cannot fill; the scroll decisions compare against these natural sizes, because
    // a clamped cell can never be smaller than the minimum it was clamped to.
    double naturalWidth = width / columns;
    double naturalHeight = height / rows;
    // Cells never shrink below the minimum: the grid scrolls instead, which is what keeps a
    // transcript legible however many agents the user starts.
    double cellWidth = Math.Max(MinCellWidth, naturalWidth);
    double cellHeight = Math.Max(MinCellHeight, naturalHeight);
    return new AgentGridPlan(
        columns,
        rows,
        cellWidth,
        cellHeight,
        ScrollsVertically: naturalHeight < MinCellHeight - Epsilon,
        ScrollsHorizontally: naturalWidth < MinCellWidth - Epsilon);
  }
}
