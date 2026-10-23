using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #238: the spawn command persists the child in the QUEUED state, not
///     Running. The runtime owns the Queued -> Running transition (it is the only party
///     that knows the run actually began), so a child that never gets a slot never
///     claims to be running. A plain start still flips it immediately.</summary>
public class StartSpawnHandlerQueueStateTests
{
  private const string FallbackModel = "openrouter/auto";

  private static AgentRecord Parent() => new(
      new AgentId(Guid.NewGuid()), null, 0, AgentStatus.Completed, null,
      "root-model", "root", "root task", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "root report");

  [Fact]
  public async Task Execute_HappyPath_PersistsTheChildQueued_RuntimeOwnsTheRunningFlip()
  {
    FakeAgentStore store = new();
    FakeAgentRuntime runtime = new();
    StartSpawnHandler handler = new(store, runtime, new SubAgentOptions(DefaultModel: "m"),
        new SpawnOptions(FallbackModel), windowSource: new FixedWindowSource());

    Result<AgentId> result = await handler.Execute(Parent(), new SpawnRequest("task"),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    AgentRecord saved = Assert.Single(store.Saved);
    Assert.Equal(AgentStatus.Queued, saved.Status);
    Assert.Null(saved.CompletedAt);
    _ = Assert.Single(runtime.Started);
  }
}
