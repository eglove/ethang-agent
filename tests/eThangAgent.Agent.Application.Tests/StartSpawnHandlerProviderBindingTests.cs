
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #124 defect 1 (2026-10-15 incident): every spawned child's agents row
///     carried provider=NULL — AgentRecord.Spawned never stamps the provider while
///     AgentRecord.Root does — so the Desktop subagent panel could not render the child
///     and diagnostics could not attribute the run. The spawn start command must stamp
///     the session's provider binding onto every child record it persists.</summary>
public class StartSpawnHandlerProviderBindingTests
{
  private const string FallbackModel = "openrouter/auto";

  private static AgentRecord Parent(int depth = 0) => new(
      new AgentId(Guid.NewGuid()), null, depth, AgentStatus.Completed, null,
      "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report",
      WorkspaceId: "C:\\ws", Provider: "openrouter");

  [Fact]
  public async Task Execute_CarriesSessionProviderOntoThePersistedChild()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"),
        new SpawnOptions(FallbackModel, ProviderName: "openrouter"), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal("openrouter", saved.Provider);
    Assert.Same(saved, Assert.Single(runtime.Started));
  }

  [Fact]
  public async Task Execute_NoProviderConfigured_ChildRowCarriesUnboundMarker()
  {
    // Legacy wiring (tests, older hosts) supplies no provider name: the row carries the
    // named unbound marker instead of SQL NULL so diagnostics can tell "never bound"
    // from an absent column in query results.
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"),
        new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal(AgentRecord.UnboundProvider, saved.Provider);
  }
}
