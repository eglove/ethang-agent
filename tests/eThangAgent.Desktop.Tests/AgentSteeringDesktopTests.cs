using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>While a turn runs, input steers instead of being dropped: it posts to the
/// session inbox and echoes in the transcript; /stop hard-cancels the turn and interrupts
/// the session's sub-agents. All awaits are bounded — every gate has a reachable settle path.</summary>
public class AgentSteeringDesktopTests
{
  /// <summary>Runner that parks on its cancellation token until released or cancelled,
  /// then returns a TurnCancelled failure (the domain contract for interruption).</summary>
  private sealed class ParkingRunner
  {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken ObservedToken { get; private set; }
    public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public void Release() => _release.TrySetResult();

    // IDE0060: parameter required to match the TurnRunner delegate shape; value ignored.
#pragma warning disable IDE0060 // Remove unused parameter
    public async Task<Result<string>> RunAsync(SendMessageCommand _command, CancellationToken ct,
        Action<string>? __ = null, Action<string>? ___ = null, Action? ____ = null,
        Action<string, string>? _____ = null, Action<string, string>? ______ = null,
        Action<string>? _______ = null)
    {
      ObservedToken = ct;
      _ = _started.TrySetResult();
      Task finished = await Task.WhenAny(_release.Task, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(true);
      if (finished == _release.Task)
      {
        return Result.Success("done");
      }

      try
      {
        await finished.ConfigureAwait(true);
      }
      catch (OperationCanceledException) { }
      return Result.Failure<string>(new DomainError("TurnCancelled", "interrupted."));
    }
  }

  private static (AgentSessionViewModel Vm, RecordingLifecycle Lifecycle) Build(
      TurnRunner runner, AgentInbox inbox, TestFixtures.StubAgentRuntime runtime)
  {
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentSessionViewModel vm = new(
        runner, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model", workspaceRoot: @"C:\work\demo",
        inbox: inbox, childRuntime: runtime);
    return (vm, lifecycle);
  }

  [Fact]
  public async Task BusySubmit_PostsToInbox_AndEchoesInTranscript()
  {
    AgentInbox inbox = new();
    ParkingRunner park = new();
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
    ParkingRunner park = new();
    (AgentSessionViewModel? vm, RecordingLifecycle _) = Build(park.RunAsync, new AgentInbox(), runtime);

    Task turnTask = vm.SubmitAsync("long running work");
    await park.Started.ConfigureAwait(true);

    _ = vm.SubmitAsync("/stop");
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
    (AgentSessionViewModel? vm, RecordingLifecycle _) = Build((_, ct, a, b, c, d, e, f) => Task.FromResult(Result.Success("ok")),
        new AgentInbox(), runtime);

    await vm.SubmitAsync("quick turn");
    await vm.WaitForTurnAsync();

    _ = vm.SubmitAsync("/stop");
    await vm.WaitForTurnAsync();

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("No active turn to stop.", notice.Text, StringComparison.Ordinal);
    Assert.Equal(0, runtime.InterruptAllCount);
  }

  [Fact]
  public async Task SteeringWithoutInbox_ShowsNoInboxError()
  {
    ParkingRunner park = new();
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentSessionViewModel vm = new(
        park.RunAsync, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model", workspaceRoot: @"C:\work\demo");

    Task turnTask = vm.SubmitAsync("work");
    await park.Started.ConfigureAwait(true);
    _ = vm.SubmitAsync("steer attempt");

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [NoInbox]", notice.Text, StringComparison.Ordinal);

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  private static List<string> TakeQueued(AgentInbox inbox)
  {
    List<string> taken = [];
    while (inbox.TryTake(out string? text))
    {
      taken.Add(text);
    }

    return taken;
  }
}
