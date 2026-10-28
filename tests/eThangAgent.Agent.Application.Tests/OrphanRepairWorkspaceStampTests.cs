using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Workspace-stamp regression (issue #305 D5). A child row that carries NO workspace
///     is invisible to orphan repair: <see cref="OrphanRepairHandler"/> filters
///     <c>r.WorkspaceId == workspaceId</c>, so a NULL-workspace child is skipped on every
///     session open, forever. Measured before the fix: 0 of 2,078 child rows carried a
///     workspace and 131 rows sat marked Running with no run behind them.
///     <para>These tests pin BOTH halves: the record factory now stamps a workspace when one
///     is supplied, and a stamped child is actually REPAIRED by a workspace-scoped pass —
///     which is the criterion the issue states, not merely that the column is populated.</para></summary>
public class OrphanRepairWorkspaceStampTests
{
  private const string Workspace = "C:\\ws-stamped";

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

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
        WatchdogEventKind kind, int limit, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Rows.Count(e => e.AgentId == agentId && e.Kind == kind)));
  }

  [Fact]
  public void Spawned_WithWorkspace_StampsIt()
  {
    AgentRecord child = AgentRecord.Spawned(AgentId.NewId(), AgentId.NewId(), depth: 1,
        modelUsed: "m/sub", label: "child", taskPrompt: "task", DateTimeOffset.UtcNow,
        workspaceId: Workspace);

    Assert.Equal(Workspace, child.WorkspaceId);
  }

  [Fact]
  public void Spawned_WithoutWorkspace_KeepsLegacyNull()
  {
    // Legacy wiring and tests spawn with no workspace: the column stays null rather than
    // being fabricated, so the pre-fix shape is still expressible.
    AgentRecord child = AgentRecord.Spawned(AgentId.NewId(), AgentId.NewId(), depth: 1,
        modelUsed: "m/sub", label: "child", taskPrompt: "task", DateTimeOffset.UtcNow);

    Assert.Null(child.WorkspaceId);
  }

  [Fact]
  public async Task StampedRunningChild_IsRepairedOnSessionOpen()
  {
    // The criterion the issue states: a persisted Running child row with no live owner is
    // repaired to Failed(Interrupted) on the next session open. Before the stamp it was
    // skipped, which is why 131 rows have been stranded since 2026-10-14.
    FakeAgentStore store = new();
    AgentRecord stranded = AgentRecord.Spawned(AgentId.NewId(), AgentId.NewId(), depth: 1,
        modelUsed: "m/sub", label: "stranded", taskPrompt: "task", DateTimeOffset.UtcNow,
        workspaceId: Workspace) with
    { Status = AgentStatus.Running };
    _ = await store.SaveAsync(stranded, TestContext.Current.CancellationToken).ConfigureAwait(true);
    FakeEvents audit = new();
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [],
        declaredLive: () => [],
        audit,
        exempt: null,
        workspaceId: Workspace);

    await handler.RepairAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<AgentRecord> after = await store.GetAsync(stranded.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Failed, after.Value.Status);
    Assert.Equal(AgentFailureReason.Interrupted, after.Value.FailureReason);
    _ = Assert.Single(audit.Rows);
  }

  [Fact]
  public async Task UnstampedRunningChild_IsSkipped_WhichIsTheDefectTheStampCloses()
  {
    // The counter-proof, pinned so the mechanism stays visible: an UNSTAMPED child is
    // skipped by a workspace-scoped repair pass. This is the 131-row failure mode.
    FakeAgentStore store = new();
    AgentRecord unstamped = AgentRecord.Spawned(AgentId.NewId(), AgentId.NewId(), depth: 1,
        modelUsed: "m/sub", label: "unstamped", taskPrompt: "task", DateTimeOffset.UtcNow) with
    { Status = AgentStatus.Running };
    _ = await store.SaveAsync(unstamped, TestContext.Current.CancellationToken).ConfigureAwait(true);
    OrphanRepairHandler handler = new(store,
        inProcessLive: () => [],
        declaredLive: () => [],
        audit: null,
        exempt: null,
        workspaceId: Workspace);

    await handler.RepairAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<AgentRecord> after = await store.GetAsync(unstamped.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(after.IsSuccess);
    Assert.Equal(AgentStatus.Running, after.Value.Status); // still stranded: no workspace to match
  }
}
