using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
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

        public async Task<Result<string>> RunAsync(SendMessageCommand _, CancellationToken ct,
            Action<string>? __ = null, Action<string>? ___ = null, Action? ____ = null,
            Action<string, string>? _____ = null, Action<string, string>? ______ = null)
        {
            ObservedToken = ct;
            _started.TrySetResult();
            var finished = await Task.WhenAny(_release.Task, Task.Delay(Timeout.InfiniteTimeSpan, ct));
            if (finished == _release.Task)
                return Result<string>.Success("done");
            try { await finished; } catch (OperationCanceledException) { }
            return Result<string>.Failure(new Error("TurnCancelled", "interrupted."));
        }
    }

    private static (AgentSessionViewModel Vm, RecordingLifecycle Lifecycle) Build(
        TurnRunner runner, IAgentInbox inbox, TestFixtures.StubAgentRuntime runtime)
    {
        var lifecycle = new RecordingLifecycle(new StubStore());
        var vm = new AgentSessionViewModel(
            runner, lifecycle, AgentId.NewId(), new Conversation(),
            "test/model", workspaceRoot: @"C:\work\demo",
            inbox: inbox, childRuntime: runtime);
        return (vm, lifecycle);
    }

    [Fact]
    public async Task BusySubmit_PostsToInbox_AndEchoesInTranscript()
    {
        var inbox = new AgentInbox();
        var park = new ParkingRunner();
        var (vm, _) = Build(park.RunAsync, inbox, new TestFixtures.StubAgentRuntime());

        var turnTask = vm.SubmitAsync("first question");
        await park.Started;
        Assert.True(vm.IsBusy);

        vm.SubmitAsync("also check the config"); // busy: must steer, not drop

        Assert.Equal("also check the config", Assert.Single(TakeQueued(inbox)));
        Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[^1]);
        Assert.True(vm.MessageCount >= 2);

        park.Release();
        await turnTask;
        await vm.WaitForTurnAsync();
    }

    [Fact]
    public async Task StopWhileBusy_CancelsTurn_AndInterruptsChildren()
    {
        var runtime = new TestFixtures.StubAgentRuntime();
        var park = new ParkingRunner();
        var (vm, _) = Build(park.RunAsync, new AgentInbox(), runtime);

        var turnTask = vm.SubmitAsync("long running work");
        await park.Started;

        vm.SubmitAsync("/stop");
        await turnTask;
        await vm.WaitForTurnAsync();

        Assert.False(vm.IsBusy);
        Assert.True(park.ObservedToken.IsCancellationRequested);
        Assert.Equal(1, runtime.InterruptAllCount);
        var notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
        Assert.Contains("Error [TurnCancelled]", notice.Text);
    }

    [Fact]
    public async Task StopWhenIdle_ShowsNotice_AndDoesNotInterruptChildren()
    {
        var runtime = new TestFixtures.StubAgentRuntime();
        var (vm, _) = Build((_, ct, a, b, c, d, e) => Task.FromResult(Result<string>.Success("ok")),
            new AgentInbox(), runtime);

        await vm.SubmitAsync("quick turn");
        await vm.WaitForTurnAsync();

        vm.SubmitAsync("/stop");
        await vm.WaitForTurnAsync();

        var notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
        Assert.Contains("No active turn to stop.", notice.Text);
        Assert.Equal(0, runtime.InterruptAllCount);
    }

    [Fact]
    public async Task SteeringWithoutInbox_ShowsNoInboxError()
    {
        var park = new ParkingRunner();
        var lifecycle = new RecordingLifecycle(new StubStore());
        var vm = new AgentSessionViewModel(
            park.RunAsync, lifecycle, AgentId.NewId(), new Conversation(),
            "test/model", workspaceRoot: @"C:\work\demo");

        var turnTask = vm.SubmitAsync("work");
        await park.Started;
        vm.SubmitAsync("steer attempt");

        var notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
        Assert.Contains("Error [NoInbox]", notice.Text);

        park.Release();
        await turnTask;
        await vm.WaitForTurnAsync();
    }

    private static IReadOnlyList<string> TakeQueued(IAgentInbox inbox)
    {
        var taken = new List<string>();
        while (inbox.TryTake(out var text)) taken.Add(text);
        return taken;
    }
}