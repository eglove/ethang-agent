using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The grid's cells must be MEASURED at the size they are ARRANGED at.
///     <para>The panel lives inside the tab's scroll viewer, whose auto scrollbars hand it an
///     INFINITE available size during measure. The panel used to answer that with its
///     480 x 360 minimum cell and then arrange the same cells at the real viewport - so every
///     cell computed its internal layout (markdown wrap heights, code-block widths) for one
///     width and was drawn at another. On screen that read as huge gaps between paragraphs, a
///     transcript that kept growing while the user typed, and exec code blocks squeezed into a
///     narrow box inside a wide card.</para>
///     <para>The invariant is asserted through a recording child, because a child's own
///     DesiredSize reports what IT wants, not the width it was measured at: the defect is the
///     size the panel HANDS DOWN, so that is what a test has to observe.</para></summary>
[Trait("Category", "Process")]
public class AgentGridMeasureArrangeTests
{
  [AvaloniaFact]
  public void OneCell_IsMeasuredAtTheSizeItIsArrangedAt()
  {
    (AgentGridPanel panel, Window window) = ShowInScrollViewer(1, 840, 800);

    RecordingCell cell = Assert.IsType<RecordingCell>(panel.Children[0]);
    Assert.Equal(cell.Arranged.Width, cell.Measured.Width, 1.0);
    Assert.Equal(cell.Arranged.Height, cell.Measured.Height, 1.0);
    Assert.Equal(panel.Bounds.Width, cell.Measured.Width, 1.0);
    window.Close();
  }

  /// <summary>The same invariant with two cells, so a fix cannot hold for the one-column
  ///     case alone: the split must be measured, not only arranged.</summary>
  [AvaloniaFact]
  public void TwoCells_AreMeasuredAtTheWidthsTheyAreArrangedAt()
  {
    (AgentGridPanel panel, Window window) = ShowInScrollViewer(2, 1200, 800);

    foreach (Control child in panel.Children)
    {
      RecordingCell cell = Assert.IsType<RecordingCell>(child);
      Assert.Equal(cell.Arranged.Width, cell.Measured.Width, 1.0);
      Assert.Equal(cell.Arranged.Height, cell.Measured.Height, 1.0);
    }

    window.Close();
  }

  /// <summary>The fix must not oscillate: an earlier version of this panel fed its own
  ///     desired size back into the next layout pass and the cells moved forever. Repeated
  ///     passes must converge on identical geometry.</summary>
  [AvaloniaFact]
  public void RepeatedLayout_Converges()
  {
    (AgentGridPanel panel, Window window) = ShowInScrollViewer(3, 1500, 800);
    RecordingCell third = Assert.IsType<RecordingCell>(panel.Children[2]);
    Rect first = third.Arranged;

    for (int i = 0; i < 5; i++)
    {
      panel.InvalidateMeasure();
      panel.InvalidateArrange();
      Dispatcher.UIThread.RunJobs();
    }

    Assert.Equal(first, third.Arranged);
    Assert.Equal(first.Width, third.Measured.Width, 1.0);
    window.Close();
  }

  /// <summary>Hosts the panel exactly as the tab does: inside a scroll viewer whose auto
  ///     scrollbars hand the panel an infinite measure size.</summary>
  private static (AgentGridPanel Panel, Window Window) ShowInScrollViewer(int cells, double width, double height)
  {
    AgentGridPanel panel = new();
    for (int i = 0; i < cells; i++)
    {
      panel.Children.Add(new RecordingCell { Name = "CellFrame" });
    }

    ScrollViewer scroll = new()
    {
      HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
      VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
      Content = panel,
    };
    Window window = new() { Width = width, Height = height, Content = scroll };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
    return (panel, window);
  }

  /// <summary>A cell stand-in that records the size the panel measures it with and the size
  ///     the panel arranges it at. Those two numbers are the whole contract: they must agree,
  ///     because a cell lays out its own content during measure.</summary>
  private sealed class RecordingCell : Border
  {
    public Size Measured { get; private set; }

    public Rect Arranged { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
      Measured = availableSize;
      return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
      Arranged = new Rect(finalSize);
      return base.ArrangeOverride(finalSize);
    }
  }
}
