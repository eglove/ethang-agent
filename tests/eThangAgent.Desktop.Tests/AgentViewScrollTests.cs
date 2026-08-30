using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless tests for the sticky auto-scroll wiring: user entries must not
///     steal scroll; a scrolled-up user must not be yanked; End re-sticks; and
///     scroll position survives the view being torn down and rebuilt.</summary>
public class AgentViewScrollTests
{
  [AvaloniaFact]
  public void User_Entry_Does_Not_Autoscroll_But_Agent_Reply_Does()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    ScrollViewer scroll = view.GetControl<ScrollViewer>("TranscriptScroll");

    for (int i = 0; i < 300; i++)
    {
      vm.Transcript.AddNotice("filler " + i);
    }
    Dispatcher.UIThread.RunJobs();

    double extentBefore = scroll.Extent.Height;
    Assert.True(extentBefore > scroll.Viewport.Height, "test needs overflowing content");
    Assert.True(scroll.Offset.Y >= extentBefore - scroll.Viewport.Height - 1.0,
        $"expected bottom, offset={scroll.Offset.Y}, extent={extentBefore}");

    double yAtBottom = scroll.Offset.Y;
    vm.Transcript.AddUser("hello");
    Dispatcher.UIThread.RunJobs();
    // The offset itself must not move; the bottom may drift by the new entry's own
    // height as the extent grows under the pinned tail.
    Assert.True(Math.Abs(scroll.Offset.Y - yAtBottom) < 30,
        $"user entry must not scroll: was {yAtBottom}, now {scroll.Offset.Y}");

    vm.Transcript.AddNotice("agent ping");
    Dispatcher.UIThread.RunJobs();
    Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1.0);
  }

  [AvaloniaFact]
  public void Scrolled_Up_User_Is_Not_Yanked_By_New_Agent_Entries()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    ScrollViewer scroll = view.GetControl<ScrollViewer>("TranscriptScroll");

    for (int i = 0; i < 300; i++)
    {
      vm.Transcript.AddNotice("filler " + i);
    }
    Dispatcher.UIThread.RunJobs();
    Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "test needs overflow");

    scroll.Offset = scroll.Offset.WithY(500);
    Dispatcher.UIThread.RunJobs();
    double y = scroll.Offset.Y;
    Assert.True(y < scroll.Extent.Height - scroll.Viewport.Height - 4.0, "precondition: scrolled up");

    for (int i = 0; i < 5; i++)
    {
      vm.Transcript.AddNotice("more " + i);
    }
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(y, scroll.Offset.Y, 1);
  }

  [AvaloniaFact]
  public void End_Key_Scrolls_To_Bottom_And_Resticks()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    ScrollViewer scroll = view.GetControl<ScrollViewer>("TranscriptScroll");

    for (int i = 0; i < 300; i++)
    {
      vm.Transcript.AddNotice("filler " + i);
    }
    Dispatcher.UIThread.RunJobs();

    scroll.Offset = scroll.Offset.WithY(0);
    Dispatcher.UIThread.RunJobs();

    _ = view.Focus();
    window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
    Dispatcher.UIThread.RunJobs();
    Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1.0,
        $"End must reach bottom, offset={scroll.Offset.Y}");
  }

  [AvaloniaFact]
  public void Scroll_Position_Survives_Tab_Switch_Rebuild()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView first = (AgentView)window.Content;
    ScrollViewer scroll1 = first.GetControl<ScrollViewer>("TranscriptScroll");

    for (int i = 0; i < 300; i++)
    {
      vm.Transcript.AddNotice("filler " + i);
    }
    Dispatcher.UIThread.RunJobs();

    scroll1.Offset = scroll1.Offset.WithY(500);
    Dispatcher.UIThread.RunJobs();
    double saved = scroll1.Offset.Y;

    // Tab switch: the old view is discarded, a new view attaches to the same VM.
    window.Content = new StackPanel();
    Dispatcher.UIThread.RunJobs();
    AgentView rebuilt = new() { DataContext = vm };
    window.Content = rebuilt;
    Dispatcher.UIThread.RunJobs();
    // The restore is posted at Loaded priority - run jobs down to that priority.
    Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
    ScrollViewer scroll2 = rebuilt.GetControl<ScrollViewer>("TranscriptScroll");
    Assert.True(scroll2.Offset.Y > 0,
        $"rebuilt view must restore offset ~{saved}, got {scroll2.Offset.Y}");
  }
}
