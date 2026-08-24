using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class StartSpawnHandlerTests
{
    private static AgentRecord Parent(int depth = 0) => new(
        new AgentId(Guid.NewGuid()), null, depth, AgentStatus.Completed, null,
        "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report");

    [Fact]
    public async Task Execute_HappyPath_PersistsRunningRecord_StartsRuntime_ReturnsId()
    {
        var callLog = new List<string>();
        var store = new FakeAgentStore(callLog);
        var runtime = new FakeAgentRuntime(callLog);
        var options = new SubAgentOptions(DefaultModel: "fallback-model");
        var handler = new StartSpawnHandler(store, runtime, options);
        var parent = Parent(depth: 1);

        var result = await handler.Execute(parent, new SpawnRequest("do the thing", Model: "explicit-model", Label: "lbl"));

        Assert.True(result.IsSuccess);
        Assert.Single(store.Saved);
        var saved = store.Saved[0];
        Assert.Equal(result.Value, saved.Id);
        Assert.Equal(parent.Id, saved.ParentId);
        Assert.Equal(2, saved.Depth);
        Assert.Equal(AgentStatus.Running, saved.Status);
        Assert.Null(saved.FailureReason);
        Assert.Equal("explicit-model", saved.ModelUsed);
        Assert.Equal("lbl", saved.Label);
        Assert.Equal("do the thing", saved.TaskPrompt);
        Assert.Null(saved.CompletedAt);
        Assert.Null(saved.FinalReport);

        var started = Assert.Single(runtime.Started);
        Assert.Same(saved, started);

        var saveIndex = callLog.IndexIf(c => c.StartsWith("save:"));
        var startIndex = callLog.IndexIf(c => c.StartsWith("start:"));
        Assert.True(saveIndex < startIndex, $"expected save before start, got [{string.Join(", ", callLog)}]");
    }

    [Fact]
    public async Task Execute_EmptyTaskPrompt_SpecificationMessage_NothingPersistedOrStarted()
    {
        var store = new FakeAgentStore();
        var runtime = new FakeAgentRuntime();
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: "m"));

        var result = await handler.Execute(Parent(), new SpawnRequest(""));

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidSpawnRequest", result.Error!.Code);
        Assert.Equal("TaskPrompt must be a non-empty string.", result.Error.Message);
        Assert.Equal(0, store.TotalWrites);
        Assert.Empty(runtime.Started);
    }

    [Fact]
    public async Task Execute_WhitespaceModel_SpecificationMessage_NothingPersistedOrStarted()
    {
        var store = new FakeAgentStore();
        var runtime = new FakeAgentRuntime();
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: "m"));

        var result = await handler.Execute(Parent(), new SpawnRequest("task", Model: "   "));

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidSpawnRequest", result.Error!.Code);
        Assert.Equal("Model must be a non-empty provider model reference when supplied.", result.Error.Message);
        Assert.Equal(0, store.TotalWrites);
        Assert.Empty(runtime.Started);
    }

    [Fact]
    public async Task Execute_DepthAtMax_VerbatimDepthError_NothingPersistedOrStarted()
    {
        var store = new FakeAgentStore();
        var runtime = new FakeAgentRuntime();
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: "m", MaxDepth: 3));

        var result = await handler.Execute(Parent(depth: 3), new SpawnRequest("task"));

        Assert.False(result.IsSuccess);
        Assert.Equal("DepthExceeded", result.Error!.Code);
        Assert.Equal("agent depth 3 is at the limit (3); children cannot spawn further", result.Error.Message);
        Assert.Equal(0, store.TotalWrites);
        Assert.Empty(runtime.Started);
    }

    [Fact]
    public async Task Execute_NoModelAnywhere_MissingModelError_NoSideEffects()
    {
        var store = new FakeAgentStore();
        var runtime = new FakeAgentRuntime();
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: null));

        var result = await handler.Execute(Parent(), new SpawnRequest("task"));

        Assert.False(result.IsSuccess);
        Assert.Equal("MissingModel", result.Error!.Code);
        Assert.Equal("Provide a model reference or configure SubAgent:DefaultModel.", result.Error.Message);
        Assert.Equal(0, store.TotalWrites);
        Assert.Empty(runtime.Started);
    }

    [Fact]
    public async Task Execute_NoExplicitModel_ConfiguredDefaultFlowsIntoPersistedRecord()
    {
        var store = new FakeAgentStore();
        var runtime = new FakeAgentRuntime();
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: "fallback-model"));

        var result = await handler.Execute(Parent(), new SpawnRequest("task"));

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(store.Saved);
        Assert.Equal("fallback-model", saved.ModelUsed);
        Assert.Same(saved, Assert.Single(runtime.Started));
    }

    [Fact]
    public async Task Execute_RuntimeCapFailure_PropagatedAfterRecordWasPersisted()
    {
        var capError = new Error("ConcurrencyCapReached", "runtime at capacity");
        var store = new FakeAgentStore();
        var runtime = new FakeAgentRuntime { StartOutcome = Result<AgentId>.Failure(capError) };
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: "m"));

        var result = await handler.Execute(Parent(), new SpawnRequest("task"));

        Assert.False(result.IsSuccess);
        Assert.Same(capError, result.Error);
        Assert.Single(store.Saved);
        Assert.Equal(AgentStatus.Running, store.Saved[0].Status);
        Assert.Single(runtime.Started);
    }

    [Fact]
    public async Task Execute_StoreSaveFailure_ErrorPropagated_RuntimeNeverStarted()
    {
        var saveError = new Error("StorageDown", "agent store unavailable.");
        var store = new FakeAgentStore { SaveFailure = saveError };
        var runtime = new FakeAgentRuntime();
        var handler = new StartSpawnHandler(store, runtime, new SubAgentOptions(DefaultModel: "m"));

        var result = await handler.Execute(Parent(), new SpawnRequest("task"));

        Assert.False(result.IsSuccess);
        Assert.Same(saveError, result.Error);
        Assert.Empty(runtime.Started);
    }
}

file static class ListExtensions
{
    public static int IndexIf<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
            if (predicate(list[i]))
                return i;
        return -1;
    }
}
