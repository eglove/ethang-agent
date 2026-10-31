using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Makes a rendered code block scroll sideways instead of being clipped.
///     <para>A fenced code line is not prose. Wrapping it reflows the line and destroys the
///     indentation that carries its meaning; clipping it hides whatever did not fit. The
///     renderer picks NoWrap and the scroll viewer that surrounds the whole document has its
///     horizontal scrollbar DISABLED, so the third option - keep the line whole and let the
///     block scroll - is the one nobody took. Measured: a 214-character line wanted 2600 px and
///     was painted into 684 px with no way to reach the rest.</para>
///     <para>WHY A POST-PASS RATHER THAN A RENDERER. MarkView's own CodeBlockRenderer is what
///     builds the block, and the syntax-highlighting extension replaces it with its own copy;
///     replacing either from here would mean reimplementing TextMate tokenisation to change one
///     scrollbar, and a subclass would silently lose highlighting the day the extension is
///     registered. The post-pass only re-parents the block MarkView already built, so
///     highlighting, theming, and selection keep working exactly as they did.</para>
///     <para>THE SCROLL POSITION SURVIVES A REBUILD, and capturing it is why this type has TWO
///     calls rather than one. A streamed delta re-renders the whole document, so the scroller
///     from the previous render is gone; without carrying its offset over, each delta snapped
///     the block back to the left edge - the user dragged, the next delta arrived, and the view
///     jumped home, which reads as a scrollbar that only moves a couple of characters. The
///     offsets must be read BEFORE the markdown is replaced, because the block reuses ONE
///     viewer instance: assigning <c>Markdown</c> rebuilds the very tree the offsets live in.
///     Reading them afterwards returns zeros, silently.</para></summary>
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
      offsets.Add(block.Parent is ScrollViewer scroller ? scroller.Offset.X : 0);
    }

    return offsets;
  }

  /// <summary>Wraps every code block under <paramref name="root"/> in a horizontally
  ///     scrolling container, so a line wider than the transcript can be scrolled to.
  ///     <para><paramref name="offsets"/> comes from <see cref="CaptureOffsets"/> taken before
  ///     the rebuild: each new block inherits the offset of the block at the same position,
  ///     because a rebuild re-renders the same content rather than different content.</para></summary>
  /// <param name="root">The freshly rendered document.</param>
  /// <param name="offsets">Offsets captured from the previous render, or empty on the first.</param>
  public static void Wrap(Control root, IReadOnlyList<double> offsets)
  {
    ArgumentNullException.ThrowIfNull(offsets);
    int index = 0;
    foreach (Border block in Descendants(root).OfType<Border>().ToList())
    {
      if (!block.Classes.Contains(CodeBlockClass) || block.Parent is ScrollViewer)
      {
        continue;
      }

      if (block.Parent is not Panel parent)
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
      // Detach BEFORE building the scroller: setting the scroller's Content adopts the block,
      // and a control cannot be adopted by a new logical parent while it still has one.
      parent.Children.RemoveAt(position);
      ScrollViewer scroller = Scroller(block);
      parent.Children.Insert(position, scroller);
      if (offset > 0)
      {
        // The extent is only known after layout, so the restore is posted rather than set
        // inline - setting Offset on a scroller with no extent clamps it straight back to 0.
        ScrollViewer target = scroller;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => target.Offset = target.Offset.WithX(offset),
            Avalonia.Threading.DispatcherPriority.Loaded);
      }
    }
  }

  /// <summary>A container that scrolls sideways only when the line needs it.
  ///     <para>The horizontal axis is <c>Auto</c> so a short block looks exactly as before and
  ///     only an overlong line grows a scrollbar. The vertical axis is disabled: the transcript
  ///     scrolls the document, and a code block that scrolled vertically inside it would trap
  ///     the wheel.</para></summary>
  private static ScrollViewer Scroller(Border block) => new()
  {
    Content = block,
    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
  };

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
