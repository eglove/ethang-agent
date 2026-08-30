using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

public class AgentViewTests
{
  [AvaloniaFact]
  public void Typing_And_Enter_Sends_User_Message_To_Transcript()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    // Named controls live in the AgentView's own name scope.
    AgentView view = (AgentView)window.Content;

    TextBox input = view.GetControl<TextBox>("InputBox");
    _ = input.Focus();
    input.Text = "hello agent";
    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

    Assert.Contains(vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "hello agent");
  }

  [AvaloniaFact]
  public async Task Escape_While_Busy_Stops_The_Active_Turn()
  {
    TestFixtures.StubAgentRuntime runtime = new();
    TestFixtures.ParkingRunner park = new();
    AgentSessionViewModel vm = BuildBusySession(park, runtime, out Window window);
    _ = vm.SubmitAsync("long running work");

    await park.Started.ConfigureAwait(true);
    Assert.True(vm.IsBusy);

    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

    // Bounded await: before the handler exists (RED) nothing settles the turn, and a
    // wiring regression in GREEN must fail, never hang the suite.
    await vm.WaitForTurnAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    Assert.False(vm.IsBusy);
    Assert.True(park.ObservedToken.IsCancellationRequested);
    Assert.Equal(1, runtime.InterruptAllCount);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [TurnCancelled]", notice.Text, StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public async Task Escape_When_Idle_Is_A_Silent_NoOp()
  {
    TestFixtures.StubAgentRuntime runtime = new();
    TestFixtures.ParkingRunner park = new();
    AgentSessionViewModel vm = BuildBusySession(park, runtime, out Window window);
    Task turnTask = vm.SubmitAsync("quick work");
    await park.Started.ConfigureAwait(true);

    park.Release();
    await turnTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);
    Assert.False(vm.IsBusy);

    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

    Assert.DoesNotContain(vm.Transcript.Entries, e => e is NoticeEntry n && n.Text.Contains("No active turn to stop.", StringComparison.Ordinal));
    Assert.Equal(0, runtime.InterruptAllCount);
  }

  /// <summary>Hosts an AgentView over a session whose turn parks until released or
  /// cancelled — the headless stand-in for a long-running agent turn.</summary>
  private static AgentSessionViewModel BuildBusySession(TestFixtures.ParkingRunner park,
      TestFixtures.StubAgentRuntime runtime, out Window window)
  {
    RecordingLifecycle lifecycle = new(new TestFixtures.StubStore());
    AgentSessionViewModel vm = new(
        park.RunAsync, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          ChildRuntime = runtime,
        });
    window = new Window { Content = new AgentView { DataContext = vm } };
    window.Show();
    // Production realism and event-routing necessity: the key event reaches the tunnel
    // through the focused element's route, so focus the input box (the normal state).
    AgentView view = (AgentView)window.Content;
    _ = view.GetControl<TextBox>("InputBox").Focus();
    return vm;
  }

  /// <summary>Regression (sticky-scroll wiring): the turn pipeline raises notices on the
  ///     worker thread (model selection fires mid-resolver). With a real AgentView
  ///     subscribed to Entries.CollectionChanged, an inline transcript mutation from that
  ///     thread used to run the view's handler cross-thread — its first statement read
  ///     DataContext, Avalonia threw VerifyAccess, and the whole turn died silently
  ///     (the view fire-and-forgets the turn task). Notices must ride the stream bridge
  ///     to the sink — production's sink marshals onto the UI thread — so the transcript
  ///     is never mutated inline on the turn thread.</summary>
  [AvaloniaFact]
  public async Task Turn_Notice_Raised_On_The_Worker_Thread_Lands_On_The_Ui_Thread()
  {
    bool sinkSawNotice = false;
    AgentSessionViewModel? vmRef = null;
    Task Sink(UiStreamEvent evt)
    {
      if (evt is UiStreamEvent.Notice)
      {
        sinkSawNotice = true;
      }

      return vmRef!.ApplyUiStreamEventAsync(evt);
    }
    AgentSessionViewModel vm = new(
        static (command, ct, callbacks, onNotice) =>
        {
          // The VM wraps this runner in DesktopHost.OffUiThread: this body runs on a
          // worker thread, exactly like production's resolver notices.
          onNotice?.Invoke("Model selected: test/model");
          callbacks?.OnContentDelta?.Invoke("ack");
          return Task.FromResult(Result.Success("ack"));
        },
        new RecordingLifecycle(new TestFixtures.StubStore()),
        AgentId.NewId(),
        new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", UiStreamSink = Sink });
    vmRef = vm;

    // DataContext AFTER Show(): the production view is templated inside a shown window,
    // so its wiring (CollectionChanged subscription) must tolerate the dispatcher-bound
    // lifecycle this order produces.
    AgentView view = new();
    Window window = new() { Content = view };
    window.Show();
    view.DataContext = vm;

    Task turnTask = vm.SubmitAsync("hello");
    await turnTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

    Assert.False(vm.IsBusy);
    Assert.Contains(vm.Transcript.Entries,
        e => e is NoticeEntry n && n.Text.Contains("Model selected: test/model", StringComparison.Ordinal));
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is NoticeEntry n
        && n.Text.Contains("TurnFault", StringComparison.Ordinal));
    Assert.True(sinkSawNotice,
        "turn notices must ride the stream bridge to the sink (which marshals onto the UI thread), never mutate the transcript inline on the turn thread");
  }
}
