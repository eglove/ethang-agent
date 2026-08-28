using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class StartSpawnHandlerTests
{
  private const string FallbackModel = "openrouter/auto";
  private static AgentRecord Parent(int depth = 0) => new(
      new AgentId(Guid.NewGuid()), null, depth, AgentStatus.Completed, null,
      "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report");

  private sealed class FakeModelSelector(ModelSelectionResult result) : IModelSelector
  {
    private readonly ModelSelectionResult _result = result;
    public Task<Result<ModelSelectionResult>> SelectAsync(string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_result));
  }

  private sealed class FailingModelSelector : IModelSelector
  {
    public Task<Result<ModelSelectionResult>> SelectAsync(string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<ModelSelectionResult>(new DomainError("SelectionFailed", "boom")));
  }

  [Fact]
  public async Task Execute_HappyPath_PersistsRunningRecord_StartsRuntime_ReturnsId()
  {
    List<string> callLog = [];
    FakeAgentStore store = new(callLog);
    FakeAgentRuntime runtime = new(callLog);
    SubAgentOptions options = new(DefaultModel: "fallback-model");
    StartSpawnHandler handler = new(store, runtime, options, FallbackModel);
    AgentRecord parent = Parent(depth: 1);

    Result<AgentId> result = await handler.Execute(parent, new SpawnRequest("do the thing", Model: "explicit-model", Label: "lbl"));

    Assert.True(result.IsSuccess);
    _ = Assert.Single(store.Saved);
    AgentRecord saved = store.Saved[0];
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

    AgentRecord started = Assert.Single(runtime.Started);
    Assert.Same(saved, started);

    int saveIndex = callLog.IndexIf(c => c.StartsWith("save:", StringComparison.Ordinal));
    int startIndex = callLog.IndexIf(c => c.StartsWith("start:", StringComparison.Ordinal));
    Assert.True(saveIndex < startIndex, $"expected save before start, got [{string.Join(", ", callLog)}]");
  }

  [Fact]
  public async Task Execute_EmptyTaskPrompt_SpecificationMessage_NothingPersistedOrStarted()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest(""));

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error!.Code);
    Assert.Equal("TaskPrompt must be a non-empty string.", result.Error.Message);
    Assert.Equal(0, store.TotalWrites);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_WhitespaceModel_SpecificationMessage_NothingPersistedOrStarted()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task", Model: "   "));

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error!.Code);
    Assert.Equal("Model must be a non-empty provider model reference when supplied.", result.Error.Message);
    Assert.Equal(0, store.TotalWrites);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_DepthAtMax_VerbatimDepthError_NothingPersistedOrStarted()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m", MaxDepth: 3), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(depth: 3), new SpawnRequest("task"));

    Assert.False(result.IsSuccess);
    Assert.Equal("DepthExceeded", result.Error!.Code);
    Assert.Equal("agent depth 3 is at the limit (3); children cannot spawn further", result.Error.Message);
    Assert.Equal(0, store.TotalWrites);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_NoModelAnywhere_NoSelector_FallsBackToAutoRouter()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"));

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("openrouter/auto", saved.ModelUsed);
    Assert.Same(saved, Assert.Single(runtime.Started));
  }

  [Fact]
  public async Task Execute_NoExplicitModel_ConfiguredDefaultFlowsIntoPersistedRecord()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "fallback-model"), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"));

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("fallback-model", saved.ModelUsed);
    Assert.Same(saved, Assert.Single(runtime.Started));
  }

  [Fact]
  public async Task Execute_RuntimeCapFailure_PropagatedAfterRecordWasPersisted()
  {
    DomainError capError = new("ConcurrencyCapReached", "runtime at capacity");
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new() { StartOutcome = Result.Failure<AgentId>(capError) };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"));

    Assert.False(result.IsSuccess);
    Assert.Same(capError, result.Error);
    _ = Assert.Single(store.Saved);
    Assert.Equal(AgentStatus.Running, store.Saved[0].Status);
    _ = Assert.Single(runtime.Started);
  }

  [Fact]
  public async Task Execute_StoreSaveFailure_ErrorPropagated_RuntimeNeverStarted()
  {
    DomainError saveError = new("StorageDown", "agent store unavailable.");
    FakeAgentStore store = new() { SaveFailure = saveError };
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), FallbackModel);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"));

    Assert.False(result.IsSuccess);
    Assert.Same(saveError, result.Error);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_NoExplicitModel_NoDefault_SelectorSucceeds_UsesSelectedModel()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    ModelSelectionResult selection = new("anthropic/claude-3.5-sonnet", "Anthropic",
        new TaskCategory(["coding"], 4, false, true, null, null),
        new ModelFilter(null, null, null, null, true, null, null, null, null, null, null), "best");
    FakeModelSelector selector = new(selection);
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null), FallbackModel, modelSelector: selector);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("write code"));

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("anthropic/claude-3.5-sonnet", saved.ModelUsed);
  }

  [Fact]
  public async Task Execute_NoExplicitModel_NoDefault_SelectorFails_FallsBackToAutoRouter()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    FailingModelSelector selector = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null), FallbackModel, modelSelector: selector);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("write code"));

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("openrouter/auto", saved.ModelUsed);
  }

  [Fact]
  public async Task Execute_SessionModelPreferenceSet_ChildrenFollowIt_AheadOfConfiguredDefault()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "glm-5.3-flash"), FallbackModel, preferences);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"));

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("glm-5.3", saved.ModelUsed);
  }

  [Fact]
  public async Task Execute_ExplicitSpawnModel_StillBeatsSessionModelPreference()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "glm-5.3-flash"), FallbackModel, preferences);

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task", Model: "spawn-specific"));

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("spawn-specific", saved.ModelUsed);
  }

  [Fact]
  public async Task Execute_PreferenceMutatedBetweenExecutions_NextChildSeesNewChoice()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3-flash" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null), FallbackModel, preferences);

    _ = await handler.Execute(Parent(), new SpawnRequest("first"));
    preferences.ModelId = "glm-5.3";
    _ = await handler.Execute(Parent(), new SpawnRequest("second"));

    Assert.Equal(2, store.Saved.Count);
    Assert.Equal("glm-5.3-flash", store.Saved[0].ModelUsed);
    Assert.Equal("glm-5.3", store.Saved[1].ModelUsed);
  }
}

file static class ListExtensions
{
  public static int IndexIf<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
  {
    for (int i = 0; i < list.Count; i++)
    {
      if (predicate(list[i]))
      {
        return i;
      }
    }

    return -1;
  }
}
