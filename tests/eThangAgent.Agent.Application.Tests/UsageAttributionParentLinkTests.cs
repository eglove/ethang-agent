using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The usage ledger's attribution walk, reconciled with #305's one-process-per-agent
///     model.
///     <para>The owner's inventory comment asked for this explicitly: *"The usage ledger's
///     attribution walk still attributes every row after `parent_id` stops being populated by
///     the spawner."*</para>
///     <para>THE PREMISE NEEDS CORRECTING, AND THAT IS THE FINDING. #305 does not stop populating
///     `parent_id`: it changed WHERE an agent runs (its own host process), not how the record
///     describes who spawned it. <c>StartSpawnHandler</c> still persists `parent.Id`, and
///     `AgentRecord.Depth` is still derived from it, so the recursive CTE in
///     <c>SqliteUsageQueries</c> keeps attributing child spend to its session. Had the spawner
///     stopped writing `parent_id`, every subagent's spend would silently leave its session's
///     total — the failure mode this test exists to catch before it can happen.</para>
///     <para>So this pins the INVARIANT rather than a change: a spawned record still names its
///     parent and its depth. If a later change does drop `parent_id` (a chain record supplying
///     the link instead, per #301/#302), this test fails and forces the attribution walk to be
///     re-cut in the same change — which is exactly when it must be.</para></summary>
public class UsageAttributionParentLinkTests
{
  [Fact]
  public async Task ASpawnedChild_StillNamesItsParentAndDepth_SoTheWalkCanAttributeIt()
  {
    // The invariant the attribution CTE depends on: parent_id and depth are written at spawn.
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(
        AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/ws", "openrouter"),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    StartSpawnHandler handler = new(
        store,
        new RecordingRuntime(),
        new SubAgentOptions(null, 2),
        new SpawnOptions("test/model", WorkspaceRoot: "C:/ws", ProviderName: "openrouter"),
        windowSource: new FixedWindow());

    Result<AgentId> spawned = await handler.Execute(
        AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/ws", "openrouter"),
        new SpawnRequest("do the thing", Model: "test/model"),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(spawned.IsSuccess, spawned.IsSuccess ? string.Empty : spawned.Error.Code + ": " + spawned.Error.Message);
    Result<AgentRecord> child = await store.GetAsync(spawned.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(child.IsSuccess);
    // The walk joins on these two facts. A child that lost either would vanish from its
    // session's spend total.
    Assert.Equal(rootId, child.Value.ParentId);
    Assert.Equal(1, child.Value.Depth);
    // And the workspace stamp #305 D5 added, so orphan repair can see it.
    Assert.Equal("C:/ws", child.Value.WorkspaceId);
  }

  /// <summary>A catalog window for the fixture model: production always wires a real source,
  ///     and a spawn without one fails UnknownModelWindow rather than running unaccounted.</summary>
  private sealed class FixedWindow : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult<int?>(200_000);
  }

  /// <summary>Records the start so the handler completes without launching anything.</summary>
  private sealed class RecordingRuntime : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("Unused", "not used in this test")));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("Unused", "not used in this test")));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
        => Result.Success(true);

    public void InterruptSubtree(AgentId rootOfSubtree)
    {
      // No runs to interrupt in this test.
    }

    public void Interrupt(AgentId? childId = null)
    {
      // No runs to interrupt in this test.
    }
  }
}
