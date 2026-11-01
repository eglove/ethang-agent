using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>A markdown re-render must not steal the scrollbar out from under a drag.
///     <para>The reported symptom: hold the code block's horizontal scrollbar and drag, and the
///     thumb advances a couple of characters and stops; you must release and click again for
///     another tiny step. It was never possible to drag across the block in one gesture.</para>
///     <para>The cause is not the wiring, which is correct in every context measured (a 40 px
///     thumb delta moves the code 156-196 px, in a bare window and inside the real transcript
///     alike). The cause is that a streamed delta re-renders the whole document, and the
///     re-render REPLACES the scroll viewer, the bar, and the thumb. The pointer still holds the
///     OLD thumb - it captured that control - so every later drag delta drives an orphaned
///     scrollbar while the visible block stays frozen. Measured: the orphan's offset climbed
///     626 -> 783 -> 939 while the block on screen did not move off 469.9.</para>
///     <para>So the contract these tests pin is about the GESTURE, not the geometry: while a
///     thumb inside the block is being dragged, a pending re-render must wait, and the same
///     scroll viewer must still be there when the user lets go.</para></summary>
public class CodeBlockScrollBarDragTests
{
  private const string LongLine =
      "var p = System.Diagnostics.Process.GetCurrentProcess(); " +
      "Output($\"app start (UTC): {p.StartTime.ToUniversalTime():O}\"); " +
      "var dll = Path.Combine(AppContext.BaseDirectory, \"eThangAgent.State.Domain.dll\");";

  /// <summary>A delta that lands mid-drag must not replace the scroller. If it does, the thumb
  ///     the pointer captured is no longer the thumb on screen and the rest of the gesture is
  ///     lost - which is exactly the 'moves a tiny amount and stops' the user reported.</summary>
  [AvaloniaFact]
  public void A_Delta_Arriving_Mid_Drag_Does_Not_Replace_The_Scroller()
  {
    (AgentMarkdownBlock block, Window window) = Show();
    (ScrollViewer before, ScrollBar _, Thumb thumb) = Find(window);

    StartDrag(thumb);
    Drag(thumb, 40);
    double dragged = before.Offset.X;
    Assert.True(dragged > 0, $"the drag must move the code before the delta arrives, offset={dragged:F1}");

    // A streamed delta arrives while the pointer is still down.
    block.MarkdownText = Fenced() + "\n\nafter";
    block.FlushPendingRender();
    Dispatcher.UIThread.RunJobs();

    (ScrollViewer after, _, _) = Find(window);
    Assert.True(ReferenceEquals(before, after),
        "a re-render during a drag replaced the scroll viewer; the pointer still holds the old thumb, " +
        "so the rest of the gesture drives a control that is no longer on screen");
    window.Close();
  }

  /// <summary>The same contract stated as behaviour: after the mid-drag delta, more thumb
  ///     movement must still move the CODE the user can see.</summary>
  [AvaloniaFact]
  public void The_Code_Keeps_Following_The_Thumb_After_A_Mid_Drag_Delta()
  {
    (AgentMarkdownBlock block, Window window) = Show();
    (ScrollViewer scroller, ScrollBar _, Thumb thumb) = Find(window);

    StartDrag(thumb);
    Drag(thumb, 40);
    double beforeDelta = scroller.Offset.X;

    block.MarkdownText = Fenced() + "\n\nafter";
    block.FlushPendingRender();
    Dispatcher.UIThread.RunJobs();

    (ScrollViewer visible, _, _) = Find(window);
    Drag(thumb, 40);
    Drag(thumb, 40);
    Dispatcher.UIThread.RunJobs();

    Assert.True(visible.Offset.X > beforeDelta,
        $"the code must keep scrolling under the held thumb, but it stayed at {visible.Offset.X:F1} " +
        $"(it was {beforeDelta:F1} before the delta arrived)");
    window.Close();
  }

  /// <summary>And the deferral must not LOSE the render: once the drag ends the pending delta is
  ///     applied, and the scroll position the user dragged to survives it.</summary>
  [AvaloniaFact]
  public void The_Deferred_Render_Applies_When_The_Drag_Ends_And_Keeps_The_Position()
  {
    (AgentMarkdownBlock block, Window window) = Show();
    (ScrollViewer _, ScrollBar _, Thumb thumb) = Find(window);

    StartDrag(thumb);
    Drag(thumb, 40);
    double dragged = Find(window).Scroller.Offset.X;

    block.MarkdownText = Fenced() + "\n\nAFTER-DELTA-MARKER";
    block.FlushPendingRender();
    Dispatcher.UIThread.RunJobs();
    int during = block.RenderCount;

    EndDrag(thumb);
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    Assert.True(block.RenderCount > during, "the deferred render must run once the drag ends");
    Assert.Contains("AFTER-DELTA-MARKER", block.MarkdownText, StringComparison.Ordinal);
    ScrollViewer after = Find(window).Scroller;
    Assert.True(Math.Abs(after.Offset.X - dragged) < 1.0,
        $"the position the user dragged to must survive the deferred render, but it went {dragged:F1} -> {after.Offset.X:F1}");
    window.Close();
  }

  private static string Fenced() => "```csharp\n" + LongLine + "\n```";

  private static (AgentMarkdownBlock Block, Window Window) Show()
  {
    AgentMarkdownBlock block = new() { MarkdownText = Fenced(), IsOpen = true };
    Window window = new() { Width = 700, Height = 400, Content = block };
    window.Show();
    for (int i = 0; i < 4; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    return (block, window);
  }

  private static (ScrollViewer Scroller, ScrollBar Bar, Thumb Thumb) Find(Window window)
  {
    Border block = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains("markdown-code-block"));
    ScrollViewer scroller = (ScrollViewer)block.Parent!;
    Grid frame = (Grid)scroller.Parent!;
    ScrollBar bar = frame.Children.OfType<ScrollBar>().First();
    Thumb thumb = bar.GetVisualDescendants().OfType<Thumb>().First();
    return (scroller, bar, thumb);
  }

  private static void StartDrag(Thumb thumb) =>
      thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = default });

  private static void Drag(Thumb thumb, double dx) =>
      thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(dx, 0) });

  private static void EndDrag(Thumb thumb) =>
      thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = default });
}
