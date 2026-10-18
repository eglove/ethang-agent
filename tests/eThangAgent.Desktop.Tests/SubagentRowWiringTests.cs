using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

public class SubagentRowWiringTests
{
  [AvaloniaFact]
  public async Task Row_Materialization_With_Started_Event_Does_Not_Crash_The_Dispatcher()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    TestEventStream events = new();
    vm.Subagents.Attach(events, apply => evt => Dispatcher.UIThread.Post(() => apply(evt)));
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    // Production shape: the child run publishes ChildStartedEvent off the UI thread;
    // the marshal posts the row add onto the dispatcher.
    await Task.Run(() => events.Publish(new ChildStartedEvent(
        AgentId.NewId(), DateTimeOffset.UtcNow, null, "test/model", 0))).ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    // The crash fired inside the LAYOUT pass that materializes the row: run one.
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();

    _ = Assert.Single(vm.Subagents.Rows);
  }
}
