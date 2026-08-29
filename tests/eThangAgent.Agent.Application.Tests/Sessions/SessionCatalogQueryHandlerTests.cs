using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests.Sessions;

/// <summary>Sessions catalog over the in-memory fake store: root-only, bound-only,
///     newest-first listing. The catalog is deliberately transcript-free — a listing
///     must never load conversation content.</summary>
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
    _ = await _store.SaveAsync(older);
    _ = await _store.SaveAsync(newer);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync();

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
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), "C:/ws/a", "openrouter"));
    _ = await _store.SaveAsync(AgentRecord.Spawned(AgentId.NewId(), AgentId.NewId(), depth: 1,
        modelUsed: "m", label: "child", taskPrompt: "task", createdAt: At(1)));
    // A root persisted before the workspace-binding migration carries no binding.
    _ = await _store.SaveAsync(new AgentRecord(AgentId.NewId(), null, 0, AgentStatus.Completed,
        null, "unassigned", "root", "conversation root", At(2), At(3), null));

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync();

    Assert.True(listed.IsSuccess);
    _ = Assert.Single(listed.Value);
    Assert.Equal("C:/ws/a", listed.Value[0].WorkspaceId);
  }

  [Fact]
  public async Task List_Store_Failure_Surfaces_Untouched()
  {
    _store.ListAllFailure = new DomainError("DbDown", "nope");

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync();

    Assert.False(listed.IsSuccess);
    Assert.Equal("DbDown", listed.Error.Code);
  }
}
