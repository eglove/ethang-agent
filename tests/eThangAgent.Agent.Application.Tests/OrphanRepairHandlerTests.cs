using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>R3.2 exactness: a Running record with no live owner is Failed(Interrupted)
///     with an audit row; a Running record owned by either owner is untouched.</summary>
public class OrphanRepairHandlerTests
{
  private static AgentRecord Running(string label)
      => AgentRecord.Spawned(new AgentId(Guid.NewGuid()), parentId: null, depth: 1,
          modelUsed: "m/sub", label: label, taskPrompt: "task", createdAt: DateTimeOffset.UtcNow);

  private sealed class FakeEvents : IWatchdogEventStore
  {
    public List<WatchdogEvent> Rows { get; } = [];

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      Rows.Add(evt);
      return Task.FromResult(Result.Success(evt.Id.ToString()));
    }

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([.. Rows.Take(limit)]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Rows.Count(e => e.AgentId == agentId && e.Kind == kind)));
  }

  [Fact]
  public async Task RunningWithNoOwner_MarkedFailedInterrupted_WithAuditRow()
  {
    FakeAgentStore store = new();
    AgentRecord orphan = Running("orphan");
    _ = await store.SaveAsync(orphan, TestContext.Current.CancellationToken);
    AgentRecord owned = Running("owned-by-host");
    _ = await store.SaveAsync(owned, TestContext.Current.CancellationToken);
    FakeEvents audit = new();
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [],
        declaredLive: () => [owned.Id.Value],
        audit);

    await handler.RepairAsync(TestContext.Current.CancellationToken);

    Result<AgentRecord> orphanAfter = await store.GetAsync(orphan.Id, TestContext.Current.CancellationToken);
    Assert.True(orphanAfter.IsSuccess);
    Assert.Equal(AgentStatus.Failed, orphanAfter.Value.Status);
    Assert.Equal(AgentFailureReason.Interrupted, orphanAfter.Value.FailureReason);
    _ = Assert.NotNull(orphanAfter.Value.CompletedAt);

    Result<AgentRecord> ownedAfter = await store.GetAsync(owned.Id, TestContext.Current.CancellationToken);
    Assert.True(ownedAfter.IsSuccess);
    Assert.Equal(AgentStatus.Running, ownedAfter.Value.Status); // untouched: in the declared set

    WatchdogEvent row = Assert.Single(audit.Rows);
    Assert.Equal(orphan.Id, row.AgentId);
    Assert.Contains("orphan repair", row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunningInInProcessOwner_Untouched()
  {
    FakeAgentStore store = new();
    AgentRecord owned = Running("owned-in-process");
    _ = await store.SaveAsync(owned, TestContext.Current.CancellationToken);
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [owned.Id.Value],
        declaredLive: () => []);

    await handler.RepairAsync(TestContext.Current.CancellationToken);

    Result<AgentRecord> after = await store.GetAsync(owned.Id, TestContext.Current.CancellationToken);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Running, after.Value.Status);
  }

  [Fact]
  public async Task NonRunningRecords_NeverTouched()
  {
    FakeAgentStore store = new();
    AgentRecord completed = AgentRecord.Spawned(new AgentId(Guid.NewGuid()), null, 1, "m", null,
        "t", DateTimeOffset.UtcNow);
    completed = completed with { Status = AgentStatus.Completed, CompletedAt = DateTimeOffset.UtcNow, FinalReport = "done" };
    _ = await store.SaveAsync(completed, TestContext.Current.CancellationToken);
    OrphanRepairHandler handler = new(store, inProcessLive: () => [], declaredLive: () => []);

    await handler.RepairAsync(TestContext.Current.CancellationToken);

    Result<AgentRecord> after = await store.GetAsync(completed.Id, TestContext.Current.CancellationToken);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Completed, after.Value.Status);
  }
}
