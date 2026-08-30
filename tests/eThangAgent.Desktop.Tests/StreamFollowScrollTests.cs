using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Stream-follow tests: a streamed delta REPLACES the tail entry record;
///     while the transcript is stuck to the bottom the view must keep the tail in
///     view during the stream - not only when the block closes.</summary>
public class StreamFollowScrollTests
{
  // Deltas use short space-separated words (realistic prose): a long unbreakable
  // token measured in the small headless window sends Avalonia's line breaker into
  // a pathological wrap loop (the 6a8dfb6 deflake lesson). Six words of 30 chars
  // still grow the extent well past a viewport between deltas.
  private const string Delta = "wordwordwordwordwordwordwordwordwordword " +
      "wordwordwordwordwordwordwordwordwordword " +
      "wordwordwordwordwordwordwordwordwordword " +
      "wordwordwordwordwordwordwordwordwordword " +
      "wordwordwordwordwordwordwordwordwordword " +
      "wordwordwordwordwordwordwordwordwordword";

  private static (AgentSessionViewModel Vm, ScrollViewer Scroll) Show()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Width = 900, Height = 600, Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    return (vm, view.GetControl<ScrollViewer>("TranscriptScroll"));
  }

  private static void Fill(TranscriptViewModel transcript)
  {
    for (int i = 0; i < 300; i++)
    {
      transcript.AddNotice("filler " + i);
    }
    Dispatcher.UIThread.RunJobs();
  }

  [AvaloniaFact]
  public void Streamed_Replace_Deltas_Keep_The_Tail_In_View_While_Stuck()
  {
    (AgentSessionViewModel vm, ScrollViewer scroll) = Show();
    Fill(vm.Transcript);

    Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "test needs overflow");
    Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1.0,
        "precondition: pinned to bottom");

    // Simulate a live stream: one open block extended by repeated Replace deltas.
    vm.Transcript.AppendAssistantDelta("#1 ");
    for (int i = 0; i < 40; i++)
    {
      vm.Transcript.AppendAssistantDelta(Delta);
      Dispatcher.UIThread.RunJobs();
      Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2.0,
          $"delta {i}: tail drifted out of view, offset={scroll.Offset.Y}, " +
          $"extent={scroll.Extent.Height}, viewport={scroll.Viewport.Height}");
    }

    // The block is still open - nothing closed, nothing completed.
    Assert.True(((AssistantTextEntry)vm.Transcript.Entries[^1]).IsOpen, "block must still be open");
  }

  [AvaloniaFact]
  public void Streamed_Replace_Deltas_Do_Not_Yank_A_Scrolled_Up_User()
  {
    (AgentSessionViewModel vm, ScrollViewer scroll) = Show();
    Fill(vm.Transcript);

    scroll.Offset = scroll.Offset.WithY(500);
    Dispatcher.UIThread.RunJobs();
    double y = scroll.Offset.Y;
    Assert.True(y < scroll.Extent.Height - scroll.Viewport.Height - 4.0, "precondition: scrolled up");

    vm.Transcript.AppendAssistantDelta("streaming ");
    for (int i = 0; i < 10; i++)
    {
      vm.Transcript.AppendAssistantDelta(Delta);
      Dispatcher.UIThread.RunJobs();
    }

    Assert.Equal(y, scroll.Offset.Y, 1);
  }

  [AvaloniaFact]
  public void Reasoning_Replace_Deltas_Also_Keep_The_Tail_In_View()
  {
    (AgentSessionViewModel vm, ScrollViewer scroll) = Show();
    Fill(vm.Transcript);

    vm.Transcript.AppendReasoning("thinking ");
    for (int i = 0; i < 30; i++)
    {
      vm.Transcript.AppendReasoning(Delta);
      Dispatcher.UIThread.RunJobs();
      Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2.0,
          $"reasoning delta {i}: tail drifted, offset={scroll.Offset.Y}");
    }
  }
}
