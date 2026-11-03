using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests.Sessions;

/// <summary>Sessions catalog over the in-memory fake store: bound-only, newest-first
///     listing. The catalog is deliberately transcript-free — a listing must never load
///     conversation content.</summary>
public class SessionCatalogQueryHandlerTests
{
  private static readonly DateTimeOffset Base =
      new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static DateTimeOffset At(double minutes) => Base.AddMinutes(minutes);

  private readonly FakeAgentStore _store = new();
  private readonly SessionCatalogQueryHandler _handler;

  public SessionCatalogQueryHandlerTests()
      => _handler = new SessionCatalogQueryHandler(_store);

  [Fact]
  public async Task List_Returns_Bound_Roots_Newest_First()
  {
    AgentRecord older = AgentRecord.Root(AgentId.NewId(), At(0), "C:/ws/a", "openrouter");
    AgentRecord newer = AgentRecord.Root(AgentId.NewId(), At(5), "C:/ws/b", "zai");
    // Insert oldest first; the listing must come back newest first.
    _ = await _store.SaveAsync(older, ct: TestContext.Current.CancellationToken);
    _ = await _store.SaveAsync(newer, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    Assert.Equal([newer.Id, older.Id], listed.Value.Select(e => e.Id).ToList());
    Assert.Equal("C:/ws/b", listed.Value[0].WorkspaceId);
    Assert.Equal("zai", listed.Value[0].Provider);
    Assert.Equal(AgentStatus.Running, listed.Value[0].Status);
    Assert.Null(listed.Value[0].CompletedAt);
  }

  [Fact]
  public async Task List_Skips_Children_And_Unbound_Rows()
  {
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), "C:/ws/a", "openrouter"), ct: TestContext.Current.CancellationToken);
    _ = await _store.SaveAsync(AgentRecord.Started(AgentId.NewId(),
        modelUsed: "m", label: "child", taskPrompt: "task", createdAt: At(1)), ct: TestContext.Current.CancellationToken);
    // A root persisted before the workspace-binding migration carries no binding.
    _ = await _store.SaveAsync(new AgentRecord(AgentId.NewId(), AgentStatus.Completed,
        null, "unassigned", "root", "conversation root", At(2), At(3), null), ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    _ = Assert.Single(listed.Value);
    Assert.Equal("C:/ws/a", listed.Value[0].WorkspaceId);
  }

  [Fact]
  public async Task List_Omits_Rows_Whose_Provider_Is_No_Longer_Resumable()
  {
    // A session persisted under a provider this build no longer knows (the retired z.ai)
    // cannot be resumed, so the catalog must not offer it. The gate is injected at the
    // composition root; here it stands in for Providers.IsKnown.
    SessionCatalogQueryHandler handler = new(_store, provider => provider == "openrouter");
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), "C:/ws/a", "openrouter"), ct: TestContext.Current.CancellationToken);
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(1), "C:/ws/b", "zai"), ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    SessionCatalogEntry only = Assert.Single(listed.Value);
    Assert.Equal("openrouter", only.Provider);
  }

  /// <summary>A chain step runs as its OWN SESSION: it is a bound record like any other
  ///     conversation, so the catalog must list it — a completed step that the catalog omitted
  ///     would be exactly the session a user cannot resume. The step's link to its chain lives
  ///     in <c>chain_steps.session_id</c>, not in the agent record, so nothing about the record
  ///     distinguishes it from a hand-started session.</summary>
  [Fact]
  public async Task List_Lists_AChainStepSession_LikeAnyOtherBoundSession()
  {
    AgentRecord stepSession = AgentRecord.Root(AgentId.NewId(), At(0), "C:/ws/a", "openrouter") with
    {
      Status = AgentStatus.Completed,
      CompletedAt = At(30),
    };
    _ = await _store.SaveAsync(stepSession, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    SessionCatalogEntry entry = Assert.Single(listed.Value);
    Assert.Equal(stepSession.Id, entry.Id);
    Assert.Equal(AgentStatus.Completed, entry.Status);
  }

  [Fact]
  public async Task List_Store_Failure_Surfaces_Untouched()
  {
    _store.ListAllFailure = new DomainError("DbDown", "nope");

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.False(listed.IsSuccess);
    Assert.Equal("DbDown", listed.Error.Code);
  }
}
