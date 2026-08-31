using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>While a turn runs, input steers instead of being dropped: it posts to the
/// session inbox and echoes in the transcript; stopping (the Stop button —
/// <see cref="AgentSessionViewModel.RequestStop"/>) hard-cancels the turn and interrupts
/// the session's sub-agents. All awaits are bounded — every gate has a reachable settle path.</summary>
public class AgentSteeringDesktopTests
{
  private static (AgentSessionViewModel Vm, RecordingLifecycle Lifecycle) Build(
      TurnRunner runner, BoundedAgentMailbox inbox, TestFixtures.StubAgentRuntime runtime)
  {
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentSessionViewModel vm = new(
        runner, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          Inbox = inbox,
          ChildRuntime = runtime,
        });
    return (vm, lifecycle);
  }

  [Fact]
  public async Task BusySubmit_PostsToInbox_AndEchoesInTranscript()
  {
    BoundedAgentMailbox inbox = new();
    TestFixtures.ParkingRunner park = new();
    (AgentSessionViewModel? vm, RecordingLifecycle _) = Build(park.RunAsync, inbox, new TestFixtures.StubAgentRuntime());

    Task turnTask = vm.SubmitAsync("first question");
    await park.Started.ConfigureAwait(true);
    Assert.True(vm.IsBusy);

    _ = vm.SubmitAsync("also check the config"); // busy: must steer, not drop

    Assert.Equal("also check the config", Assert.Single(TakeQueued(inbox)));
    _ = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[^1]);
    Assert.True(vm.MessageCount >= 2);

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  [Fact]
  public async Task StopWhileBusy_CancelsTurn_AndInterruptsChildren()
  {
    TestFixtures.StubAgentRuntime runtime = new();
    TestFixtures.ParkingRunner park = new();
    (AgentSessionViewModel? vm, RecordingLifecycle _) = Build(park.RunAsync, new BoundedAgentMailbox(), runtime);

    Task turnTask = vm.SubmitAsync("long running work");
    await park.Started.ConfigureAwait(true);

    vm.RequestStop(); // the Stop button's entry point
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();

    Assert.False(vm.IsBusy);
    Assert.True(park.ObservedToken.IsCancellationRequested);
    Assert.Equal(1, runtime.InterruptAllCount);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [TurnCancelled]", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task StopWhenIdle_ShowsNotice_AndDoesNotInterruptChildren()
  {
    TestFixtures.StubAgentRuntime runtime = new();
    (AgentSessionViewModel? vm, RecordingLifecycle _) = Build((_, _, _, _) => Task.FromResult(Result.Success("ok")),
        new BoundedAgentMailbox(), runtime);

    await vm.SubmitAsync("quick turn");
    await vm.WaitForTurnAsync();

    vm.RequestStop();
    await vm.WaitForTurnAsync();

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("No active turn to stop.", notice.Text, StringComparison.Ordinal);
    Assert.Equal(0, runtime.InterruptAllCount);
  }

  [Fact]
  public async Task SteeringWithoutInbox_ShowsNoInboxError()
  {
    TestFixtures.ParkingRunner park = new();
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentSessionViewModel vm = new(
        park.RunAsync, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo" });

    Task turnTask = vm.SubmitAsync("work");
    await park.Started.ConfigureAwait(true);
    _ = vm.SubmitAsync("steer attempt");

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [NoInbox]", notice.Text, StringComparison.Ordinal);

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  private static List<string> TakeQueued(BoundedAgentMailbox inbox)
  {
    List<string> taken = [];
    while (inbox.TryTake(out string? text))
    {
      taken.Add(text);
    }

    return taken;
  }
}
