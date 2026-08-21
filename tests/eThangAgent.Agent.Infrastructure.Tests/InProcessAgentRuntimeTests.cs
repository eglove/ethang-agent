using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Unit tests for the in-process actor runtime: immediate start, terminal persistence,
/// provider-error fallback, concurrency cap, and slot recycling. Fakes only.</summary>
public class InProcessAgentRuntimeTests
{
    private static AgentRecord RunningChild(string prompt = "child task") =>
        AgentRecord.Spawned(new AgentId(Guid.NewGuid()), parentId: null, depth: 1,
            modelUsed: "mock/model", label: "test", taskPrompt: prompt, createdAt: DateTimeOffset.UtcNow);

    private static AgentRunOutcome CompletedOutcome(AgentId childId, string report) =>
        new(childId, AgentStatus.Completed, Reason: null, Report: report, ModelUsed: "mock/model", Depth: 1);

    /// <summary>Runner whose single shared gate holds children in-flight until the test releases them.</summary>
    private sealed class GateRunner : IAgentRunner
    {
        private readonly TaskCompletionSource<AgentRunOutcome> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AgentRecord> _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AgentRecord> Started { get; } = [];

        /// <summary>Resolves once any child actually entered RunAsync (background dispatch observed).</summary>
        public Task FirstCall => _firstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Complete(AgentRunOutcome outcome) => _gate.SetResult(outcome);

        public void Throw(Exception exception) => _gate.SetException(exception);

        public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
        {
            Started.Add(child);
            _firstCall.TrySetResult(child);
            return _gate.Task;
        }
    }

    /// <summary>Store capturing terminal updates; signals the first one for deterministic awaits.</summary>
    private sealed class FakeStore : IAgentStore
    {
        private readonly TaskCompletionSource<AgentRecord> _firstUpdate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AgentRecord> Updates { get; } = [];

        /// <summary>Completes when the first update lands; times out loudly instead of hanging.</summary>
        public Task<AgentRecord> FirstUpdate => _firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        {
            lock (Updates)
            {
                Updates.Add(record);
            }
            _firstUpdate.TrySetResult(record);
            return Task.FromResult(Result<string>.Success("updated"));
        }

        public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
            => throw new NotSupportedException("not exercised by runtime tests");

        public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
            => throw new NotSupportedException("not exercised by runtime tests");

        public Task<Result<string>> AppendMessageAsync(AgentId id, eThangAgent.ConversationDomain.Message message,
            CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

        public Task<Result<IReadOnlyList<eThangAgent.ConversationDomain.Message>>> GetTranscriptAsync(
            AgentId id, CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

        public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
            CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");
    }

    [Fact]
    public void Constructor_NonPositiveCap_Throws()
    {
        var runner = new GateRunner();
        var store = new FakeStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => new InProcessAgentRuntime(runner, store, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InProcessAgentRuntime(runner, store, -3));
    }

    [Fact]
    public async Task Start_ChildRunning_ReturnsOkImmediately_WithNoStoreUpdate()
    {
        var runner = new GateRunner();
        var store = new FakeStore();
        var runtime = new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 2);
        var child = RunningChild();

        var result = await runtime.Start(child);

        Assert.True(result.IsSuccess);
        Assert.Equal(child.Id, result.Value);
        await runner.FirstCall; // dispatch happened, child still in-flight behind the gate
        Assert.Empty(store.Updates);
    }

    [Fact]
    public async Task RunnerCompletes_Lands_CompletedUpdate_CarryingReport()
    {
        var runner = new GateRunner();
        var store = new FakeStore();
        var runtime = new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 1);
        var child = RunningChild();

        await runtime.Start(child);
        runner.Complete(CompletedOutcome(child.Id, "the child report"));

        var updated = await store.FirstUpdate;

        Assert.Equal(child.Id, updated.Id);
        Assert.Equal(AgentStatus.Completed, updated.Status);
        Assert.Null(updated.FailureReason);
        Assert.Equal("the child report", updated.FinalReport);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task RunnerThrows_Lands_FailedProviderError_Update()
    {
        var runner = new GateRunner();
        var store = new FakeStore();
        var runtime = new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 1);
        var child = RunningChild();

        await runtime.Start(child);
        runner.Throw(new InvalidOperationException("provider exploded"));

        var updated = await store.FirstUpdate;

        Assert.Equal(child.Id, updated.Id);
        Assert.Equal(AgentStatus.Failed, updated.Status);
        Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
        Assert.Equal("Error [ProviderError]: provider exploded", updated.FinalReport);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task AtCapacity_StartFails_CapReached_WithoutSideEffects()
    {
        var runner = new GateRunner();
        var store = new FakeStore();
        var runtime = new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 1);
        var first = RunningChild();

        var accepted = await runtime.Start(first);
        Assert.True(accepted.IsSuccess);
        await runner.FirstCall; // the sole slot's child is provably in-flight

        var rejected = await runtime.Start(RunningChild());

        Assert.False(rejected.IsSuccess);
        Assert.NotNull(rejected.Error);
        Assert.Equal(RuntimeErrors.CapReached,
            $"Error [{rejected.Error.Code}]: {rejected.Error.Message}");
        Assert.Single(runner.Started); // rejected request never reached the runner
        Assert.Empty(store.Updates); // nothing persisted by either start yet
    }

    [Fact]
    public async Task ReleasedSlot_NextStart_Succeeds()
    {
        var runner = new GateRunner();
        var store = new FakeStore();
        var runtime = new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 1);
        var first = RunningChild();

        var firstStart = await runtime.Start(first);
        Assert.True(firstStart.IsSuccess);
        await runner.FirstCall;

        var blocked = await runtime.Start(RunningChild());
        Assert.False(blocked.IsSuccess); // slot held by the gated child

        runner.Complete(CompletedOutcome(first.Id, "first done"));
        await store.FirstUpdate; // terminal update landed

        var next = RunningChild();
        // Slot release races the awaited update; poll briefly until the freed slot admits the child.
        Result<AgentId> secondStart = await runtime.Start(next);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!secondStart.IsSuccess && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            secondStart = await runtime.Start(next);
        }

        Assert.True(secondStart.IsSuccess);
        Assert.Equal(next.Id, secondStart.Value);
    }
}
