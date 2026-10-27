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

  [Fact]
  public async Task Execute_HappyPath_PersistsQueuedRecord_StartsRuntime_ReturnsId()
  {
    List<string> callLog = [];
    FakeAgentStore store = new(callLog);
    FakeAgentRuntime runtime = new(callLog);
    SubAgentOptions options = new(DefaultModel: "fallback-model");
    StartSpawnHandler handler = new(store, runtime, options, new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());
    AgentRecord parent = Parent(depth: 1);

    Result<AgentId> result = await handler.Execute(parent, new SpawnRequest("do the thing", Model: "explicit-model", Label: "lbl"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(store.Saved);
    AgentRecord saved = store.Saved[0];
    Assert.Equal(result.Value, saved.Id);
    Assert.Equal(parent.Id, saved.ParentId);
    Assert.Equal(2, saved.Depth);
    // Issue #238: the spawn command persists QUEUED; the runtime owns the Queued ->
    // Running transition because it is the only party that knows the run began.
    Assert.Equal(AgentStatus.Queued, saved.Status);
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
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest(""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error.Code);
    Assert.Equal("TaskPrompt must be a non-empty string.", result.Error.Message);
    Assert.Equal(0, store.TotalWrites);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_WhitespaceModel_SpecificationMessage_NothingPersistedOrStarted()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task", Model: "   "), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSpawnRequest", result.Error.Code);
    Assert.Equal("Model must be a non-empty provider model reference when supplied.", result.Error.Message);
    Assert.Equal(0, store.TotalWrites);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_DepthAtMax_VerbatimDepthError_NothingPersistedOrStarted()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m", MaxDepth: 3), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(depth: 3), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("DepthExceeded", result.Error.Code);
    Assert.Equal("agent depth 3 is at the limit (3); children cannot spawn further", result.Error.Message);
    Assert.Equal(0, store.TotalWrites);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_NoModelAnywhere_NoSelector_FallsBackToAutoRouter()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

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
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "fallback-model"), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

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
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Same(capError, result.Error);
    _ = Assert.Single(store.Saved);
    // Issue #238: a refused start terminalizes the row - never a Queued record that no
    // run will ever claim.
    AgentRecord terminal = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, terminal.Status);
    Assert.Equal(AgentFailureReason.ProviderError, terminal.FailureReason);
    Assert.Contains("ConcurrencyCapReached", terminal.FinalReport!, StringComparison.Ordinal);
    _ = Assert.Single(runtime.Started);
  }

  [Fact]
  public async Task Execute_StoreSaveFailure_ErrorPropagated_RuntimeNeverStarted()
  {
    DomainError saveError = new("StorageDown", "agent store unavailable.");
    FakeAgentStore store = new() { SaveFailure = saveError };
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"), new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Same(saveError, result.Error);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_SessionModelPreferenceSet_ChildrenFollowIt_AheadOfConfiguredDefault()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "glm-5.3-flash"), new SpawnOptions(FallbackModel, preferences), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

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
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "glm-5.3-flash"), new SpawnOptions(FallbackModel, preferences), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task", Model: "spawn-specific"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("spawn-specific", saved.ModelUsed);
  }

  [Fact]
  public async Task Execute_SessionProviderCommitted_ChildResolvesOnIt_AndFailsWhenTheCatalogLacksIt()
  {
    // Issue #297, D4/D5 at the spawn boundary: the session's pin rides the child's resolution,
    // and a pinned provider the catalog does not offer for the child's model fails the spawn
    // by name instead of quietly dropping the pin (which would split the subtree's cost across
    // providers).
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "alpha/model", ProviderName = "Reka" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null),
        new SpawnOptions(FallbackModel, preferences), windowSource: new FixedWindowSource(),
        catalog: new ListingCatalog(("alpha/model", "DeepInfra")));

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("UnknownPinnedProvider", result.Error.Code);
    Assert.Contains("Reka", result.Error.Message, StringComparison.Ordinal);
    Assert.Empty(store.Saved);
    Assert.Empty(runtime.Started);
  }

  [Fact]
  public async Task Execute_SessionProviderCommitted_ChildResolvesOnIt_WhenTheCatalogOffersIt()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "alpha/model", ProviderName = "DeepInfra" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null),
        new SpawnOptions(FallbackModel, preferences), windowSource: new FixedWindowSource(),
        catalog: new ListingCatalog(("alpha/model", "DeepInfra")));

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("alpha/model", Assert.Single(store.Saved).ModelUsed);
  }

  [Fact]
  public async Task Execute_PreferenceMutatedBetweenExecutions_NextChildSeesNewChoice()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3-flash" };
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: null), new SpawnOptions(FallbackModel, preferences), windowSource: new FixedWindowSource());

    _ = await handler.Execute(Parent(), new SpawnRequest("first"), ct: TestContext.Current.CancellationToken);
    preferences.ModelId = "glm-5.3";
    _ = await handler.Execute(Parent(), new SpawnRequest("second"), ct: TestContext.Current.CancellationToken);

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
