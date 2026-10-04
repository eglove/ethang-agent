using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Workspace-scope regression (2026-10-04): repair ran over the GLOBAL agents
///     table on every session open, so opening workspace B marked workspace A's live
///     root and children Failed(Interrupted) — twice in one day, plus a documented
///     false positive the previous day. Repair must touch ONLY the opening session's
///     workspace: other workspaces' Running rows are other sessions' live state.
///     A null scope keeps the legacy global behavior (tests, legacy wiring).</summary>
public class OrphanRepairWorkspaceScopeTests
{
  private static AgentRecord Running(string label, string? workspaceId)
      => AgentRecord.Spawned(new AgentId(Guid.NewGuid()), parentId: null, depth: 1,
          modelUsed: "m/sub", label: label, taskPrompt: "task", createdAt: DateTimeOffset.UtcNow)
      with
      { WorkspaceId = workspaceId };

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
  public async Task OtherWorkspacesRunningRows_NeverTouched()
  {
    FakeAgentStore store = new();
    AgentRecord foreign = Running("live-in-workspace-a", "C:\\ws-a");
    _ = await store.SaveAsync(foreign, TestContext.Current.CancellationToken).ConfigureAwait(true);
    AgentRecord own = Running("orphan-in-workspace-b", "C:\\ws-b");
    _ = await store.SaveAsync(own, TestContext.Current.CancellationToken).ConfigureAwait(true);
    FakeEvents audit = new();
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [],
        declaredLive: () => [],
        audit,
        exempt: null,
        workspaceId: "C:\\ws-b");

    await handler.RepairAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<AgentRecord> foreignAfter = await store.GetAsync(foreign.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(foreignAfter.IsSuccess);
    Assert.Equal(AgentStatus.Running, foreignAfter.Value.Status); // untouched: different workspace
    Result<AgentRecord> ownAfter = await store.GetAsync(own.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(ownAfter.IsSuccess);
    Assert.Equal(AgentStatus.Failed, ownAfter.Value.Status); // same-workspace orphan still repaired
  }

  [Fact]
  public async Task NullScope_RepairsGlobally_LegacyBehavior()
  {
    FakeAgentStore store = new();
    AgentRecord foreign = Running("live-in-workspace-a", "C:\\ws-a");
    _ = await store.SaveAsync(foreign, TestContext.Current.CancellationToken).ConfigureAwait(true);
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [],
        declaredLive: () => [],
        audit: null,
        exempt: null,
        workspaceId: null);

    await handler.RepairAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<AgentRecord> after = await store.GetAsync(foreign.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Failed, after.Value.Status); // legacy global repair
  }
}
