using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class SubAgentSpawnerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static AgentRecord ParentAtDepth(int depth)
        => AgentRecord.Spawned(AgentId.NewId(), null, depth, "parent-model",
            null, "parent task", FixedNow);

    private static SubAgentSpawner MakeSpawner(
        IModelProvider provider,
        FakeAgentStore store,
        SubAgentOptions? options = null,
        IToolRegistry? tools = null)
        => new(
            new FakeModelProviderFactory(provider),
            store,
            tools ?? new ToolRegistry([]),
            new StaticPromptProvider("guide text"),
            options ?? new SubAgentOptions(DefaultModel: "default/sub-model"));

    // --- depth guard ---

    [Fact]
    public async Task Spawn_ParentAtMaxDepth_Rejected_NoWrites_NoFactoryCalls()
    {
        var factory = new FakeModelProviderFactory(new FakeProvider());
        var store = new FakeAgentStore();
        var spawner = new SubAgentSpawner(factory, store, new ToolRegistry([]),
            new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "m/sub"));

        var result = await spawner.SpawnAsync(ParentAtDepth(3), new SpawnRequest("do things"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("DepthExceeded", result.Error!.Code);
        Assert.Contains("depth 3 is at the limit (3)", result.Error.Message);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, store.TotalWrites);
    }

    [Fact]
    public async Task Spawn_Depth2Parent_ChildCompletesAtDepth3_AtBoundary()
    {
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse("deep report", [])));
        var spawner = MakeSpawner(provider, store);

        var result = await spawner.SpawnAsync(ParentAtDepth(2), new SpawnRequest("deep task"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Depth);
    }

    [Fact]
    public async Task Spawn_Depth1Parent_ChildCompletesAtDepth2()
    {
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse("child report", [])));
        var spawner = MakeSpawner(provider, store);

        var result = await spawner.SpawnAsync(ParentAtDepth(1), new SpawnRequest("do things"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Depth);
        Assert.Equal(AgentStatus.Completed, result.Value.Status);
    }

    // --- model resolution precedence ---

    [Fact]
    public async Task Spawn_ExplicitModel_BeatsConfiguredDefault()
    {
        var factory = new FakeModelProviderFactory(new FakeProvider());
        var store = new FakeAgentStore();
        var spawner = new SubAgentSpawner(factory, store, new ToolRegistry([]),
            new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "default/sub-model"));

        var result = await spawner.SpawnAsync(ParentAtDepth(0),
            new SpawnRequest("task", Model: "explicit/model"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("explicit/model", result.Value!.ModelUsed);
        Assert.Equal("explicit/model", factory.LastConfig!.ModelId);
    }

    [Fact]
    public async Task Spawn_NoExplicitModel_UsesConfiguredDefault()
    {
        var factory = new FakeModelProviderFactory(new FakeProvider());
        var store = new FakeAgentStore();
        var spawner = new SubAgentSpawner(factory, store, new ToolRegistry([]),
            new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "default/sub-model"));

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("task"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("default/sub-model", result.Value!.ModelUsed);
        Assert.Equal("default/sub-model", factory.LastConfig!.ModelId);
    }

    [Fact]
    public async Task Spawn_NoModelAnywhere_MissingModelError_NoSideEffects()
    {
        var factory = new FakeModelProviderFactory(new FakeProvider());
        var store = new FakeAgentStore();
        var spawner = new SubAgentSpawner(factory, store, new ToolRegistry([]),
            new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: null));

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("task"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("MissingModel", result.Error!.Code);
        Assert.Equal("supply model or configure SubAgent:DefaultModel", result.Error.Message);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, store.TotalWrites);
    }

    // --- request validation ---

    [Fact]
    public async Task Spawn_EmptyTaskPrompt_RejectedBeforeAnySideEffect()
    {
        var factory = new FakeModelProviderFactory(new FakeProvider());
        var store = new FakeAgentStore();
        var spawner = new SubAgentSpawner(factory, store, new ToolRegistry([]),
            new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "m/sub"));

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("   "),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("TaskPrompt", result.Error!.Message);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, store.TotalWrites);
    }

    // --- persistence flow ---

    [Fact]
    public async Task Spawn_PersistsRunningThenCompleted_WithReportAndIdentity()
    {
        var parent = ParentAtDepth(1);
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse("child report", [])));
        var spawner = MakeSpawner(provider, store,
            options: new SubAgentOptions(DefaultModel: "default/sub-model"));

        var result = await spawner.SpawnAsync(parent,
            new SpawnRequest("summarize the file", Label: "child-a"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var outcome = result.Value!;

        var saved = Assert.Single(store.Saved);
        var updated = Assert.Single(store.Updated);
        Assert.Equal(AgentStatus.Running, saved.Status);
        Assert.Equal(parent.Id, saved.ParentId);
        Assert.Equal(2, saved.Depth);
        Assert.Equal("default/sub-model", saved.ModelUsed);
        Assert.Equal("child-a", saved.Label);
        Assert.Equal("summarize the file", saved.TaskPrompt);

        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal(AgentStatus.Completed, updated.Status);
        Assert.Null(updated.FailureReason);
        Assert.NotNull(updated.CompletedAt);
        Assert.Equal("child report", updated.FinalReport);

        Assert.Equal(saved.Id, outcome.ChildId);
        Assert.Equal(AgentStatus.Completed, outcome.Status);
        Assert.Null(outcome.Reason);
        Assert.Equal("child report", outcome.Report);
        Assert.Equal("default/sub-model", outcome.ModelUsed);
        Assert.Equal(2, outcome.Depth);
    }

    [Fact]
    public async Task Spawn_Completed_AppendsChildTranscript()
    {
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse("child report", [])));
        var spawner = MakeSpawner(provider, store);
        var spawnResult = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("do things"),
            CancellationToken.None);

        var transcript = await store.GetTranscriptAsync(spawnResult.Value!.ChildId);

        Assert.True(transcript.IsSuccess);
        Assert.Equal(2, transcript.Value!.Count);
        Assert.Equal(Role.User, transcript.Value[0].Role);
        Assert.Equal("do things", transcript.Value[0].Content);
        Assert.Equal(Role.Assistant, transcript.Value[1].Role);
        Assert.Equal("child report", transcript.Value[1].Content);
    }

    [Fact]
    public async Task Spawn_SeedsChildConversation_AndSuppliesSystemPromptAndTools()
    {
        var fakeTool = new FakeTool("read_file", "file content");
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse(null,
                [new ToolCallRequest("call_1", "read_file", "{}")])),
            Result<ModelResponse>.Success(new ModelResponse("finished", [])));
        var spawner = MakeSpawner(provider, store,
            tools: new ToolRegistry([fakeTool]));

        await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("read it"),
            CancellationToken.None);

        var request = provider.RequestsSeen[0]; // first of two turns: tool call, then final report
        Assert.Equal("guide text", request.SystemPrompt);
        Assert.NotNull(request.Tools);
        Assert.Contains(request.Tools!, t => t.Name == "read_file");
        Assert.Equal(Role.User, request.Messages[0].Role);
        Assert.Equal("read it", request.Messages[0].Content);
        Assert.Equal("file content", request.Messages[2].Content); // tool result fed back
    }

    // --- failure paths ---

    [Fact]
    public async Task Spawn_Timeout_PersistsFailedTimeout_ReturnsTimeoutError()
    {
        var store = new FakeAgentStore();
        var spawner = MakeSpawner(new BlockingProvider(), store,
            options: new SubAgentOptions(DefaultModel: "m/sub",
                ChildTimeout: TimeSpan.FromMilliseconds(50)));

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("slow task"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Timeout", result.Error!.Code);

        var updated = Assert.Single(store.Updated);
        Assert.Equal(AgentStatus.Failed, updated.Status);
        Assert.Equal(AgentFailureReason.Timeout, updated.FailureReason);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task Spawn_ProviderThrows_PersistsFailedProviderError_ReturnsFailure()
    {
        var store = new FakeAgentStore();
        var spawner = MakeSpawner(new ThrowingProvider(), store);

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("task"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderError", result.Error!.Code);

        var updated = Assert.Single(store.Updated);
        Assert.Equal(AgentStatus.Failed, updated.Status);
        Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
    }

    [Fact]
    public async Task Spawn_MaxIterationsReached_PersistsFailedMaxIterations()
    {
        var store = new FakeAgentStore();
        var spawner = MakeSpawner(new LoopingProvider(), store,
            tools: new ToolRegistry([new FakeTool("loop", "again")]));

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("loop forever"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("MaxIterations", result.Error!.Code);

        var updated = Assert.Single(store.Updated);
        Assert.Equal(AgentStatus.Failed, updated.Status);
        Assert.Equal(AgentFailureReason.MaxIterations, updated.FailureReason);
    }

    // --- report overflow ---

    [Fact]
    public async Task Spawn_ReportOver50KB_AnnotationAppendedToPersistedReportAndOutcome()
    {
        var bigReport = new string('x', 52_000);
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse(bigReport, [])));
        var spawner = MakeSpawner(provider, store);

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("big task"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.StartsWith(bigReport, result.Value!.Report);
        Assert.Contains("[agent] note: report exceeded 50 KB", result.Value.Report);

        var updated = Assert.Single(store.Updated);
        Assert.StartsWith(bigReport, updated.FinalReport!);
        Assert.Contains("[agent] note: report exceeded 50 KB", updated.FinalReport!);
    }

    [Fact]
    public async Task Spawn_ReportUnder50KB_NoAnnotation()
    {
        var store = new FakeAgentStore();
        var provider = new FakeProvider(
            Result<ModelResponse>.Success(new ModelResponse("compact report", [])));
        var spawner = MakeSpawner(provider, store);

        var result = await spawner.SpawnAsync(ParentAtDepth(0), new SpawnRequest("task"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("[agent] note:", result.Value!.Report);
    }
}
