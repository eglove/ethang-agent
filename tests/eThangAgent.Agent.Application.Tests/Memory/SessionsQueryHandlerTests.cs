using eThangAgent.Agent.Application.Memory;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests.Memory;

/// <summary>Sessions query handler over a seeded fake store: validation strings verbatim,
///     newest-first ordering, summary shape with entry counts, and the constant hot tier.</summary>
public class SessionsQueryHandlerTests
{
  private static readonly DateTimeOffset Base =
      new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static DateTimeOffset At(double minutes) => Base.AddMinutes(minutes);

  private readonly FakeAgentStore _store = new();
  private readonly SessionsQueryHandler _handler;

  public SessionsQueryHandlerTests()
      => _handler = new SessionsQueryHandler(_store);

  /// <summary>Seeds a completed root, a running child, and a failed orphan child
  ///     (parent row absent) — saved oldest-first so ordering must reverse it.</summary>
  private async Task<(AgentId RootId, AgentId ChildId, AgentId OrphanId)> SeedSessionsAsync()
  {
    AgentId rootId = AgentId.NewId();
    AgentId childId = AgentId.NewId();
    AgentId orphanId = AgentId.NewId();

    AgentRecord root = AgentRecord.Root(rootId, At(0));
    _ = await _store.SaveAsync(root);
    _ = await _store.UpdateAsync(root with { Status = AgentStatus.Completed });

    _ = await _store.SaveAsync(AgentRecord.Spawned(childId, rootId, depth: 1,
        modelUsed: "mock/model", label: "worker", taskPrompt: "do work", createdAt: At(1)));

    _ = await _store.SaveAsync(new AgentRecord(orphanId, AgentId.NewId(), 1,
        AgentStatus.Failed, AgentFailureReason.Timeout, "mock/model", "orphan",
        "lost lineage", At(2), At(3), null));

    _ = await _store.AppendMessageAsync(rootId,
        new Message(Role.User, "root turn one", At(4)));
    _ = await _store.AppendMessageAsync(rootId,
        new Message(Role.Assistant, "root turn two", At(5)));
    _ = await _store.AppendMessageAsync(childId,
        new Message(Role.User, "child turn", At(6)));

    return (rootId, childId, orphanId);
  }

  private static string Rendered(Result<IReadOnlyList<SessionSummary>> result)
      => $"Error [{result.Error!.Code}]: {result.Error.Message}";

  // ---- Validation ----

  [Theory]
  [InlineData(0)]
  [InlineData(-7)]
  public async Task Execute_LimitBelowOne_FailsWithExactString(int limit)
  {
    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute(null, "active", limit);

    Assert.Equal("Error [InvalidArgument]: limit must be between 1 and 500.", Rendered(result));
  }

  [Fact]
  public async Task Execute_LimitAbove500_FailsWithExactString()
  {
    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute(null, "active", 501);

    Assert.Equal("Error [InvalidArgument]: limit must be between 1 and 500.", Rendered(result));
  }

  [Fact]
  public async Task Execute_UnknownScope_SurfacesParseFailureUntouched()
  {
    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute("project:x", "active", 50);

    Assert.Equal(
        "Error [InvalidScope]: Unknown scope 'project:x'. Valid scopes: global | session:<agentId>.",
        Rendered(result));
  }

  [Fact]
  public async Task Execute_UnknownBranches_FailsWithExactString()
  {
    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute(null, "everywhere", 50);

    Assert.Equal("Error [InvalidArgument]: branches must be 'active' or 'all'.", Rendered(result));
  }

  [Fact]
  public async Task Execute_ValidationOrder_LimitBeforeScopeBeforeBranches()
  {
    Result<IReadOnlyList<SessionSummary>> limitFirst = await _handler.Execute("bogus", "everywhere", 0);
    Assert.Equal("Error [InvalidArgument]: limit must be between 1 and 500.", Rendered(limitFirst));

    Result<IReadOnlyList<SessionSummary>> scopeSecond = await _handler.Execute("bogus", "everywhere", 50);
    Assert.StartsWith("Error [InvalidScope]:", Rendered(scopeSecond));

    Result<IReadOnlyList<SessionSummary>> branchesThird = await _handler.Execute(null, "everywhere", 50);
    Assert.Equal("Error [InvalidArgument]: branches must be 'active' or 'all'.", Rendered(branchesThird));
  }

  // ---- Listing shape and order ----

  [Fact]
  public async Task Execute_ListsEverySession_NewestFirst_WithCountsStatusAndHotTier()
  {
    (AgentId rootId, AgentId childId, AgentId orphanId) = await SeedSessionsAsync();

    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute(null, "active", 500);

    Assert.True(result.IsSuccess);
    IReadOnlyList<SessionSummary> summaries = result.Value!;
    Assert.Equal([orphanId, childId, rootId], summaries.Select(s => s.Id).ToList());

    SessionSummary root = summaries[2];
    Assert.Equal("root", root.Label);
    Assert.Equal(0, root.Depth);
    Assert.Equal(2, root.EntryCount);          // two appended turns
    Assert.Equal(nameof(AgentStatus.Completed), root.Status);
    Assert.Equal("hot", root.Tier);

    SessionSummary child = summaries[1];
    Assert.Equal("worker", child.Label);
    Assert.Equal(1, child.Depth);
    Assert.Equal(1, child.EntryCount);
    Assert.Equal(nameof(AgentStatus.Running), child.Status);
    Assert.Equal("hot", child.Tier);

    SessionSummary orphan = summaries[0];
    Assert.Equal("orphan", orphan.Label);
    Assert.Equal(0, orphan.EntryCount);        // no transcript rows
    Assert.Equal(nameof(AgentStatus.Failed), orphan.Status);
    Assert.Equal("hot", orphan.Tier);
  }

  [Fact]
  public async Task Execute_LimitTruncates_NewestFirst()
  {
    (AgentId _, AgentId _, AgentId _) = await SeedSessionsAsync();

    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute(null, "all", 2);

    Assert.True(result.IsSuccess);
    IReadOnlyList<SessionSummary> summaries = result.Value!;
    Assert.Equal(2, summaries.Count);
    // The two newest sessions are the failed orphan and the running child.
    Assert.Equal("orphan", summaries[0].Label);
    Assert.Equal("worker", summaries[1].Label);
  }

  [Fact]
  public async Task Execute_EmptyStore_YieldsEmptySuccess()
  {
    Result<IReadOnlyList<SessionSummary>> result = await _handler.Execute(null, "active", 50);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value!);
  }
}
