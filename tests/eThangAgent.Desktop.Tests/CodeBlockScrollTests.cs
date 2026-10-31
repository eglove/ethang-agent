using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>A fenced code block must SCROLL horizontally rather than be clipped.
///     <para>A code line is not prose: wrapping it destroys the indentation that carries its
///     meaning, and clipping it hides the part of the line that matters. The renderer laid the
///     block out with <c>TextWrapping.NoWrap</c> inside a scroll viewer whose horizontal
///     scrollbar is disabled, so a line wider than the transcript was simply cut off - a
///     measured 2600 px line painted into 684 px with no way to reach the rest.</para>
///     <para>The rule these tests pin: the line keeps its full width and the BLOCK scrolls.</para></summary>
public class CodeBlockScrollTests
{
  private const string LongLine =
      "var p = System.Diagnostics.Process.GetCurrentProcess(); " +
      "Output($\"app start (UTC): {p.StartTime.ToUniversalTime():O}\"); " +
      "var dll = Path.Combine(AppContext.BaseDirectory, \"eThangAgent.State.Domain.dll\");";

  /// <summary>The defect stated as geometry: the text wants more width than it is given, and
  ///     nothing on the path offers a horizontal scrollbar to reach the remainder.</summary>
  [AvaloniaFact]
  public void Long_Code_Line_Is_Reachable_Rather_Than_Clipped()
  {
    Window window = Show("```csharp\n" + LongLine + "\n```", 700);

    (TextBlock line, ScrollViewer scroller) = CodeLineAndScroller(window);
    Assert.Equal(TextWrapping.NoWrap, line.TextWrapping);
    Assert.True(line.Bounds.Width > scroller.Viewport.Width + 1,
        $"the line must keep its full width ({line.Bounds.Width:F0}) rather than be squeezed into the viewport ({scroller.Viewport.Width:F0})");
    Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
    Assert.True(scroller.Extent.Width > scroller.Viewport.Width + 1,
        $"the block must report overflow to scroll ({scroller.Extent.Width:F0} > {scroller.Viewport.Width:F0})");
    window.Close();
  }

  /// <summary>A short line must NOT produce a scrollbar: the block sizes to the transcript
  ///     until a line actually needs more room, so an ordinary code block looks unchanged.</summary>
  [AvaloniaFact]
  public void Short_Code_Block_Does_Not_Scroll()
  {
    Window window = Show("```csharp\nvar x = 1;\n```", 700);

    (TextBlock _, ScrollViewer scroller) = CodeLineAndScroller(window);
    Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1,
        $"a short block must not overflow ({scroller.Extent.Width:F0} vs {scroller.Viewport.Width:F0})");
    window.Close();
  }

  /// <summary>Code keeps its indentation: wrapping it would reflow the line and destroy the
  ///     structure that makes it readable, so the wrapping mode is part of the contract.</summary>
  [AvaloniaFact]
  public void Code_Line_Keeps_NoWrap_So_Indentation_Survives()
  {
    Window window = Show("```csharp\n    indented();\n```", 700);

    (TextBlock line, ScrollViewer _) = CodeLineAndScroller(window);
    Assert.Equal(TextWrapping.NoWrap, line.TextWrapping);
    window.Close();
  }

  private static Window Show(string markdown, double width)
  {
    Window window = new() { Width = width, Height = 400 };
    AgentMarkdownBlock block = new() { MarkdownText = markdown, IsOpen = false };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return window;
  }

  /// <summary>The code line's TextBlock and the scroll viewer that must carry the overflow.
  ///     The scroller is the NEAREST ancestor offering horizontal scrolling, which is what
  ///     makes the assertion independent of how many wrappers the renderer adds.</summary>
  private static (TextBlock Line, ScrollViewer Scroller) CodeLineAndScroller(Window window)
  {
    Border block = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains("markdown-code-block"));
    TextBlock line = block.GetVisualDescendants().OfType<TextBlock>().First();
    ScrollViewer scroller = block.GetVisualAncestors().OfType<ScrollViewer>()
        .First(s => s.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
    return (line, scroller);
  }
}
