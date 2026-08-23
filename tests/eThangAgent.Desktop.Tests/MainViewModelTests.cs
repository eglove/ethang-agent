using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Recording subclass — overrides AppendExchangeAsync to count calls
/// without executing real persistence (the conversation in these tests
/// has no messages, so calling base would throw an index-out-of-range).
/// RootSessionLifecycle persistence semantics are covered in
/// eThangAgent.Composition.Tests.
/// </summary>
public sealed class RecordingLifecycle(IAgentStore store) : RootSessionLifecycle(store)
{
    public int Exchanges;

    public override Task AppendExchangeAsync(
        AgentId rootId, Conversation c, int before,
        Result<string> result, Action<string> err)
    {
        Exchanges++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Lifecycle override whose persistence step fails through the reportError
/// callback — mirroring how RootSessionLifecycle surfaces store failures —
/// while still recording that the exchange was booked.
/// </summary>
public sealed class PersistenceErroringLifecycle(IAgentStore store)
    : RootSessionLifecycle(store)
{
    public int Exchanges;

    public override Task AppendExchangeAsync(
        AgentId rootId, Conversation c, int before,
        Result<string> result, Action<string> err)
    {
        Exchanges++;
        err("Error [DbDown]: nope");
        return Task.CompletedTask;
    }
}

public class MainViewModelTests
{
    private static (MainViewModel Vm, List<string> Errors, RecordingLifecycle Lifecycle)
        Build(TurnRunner runner)
    {
        var store = new StubStore();
        var lifecycle = new RecordingLifecycle(store);
        var errors = new List<string>();
        var vm = new MainViewModel(
            runner, lifecycle, AgentId.NewId(), new Conversation(),
            "test/model", () => { });
        return (vm, errors, lifecycle);
    }

    // ── 1. /help ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Help_Prints_Command_List_Not_Sent_To_Model()
    {
        var sent = 0;
        var (vm, _, _) = Build((_, _, _, _, _, _, _) =>
        {
            sent++;
            return Task.FromResult(Result<string>.Success(""));
        });

        await vm.SubmitAsync("/help");

        Assert.Equal(0, sent);
        Assert.False(vm.IsBusy);
        var notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
        Assert.Contains("/help", notice.Text);
        Assert.Contains("/exit", notice.Text);
        Assert.Contains("/quit", notice.Text);
    }

    // ── 2. /exit, /quit ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("/exit")]
    [InlineData("/quit")]
    public async Task Quit_Commands_Request_Close_Without_Model_Call(string cmd)
    {
        var sent = 0;
        var closed = false;
        var store = new StubStore();
        var vm = new MainViewModel(
            (_, _, _, _, _, _, _) => { sent++; return Task.FromResult(Result<string>.Success("")); },
            new RecordingLifecycle(store), AgentId.NewId(), new Conversation(), "m",
            () => closed = true);

        await vm.SubmitAsync(cmd);

        Assert.True(closed);
        Assert.Equal(0, sent);
    }

    // ── 3. Normal turn ────────────────────────────────────────────────────────

    [Fact]
    public async Task Normal_Turn_Appends_User_Entry_Disables_Input_And_Books_Exchange()
    {
        var (vm, _, lifecycle) = Build(async (_, _, onContent, _, _, _, _) =>
        {
            onContent!("hel");
            onContent!("lo");
            await Task.Yield();
            return Result<string>.Success("hello");
        });

        var turnTask = vm.SubmitAsync("hi");
        await turnTask;
        await vm.WaitForTurnAsync();

        Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[0]);
        var last = Assert.IsType<AssistantTextEntry>(vm.Transcript.Entries[^1]);
        Assert.Equal("hello", last.Text);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, lifecycle.Exchanges);
        Assert.Equal(1, vm.MessageCount);
    }

    // ── 3a. Failure produces error notice ─────────────────────────────────────

    [Fact]
    public async Task Failure_Produces_Error_Notice_With_Code()
    {
        var (vm, _, _) = Build((_, _, _, _, _, _, _) =>
            Task.FromResult(Result<string>.Failure(new Error("RateLimited", "slow down"))));

        await vm.SubmitAsync("go");
        await vm.WaitForTurnAsync();

        var notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
        Assert.Contains("Error [RateLimited]: slow down", notice.Text);
    }

    // ── 3b. Success with no deltas falls back to notice ───────────────────────

    [Fact]
    public async Task Success_Without_Streamed_Deltas_Falls_Back_To_Final_Text_Notice()
    {
        var (vm, _, _) = Build((_, _, _, _, _, _, _) =>
            Task.FromResult(Result<string>.Success("plain answer")));

        await vm.SubmitAsync("q");
        await vm.WaitForTurnAsync();

        var notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
        Assert.Contains("plain answer", notice.Text);
    }

    // ── 4. Busy submissions ignored ───────────────────────────────────────────

    [Fact]
    public async Task Submission_While_Busy_Is_Ignored()
    {
        var release = new TaskCompletionSource();
        var (vm, _, _) = Build((_, _, _, _, _, _, _) =>
            release.Task.ContinueWith(_ => Result<string>.Success("done"),
                TaskContinuationOptions.ExecuteSynchronously));

        var first = vm.SubmitAsync("one");
        Assert.True(vm.IsBusy);

        await vm.SubmitAsync("two"); // ignored — no second user entry

        release.SetResult();
        await first;
        await vm.WaitForTurnAsync();

        Assert.Equal(1, vm.Transcript.Entries.OfType<UserMessageEntry>().Count());
    }

    // ── 5. Persistence errors route through reportError → notice entries ─────

    [Fact]
    public async Task Persistence_Error_Routes_Through_ReportError_To_Notice_Entry()
    {
        var lifecycle = new PersistenceErroringLifecycle(new StubStore());
        var vm = new MainViewModel(
            (_, _, _, _, _, _, _) =>
                Task.FromResult(Result<string>.Success("answer")),
            lifecycle, AgentId.NewId(), new Conversation(), "test/model",
            () => { });

        await vm.SubmitAsync("hi");
        await vm.WaitForTurnAsync();

        Assert.Contains(vm.Transcript.Entries.OfType<NoticeEntry>(),
            n => n.Text.Contains("Error [DbDown]"));
        Assert.Equal(1, lifecycle.Exchanges);
    }

    // ── 6. Blank input ignored ────────────────────────────────────────────────

    [Fact]
    public async Task Blank_Input_Is_Ignored()
    {
        var (vm, _, _) = Build((_, _, _, _, _, _, _) =>
            Task.FromResult(Result<string>.Success("x")));

        await vm.SubmitAsync("   ");

        Assert.Empty(vm.Transcript.Entries);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubStore : IAgentStore
    {
        public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("saved"));

        public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
            => Task.FromResult(Result<AgentRecord>.Failure(new Error("NotFound", "stub")));

        public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("updated"));

        public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("appended"));

        public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
