using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Makes a rendered code block scroll sideways instead of being clipped.
///     <para>A fenced code line is not prose. Wrapping it reflows the line and destroys the
///     indentation that carries its meaning; clipping it hides whatever did not fit. The
///     renderer picks NoWrap and the scroll viewer that surrounds the whole document has its
///     horizontal scrollbar DISABLED, so the third option - keep the line whole and let the
///     block scroll - is the one nobody took.</para>
///     <para>THE SCROLLBAR IS BUILT HERE, OUTSIDE THE SCROLLING AREA, RATHER THAN LEFT TO THE
///     SCROLL VIEWER. A scroll viewer does not reserve its own scrollbar's space: measured, a
///     15 px block with a 16 px bar produced a desired height of 16 px, so the bar was drawn
///     straight OVER the code. Grabbing that overlay strip started a drag that changed the
///     clipping, changed the extent, and moved the thumb out from under the pointer - a
///     scrollbar that advanced a little and stopped, every time. Neither Auto nor Visible
///     changes it, and sizing the scroller from its content feeds back into itself (the block
///     grew 34 -> 35 as the scroller grew 42 -> 51).</para>
///     <para>So the layout is explicit: a Grid with the code in row 0 and a scrollbar in row 1,
///     the scroller's own scrollbars suppressed and its content offset driven by the bar. The
///     rows cannot overlap because the row definitions decide the geometry.</para>
///     <para>WHY A POST-PASS RATHER THAN A RENDERER. MarkView's own CodeBlockRenderer builds the
///     block, and the syntax-highlighting extension replaces it with its own copy; replacing
///     either from here would mean reimplementing TextMate tokenisation to change one scrollbar,
///     and a subclass would silently lose highlighting the day the extension is registered. The
///     post-pass only re-parents the block MarkView already built, so highlighting, theming, and
///     selection keep working exactly as they did.</para>
///     <para>THE SCROLL POSITION SURVIVES A REBUILD, and capturing it is why this type has TWO
///     entry points. A streamed delta re-renders the whole document, so the scroller from the
///     previous render is gone; without carrying its offset over, each delta snapped the block
///     back to the left edge. The offsets must be read BEFORE the markdown is replaced, because
///     the block reuses ONE viewer instance: assigning <c>Markdown</c> rebuilds the very tree the
///     offsets live in, and reading them afterwards returns zeros, silently.</para></summary>
internal static class CodeBlockScroller
{
  /// <summary>The horizontal scroll offsets of the code blocks in the CURRENT document, in
  ///     document order. Call this before replacing the markdown, then pass the result to
  ///     <see cref="Wrap"/>.</summary>
  /// <param name="viewer">The document that is about to be re-rendered.</param>
  public static IReadOnlyList<double> CaptureOffsets(Control? viewer)
  {
    List<double> offsets = [];
    if (viewer is null)
    {
      return offsets;
    }

    foreach (Border block in Descendants(viewer).OfType<Border>().Where(b => b.Classes.Contains(CodeBlockClass)))
    {
      offsets.Add(OffsetOf(block));
    }

    return offsets;
  }

  /// <summary>Wraps every code block under <paramref name="root"/> in a horizontally
  ///     scrolling container, so a line wider than the transcript can be scrolled to.</summary>
  /// <param name="root">The freshly rendered document.</param>
  /// <param name="offsets">Offsets captured from the previous render, or empty on the first.</param>
  public static void Wrap(Control root, IReadOnlyList<double> offsets)
  {
    ArgumentNullException.ThrowIfNull(offsets);
    int index = 0;
    foreach (Border block in Descendants(root).OfType<Border>().ToList())
    {
      if (!block.Classes.Contains(CodeBlockClass) || block.Parent is not Panel parent)
      {
        continue;
      }

      int position = parent.Children.IndexOf(block);
      if (position < 0)
      {
        continue;
      }

      double offset = index < offsets.Count ? offsets[index] : 0;
      index++;
      // Detach BEFORE building the container: setting Content adopts the block, and a control
      // cannot be adopted by a new logical parent while it still has one.
      parent.Children.RemoveAt(position);
      parent.Children.Insert(position, Frame(block, offset));
    }
  }

  /// <summary>A code block above its own scrollbar, in two rows that cannot overlap.</summary>
  /// <param name="block">The rendered code block.</param>
  /// <param name="offset">The horizontal position to restore.</param>
  private static Grid Frame(Border block, double offset)
  {
    ScrollViewer scroller = new()
    {
      Content = block,
      HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
      VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    ScrollBar bar = new()
    {
      Orientation = Orientation.Horizontal,
      Height = ScrollBarHeight,
    };

    // The bar drives the scroller and the scroller drives the bar; the guard stops the echo.
    bool syncing = false;
    bar.PropertyChanged += (_, e) =>
    {
      if (e.Property != RangeBase.ValueProperty || syncing)
      {
        return;
      }

      syncing = true;
      try
      {
        scroller.Offset = scroller.Offset.WithX(bar.Value);
      }
      finally
      {
        syncing = false;
      }
    };

    scroller.ScrollChanged += (_, _) =>
    {
      if (syncing)
      {
        return;
      }

      syncing = true;
      try
      {
        bar.Minimum = 0;
        bar.Maximum = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        bar.ViewportSize = scroller.Viewport.Width;
        bar.Value = scroller.Offset.X;
      }
      finally
      {
        syncing = false;
      }
    };

    Grid frame = new() { RowDefinitions = new RowDefinitions("Auto,Auto") };
    Grid.SetRow(scroller, 0);
    Grid.SetRow(bar, 1);
    frame.Children.Add(scroller);
    frame.Children.Add(bar);

    if (offset > 0)
    {
      // The extent is only known after layout, so the restore is posted rather than set inline.
      ScrollViewer target = scroller;
      Avalonia.Threading.Dispatcher.UIThread.Post(
          () => target.Offset = target.Offset.WithX(offset),
          Avalonia.Threading.DispatcherPriority.Loaded);
    }

    return frame;
  }

  /// <summary>The horizontal offset of an already-framed block, or 0 when it is not framed.
  ///     <para>Read from the scrollbar rather than the scroller, because the scrollbar is what
  ///     survives as the visible position and is what the user is dragging.</para></summary>
  /// <param name="block">A rendered code block.</param>
  private static double OffsetOf(Border block)
  {
    if (block.Parent is not ScrollViewer scroller)
    {
      return 0;
    }

    if (scroller.Parent is Grid frame)
    {
      ScrollBar? bar = frame.Children.OfType<ScrollBar>().FirstOrDefault();
      if (bar is not null)
      {
        return bar.Value;
      }
    }

    return scroller.Offset.X;
  }

  /// <summary>Room the horizontal scrollbar takes.</summary>
  private const double ScrollBarHeight = 16;

  /// <summary>The class MarkView puts on a fenced code block's container. Named here rather
  ///     than repeated at the call site so the one place that has to agree with MarkView is
  ///     this constant.</summary>
  private const string CodeBlockClass = "markdown-code-block";

  private static IEnumerable<Control> Descendants(Control root)
  {
    foreach (ILogical child in root.GetLogicalChildren())
    {
      if (child is not Control control)
      {
        continue;
      }

      yield return control;
      foreach (Control nested in Descendants(control))
      {
        yield return nested;
      }
    }
  }
}
