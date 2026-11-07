using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>A code block's horizontal scroll position must SURVIVE a re-render.
///     <para>Every streamed delta rebuilds the whole markdown document, so the scroll viewer
///     from the previous render is discarded and a new one is built. Without carrying the offset
///     across, each delta snapped the block back to its left edge - which reads to a user as a
///     scrollbar that only moves a couple of characters: they drag, the next token arrives, and
///     the view jumps home. Measured before the fix: a scroller set to offset 300 came back as a
///     NEW instance at offset 0.</para>
///     <para>Capturing is the subtle half. The block reuses ONE viewer instance, so assigning
///     <c>Markdown</c> rebuilds the very tree the offsets live in - reading them afterwards
///     returns zeros, silently. The offsets are therefore captured BEFORE the rebuild.</para></summary>
[Trait("Category", "Process")]
public class CodeBlockScrollPositionTests
{
  private const string LongLine =
      "var p = System.Diagnostics.Process.GetCurrentProcess(); " +
      "Output($\"app start (UTC): {p.StartTime.ToUniversalTime():O}\"); " +
      "var dll = Path.Combine(AppContext.BaseDirectory, \"eThangAgent.State.Domain.dll\");";

  /// <summary>The defect stated directly: scroll right, re-render, and the position is kept.</summary>
  [AvaloniaFact]
  public void Scroll_Position_Survives_A_Streamed_Re_Render()
  {
    Window window = Show("```csharp\n" + LongLine + "\n```");

    ScrollViewer first = CodeScroller(window);
    first.Offset = first.Offset.WithX(300);
    Dispatcher.UIThread.RunJobs();
    Assert.True(first.Offset.X > 100, $"precondition: the block must have scrolled, offset={first.Offset.X:F0}");

    // A streamed delta re-renders the document, which replaces the scroller.
    AgentMarkdownBlock block = window.Content as AgentMarkdownBlock ??
        throw new InvalidOperationException("the window must host the markdown block");
    block.MarkdownText += " ";
    block.FlushPendingRender();
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    ScrollViewer second = CodeScroller(window);
    Assert.NotSame(first, second);
    Assert.True(second.Offset.X > 100,
        $"the scroll position must survive the rebuild, but it reset to {second.Offset.X:F0}");
    window.Close();
  }

  /// <summary>A block the user never scrolled stays at the left edge: the restore must not
  ///     invent a position, or every rebuild would shift the view for no reason.</summary>
  [AvaloniaFact]
  public void An_Unscrolled_Block_Stays_At_The_Left_Edge_Across_A_Rebuild()
  {
    Window window = Show("```csharp\n" + LongLine + "\n```");

    AgentMarkdownBlock block = (AgentMarkdownBlock)window.Content!;
    block.MarkdownText += " ";
    block.FlushPendingRender();
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(0, CodeScroller(window).Offset.X, 1.0);
    window.Close();
  }

  private static Window Show(string markdown)
  {
    Window window = new() { Width = 700, Height = 400 };
    AgentMarkdownBlock block = new() { MarkdownText = markdown, IsOpen = true };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return window;
  }

  private static ScrollViewer CodeScroller(Window window)
  {
    Border block = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains("markdown-code-block"));
    return (ScrollViewer)block.Parent!;
  }
}
