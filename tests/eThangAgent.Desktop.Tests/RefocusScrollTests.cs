using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Regression for the refocus scroll jump (plan line 63): clicking back into
///     an unfocused window must not move the transcript scroll. The headless platform
///     cannot reproduce a real OS-level deactivate/reactivate cycle (no deactivate
///     call; the reported offsets come from stored state, not a live layout pass), so
///     this test pins the reproducible analog — the click-refocus on the transcript.
///     The fix's core guard is the unit suite in TranscriptScrollControllerTests:
///     controller-state moves within a tolerance cannot unstick the transcript, so a
///     reactivation-time transient offset report can no longer flip the sticky state.
///     If the jump still reproduces on the real window, the controller guard holds the
///     position even when a transient would try to unstick it.</summary>
public class RefocusScrollTests
{
  [AvaloniaFact]
  public void Click_Refocus_Does_Not_Move_The_Transcript_Scroll()
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

    // Read at the middle of the transcript (analog of the real-world scrolled-up state).
    scroll.Offset = scroll.Offset.WithY(500);
    Dispatcher.UIThread.RunJobs();
    double before = scroll.Offset.Y;

    // Click-refocus: deactivate the window (minimize/another app), then re-activate
    // with a click. The platform lacks a deactivate call, so drive the closest
    // reproducible headless analog: focus the window, click the transcript.
    window.Activate();
    Dispatcher.UIThread.RunJobs();
    window.MouseDown(new Point(30, 200), MouseButton.Left);
    window.MouseUp(new Point(30, 200), MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    double afterRefocusClick = scroll.Offset.Y;

    Assert.Equal(before, afterRefocusClick, 1);
  }
}
