using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #238: a persisted QUEUED row is a live claim the same way a Running row
///     is — but a queued row has no live owner by construction (its run never began), so
///     startup repair must terminalize it exactly like an orphaned Running child. Without
///     this a parked spawn survives a restart as an unretrievable Queued row.</summary>
public class OrphanRepairQueuedTests
{
  private static AgentRecord Queued(string label)
      => AgentRecord.Spawned(new AgentId(Guid.NewGuid()), parentId: null, depth: 1,
          modelUsed: "m/sub", label: label, taskPrompt: "task", createdAt: DateTimeOffset.UtcNow)
          with
      { Status = AgentStatus.Queued };

  [Fact]
  public async Task QueuedRowWithNoOwner_MarkedFailedInterrupted()
  {
    FakeAgentStore store = new();
    AgentRecord parked = Queued("parked");
    _ = await store.SaveAsync(parked, TestContext.Current.CancellationToken);
    OrphanRepairHandler handler = new(store, inProcessLive: () => [], declaredLive: () => []);

    await handler.RepairAsync(TestContext.Current.CancellationToken);

    Result<AgentRecord> after = await store.GetAsync(parked.Id, TestContext.Current.CancellationToken);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Failed, after.Value.Status);
    Assert.Equal(AgentFailureReason.Interrupted, after.Value.FailureReason);
    _ = Assert.NotNull(after.Value.CompletedAt);
  }

  [Fact]
  public async Task QueuedRowOwnedByALiveRuntime_IsLeftAlone()
  {
    FakeAgentStore store = new();
    AgentRecord parked = Queued("parked");
    _ = await store.SaveAsync(parked, TestContext.Current.CancellationToken);
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [parked.Id.Value],
        declaredLive: () => []);

    await handler.RepairAsync(TestContext.Current.CancellationToken);

    Result<AgentRecord> after = await store.GetAsync(parked.Id, TestContext.Current.CancellationToken);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Queued, after.Value.Status);
  }
}
