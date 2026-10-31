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
///     <para>It runs after every render because a rebuild makes fresh visuals: the code block
///     produced last time is gone.</para></summary>
internal static class CodeBlockScroller
{
  /// <summary>Wraps every code block under <paramref name="root"/> in a horizontally
  ///     scrolling container, so a line wider than the transcript can be scrolled to.</summary>
  /// <param name="root">The freshly rendered document.</param>
  public static void Wrap(Control root)
  {
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

      int index = parent.Children.IndexOf(block);
      if (index < 0)
      {
        continue;
      }

      // Detach before re-parenting: a control cannot be adopted by a new logical parent
      // while it still has one, and setting Content on the scroller is exactly that adoption.
      parent.Children.RemoveAt(index);
      parent.Children.Insert(index, Scroller(block));
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
