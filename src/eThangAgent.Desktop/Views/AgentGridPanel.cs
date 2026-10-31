using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
///     <para>MEASURE AND ARRANGE MUST AGREE ABOUT THE CELL SIZE. This panel sits inside the
///     tab's scroll viewer, whose auto scrollbars hand it an INFINITE available size; answering
///     that with the 480 x 360 minimum and then arranging the same cells at the real viewport
///     made every cell compute its internal layout for one width and draw it at another - a
///     markdown paragraph's line breaks were measured at ~450 px and painted at ~810, so the
///     wrapped height was roughly double what the text needed.</para>
///     <para>THE VIEWPORT COMES FROM THE SCROLL VIEWER, NEVER FROM THIS PANEL'S OWN BOUNDS.
///     An earlier fix fell back to <c>Bounds</c>, which is circular: the panel's arranged size is
///     BOTH the measure input and an output of arrange, so opening the workbench narrowed the
///     panel's slot while it kept measuring against its previous width, reported the stale
///     width, and left the scroll viewer with a permanent horizontal overflow - the chat ran
///     under the rail. A layout input the layout itself produces can never converge.</para>
///     <para>THE VIEWPORT ARRIVES ONE PASS LATE, which is why this panel watches it. A scroll
///     viewer only knows its viewport once it has arranged, so the first measure pass sees zero;
///     the panel therefore re-measures when the viewport changes. That dependency points
///     UPWARD - the viewport is decided above this panel and never by it - so unlike the bounds
///     fallback it converges instead of chasing its own output.</para>
///     <para>Past what fits at the minimum cell size the panel reports a desired size larger
///     than the viewport, which is what makes the surrounding scroll viewer scroll rather
///     than shrink a transcript below its readability floor.</para></summary>
internal sealed class AgentGridPanel : Panel
{
  /// <summary>The scroll viewer this panel measures against, while attached to one.</summary>
  private ScrollViewer? _scroller;

  /// <summary>The viewport subscription, released with the tree so a recycled tab does not
  ///     keep invalidating a panel nothing renders.</summary>
  private IDisposable? _viewportSubscription;

  /// <summary>The plan the last arrange used. Exposed for tests: the layout contract is a
  ///     claim about measured geometry, so a test must be able to read what was applied.</summary>
  internal AgentGridPlan LastPlan { get; private set; }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
  {
    base.OnAttachedToVisualTree(e);
    _scroller = this.FindAncestorOfType<ScrollViewer>();
    _viewportSubscription = _scroller?.GetObservable(ScrollViewer.ViewportProperty)
        .Subscribe(new AnonymousObserver<Size>(_ => InvalidateMeasure()));
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
  {
    _viewportSubscription?.Dispose();
    _viewportSubscription = null;
    _scroller = null;
    base.OnDetachedFromVisualTree(e);
  }

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
  ///     constrain this axis. The answer is the scroll viewer's VIEWPORT, because that is the
  ///     space the cells are about to be given.</para>
  ///     <para>Never this panel's own Bounds: that value is produced by arrange, so feeding it
  ///     back as a measure input makes the two passes depend on each other and the panel keeps
  ///     reporting its pre-shrink width (measured: opening the workbench left the panel at
  ///     1883 while its slot was 1523, overflowing forever).</para></summary>
  private Size ViewportFor(Size availableSize)
  {
    Size viewport = _scroller?.Viewport ?? default;
    double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
        ? availableSize.Width
        : viewport.Width;
    double height = double.IsFinite(availableSize.Height) && availableSize.Height > 0
        ? availableSize.Height
        : viewport.Height;
    return new Size(width, height);
  }

  /// <summary>Minimal observer so the viewport subscription needs no reactive package.</summary>
  private sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
  {
    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(T value) => onNext(value);
  }
}
