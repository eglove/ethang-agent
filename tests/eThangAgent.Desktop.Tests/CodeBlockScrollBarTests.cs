using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>A code block's scrollbar must sit BELOW the code and drive it.
///     <para>The scroll viewer draws its own scrollbar INSIDE its bounds and never reserves the
///     space: measured in a StackPanel, a 15 px block with a 16 px bar produced a scroller whose
///     desired height was 16 px, so the bar covered the code. Grabbing that overlay strip began
///     a drag that changed the clipping, changed the extent, and moved the thumb out from under
///     the pointer - a scrollbar that advanced a little and stopped, every time. Neither
///     <c>Auto</c> nor <c>Visible</c> changed it, and sizing the scroller from its content fed
///     back into itself (the block grew 34 -> 35 as the scroller grew 42 -> 51).</para>
///     <para>So the frame is explicit: a Grid whose row 0 is the scroller and row 1 is the
///     scrollbar, with the two wired to each other. The rows cannot overlap because the row
///     definitions decide the geometry, and the wiring is asserted directly - the headless
///     pointer path cannot be trusted here, because a plain ScrollViewer fails it too.</para></summary>
public class CodeBlockScrollBarTests
{
  private const string LongLine =
      "var p = System.Diagnostics.Process.GetCurrentProcess(); " +
      "Output($\"app start (UTC): {p.StartTime.ToUniversalTime():O}\"); " +
      "var dll = Path.Combine(AppContext.BaseDirectory, \"eThangAgent.State.Domain.dll\");";

  /// <summary>The scrollbar must not overlap the code. This is the defect stated as geometry.</summary>
  [AvaloniaFact]
  public void The_Scrollbar_Sits_Below_The_Code_Not_Over_It()
  {
    (ScrollViewer scroller, ScrollBar bar, Border block) = Frame(Show("```csharp\n" + LongLine + "\n```"));

    // Both live in the frame's own coordinate space, so their rows are directly comparable.
    // Compare the two in a space they share, whatever the container looks like: the bar must not
    // cover the code's last line. This is the defect stated as geometry and it holds for either
    // layout, so it fails on the behaviour rather than on the shape of the tree.
    // Shared space: the WINDOW. Translating into the bar itself would report 0 for both and the
    // assertion would hold vacuously - which is exactly how a green test proves nothing.
    Visual root = TopLevel.GetTopLevel(bar)!;
    double blockBottom = block.TranslatePoint(new Point(0, block.Bounds.Height), root)!.Value.Y;
    double barTop = bar.TranslatePoint(new Point(0, 0), root)!.Value.Y;
    Assert.True(barTop >= blockBottom - 0.5,
        $"the bar must start at or below the code, but bar top={barTop:F0} < block bottom={blockBottom:F0} - it is drawn over the text");
    Assert.True(bar.Bounds.Height > 0, "the scrollbar must have real height");
    Assert.True(scroller.Bounds.Height > 0, "the scroller must have real height");
  }

  /// <summary>Dragging the bar must MOVE the code. Asserted through the wiring the pointer
  ///     drives, because that is the contract: bar value in, scroll offset out.</summary>
  [AvaloniaFact]
  public void Moving_The_Scrollbar_Moves_The_Code()
  {
    (ScrollViewer scroller, ScrollBar bar, Border _) = Frame(Show("```csharp\n" + LongLine + "\n```"));
    Assert.True(bar.Maximum > 0, $"a long line must produce scrollable range, max={bar.Maximum:F0}");

    bar.Value = 200;
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(200, scroller.Offset.X, 1.0);

    bar.Value = 400;
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(400, scroller.Offset.X, 1.0);
  }

  /// <summary>And the reverse: scrolling the code (wheel, keyboard, a restore) must move the
  ///     bar, or the thumb sits at the left edge while the content is scrolled.</summary>
  [AvaloniaFact]
  public void Scrolling_The_Code_Moves_The_Scrollbar()
  {
    (ScrollViewer scroller, ScrollBar bar, Border _) = Frame(Show("```csharp\n" + LongLine + "\n```"));

    scroller.Offset = scroller.Offset.WithX(300);
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(300, bar.Value, 1.0);
  }

  /// <summary>A short line has nothing to scroll, and the bar says so rather than offering a
  ///     range that does not exist.</summary>
  [AvaloniaFact]
  public void A_Short_Line_Has_No_Scrollable_Range()
  {
    (ScrollViewer _, ScrollBar bar, Border _) = Frame(Show("```csharp\nvar x = 1;\n```"));
    Assert.Equal(0, bar.Maximum, 1.0);
  }

  private static Window Show(string markdown)
  {
    Window window = new()
    {
      Width = 700,
      Height = 400,
      Content = new AgentMarkdownBlock { MarkdownText = markdown, IsOpen = false },
    };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
    return window;
  }

  private static (ScrollViewer Scroller, ScrollBar Bar, Border Block) Frame(Window window)
  {
    Border block = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains("markdown-code-block"));
    ScrollViewer scroller = Assert.IsType<ScrollViewer>(block.Parent);

    // The bar may be the frame's own sibling (the fixed layout) or the scroller's internal one
    // (the old layout). Finding EITHER lets the behavioural assertions below be the thing that
    // fails, rather than a structural type check that fails for an uninteresting reason.
    ScrollBar? bar = (scroller.Parent as Grid)?.Children.OfType<ScrollBar>().FirstOrDefault()
        ?? scroller.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
    Assert.True(bar is not null, "the code block must have a horizontal scrollbar");
    return (scroller, bar, block);
  }
}
