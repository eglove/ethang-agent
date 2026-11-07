using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>A drag that ends by CAPTURE LOSS - not DragCompleted - must not freeze the block.
///     <para>The deferral holds renders off while a thumb is held. If it only ever released on
///     DragCompleted, a gesture that ended because the window deactivated (the pointer's capture
///     is lost, and measured: no DragCompleted is raised) would leave the count above zero and
///     defer every later render forever - trading a scrollbar that stops moving for a block that
///     stops streaming, which is the worse failure.</para></summary>
[Trait("Category", "Process")]
public class CodeBlockScrollBarAbortedDragTests
{
  private const string LongLine =
      "var p = System.Diagnostics.Process.GetCurrentProcess(); Output($\"start {p.StartTime.ToUniversalTime():O}\"); var d = Path.Combine(AppContext.BaseDirectory, \"eThangAgent.State.Domain.dll\");";

  [AvaloniaFact]
  public void A_Drag_That_Ends_By_Capture_Loss_Still_Lets_The_Block_Render()
  {
    AgentMarkdownBlock block = new() { MarkdownText = "```csharp\n" + LongLine + "\n```", IsOpen = true };
    Window window = new() { Width = 700, Height = 400, Content = block };
    window.Show();
    for (int i = 0; i < 4; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    Thumb thumb = Find(window);
    thumb.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = default });
    Dispatcher.UIThread.RunJobs();

    // FIRST prove the deferral is engaged, or this test could pass by never engaging at all.
    int before = block.RenderCount;
    block.MarkdownText = "```csharp\n" + LongLine + "\n\nHELD\n```";
    block.FlushPendingRender();
    for (int i = 0; i < 3; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    Assert.Equal(before, block.RenderCount);

    // Now abort the gesture the way a deactivating window does.
    using Pointer pointer = new(0, PointerType.Mouse, true);
    thumb.RaiseEvent(new PointerCaptureLostEventArgs(thumb, pointer));
    for (int i = 0; i < 3; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    Assert.True(block.RenderCount > before,
        "the render held off by the drag must run once the capture is lost, or the block stops streaming forever");
    window.Close();
  }

  private static Thumb Find(Window window)
  {
    Border b = window.GetVisualDescendants().OfType<Border>().First(x => x.Classes.Contains("markdown-code-block"));
    ScrollViewer s = (ScrollViewer)b.Parent!;
    Grid f = (Grid)s.Parent!;
    ScrollBar bar = f.Children.OfType<ScrollBar>().First();
    return bar.GetVisualDescendants().OfType<Thumb>().First();
  }
}
