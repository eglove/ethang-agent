using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The spinner timer must run whenever the view is attached to a BUSY session VM.
///     Previously the timer started only from the IsBusy PropertyChanged event, so a view
///     attached AFTER a turn started (tab switch back to a busy workspace, or a tab whose
///     content materialized mid-turn) never started its timer — the spinner froze with the
///     phase label showing Thinking/Streaming.</summary>
public class SpinnerAttachTests
{
  [AvaloniaFact]
  public void Attach_To_AlreadyBusy_VM_Runs_The_Spinner_Timer()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    // Simulate a turn already in flight: IsBusy is set by ExecuteTurnCoreAsync; here the
    // property is the contract under test, not the turn loop.
    typeof(AgentSessionViewModel).GetProperty(nameof(AgentSessionViewModel.IsBusy))!
        .SetValue(vm, true);

    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    AgentView view = (AgentView)window.Content;
    Assert.True(view.IsSpinnerTimerRunning);
  }

  [AvaloniaFact]
  public void Attach_To_Idle_VM_Does_Not_Run_The_Spinner_Timer()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    AgentView view = (AgentView)window.Content;
    Assert.False(view.IsSpinnerTimerRunning);
  }
}
