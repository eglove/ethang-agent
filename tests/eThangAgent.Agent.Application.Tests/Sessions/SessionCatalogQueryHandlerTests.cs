using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests.Sessions;

/// <summary>Sessions catalog over the in-memory fake store: bound-only, newest-first
///     listing, scoped to one workspace. The catalog is deliberately transcript-free — a
///     listing must never load conversation content.</summary>
public class SessionCatalogQueryHandlerTests
{
  private const string Alpha = @"C:\work\alpha";
  private const string Beta = @"C:\work\beta";
  private const string Gamma = @"C:\work\gamma";

  private static readonly DateTimeOffset Base =
      new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static DateTimeOffset At(double minutes) => Base.AddMinutes(minutes);

  private readonly FakeAgentStore _store = new();
  private readonly SessionCatalogQueryHandler _handler;

  public SessionCatalogQueryHandlerTests()
      => _handler = new SessionCatalogQueryHandler(_store);

  /// <summary>Three alpha sessions and two beta ones, alpha's newest last so the ordering
  ///     assertion has something to reverse.</summary>
  private async Task SeedTwoWorkspacesAsync()
  {
    foreach (AgentRecord record in new[]
    {
      AgentRecord.Root(AgentId.NewId(), At(0), Alpha, "openrouter"),
      AgentRecord.Root(AgentId.NewId(), At(1), Alpha, "openrouter"),
      AgentRecord.Root(AgentId.NewId(), At(2), Alpha, "openrouter"),
      AgentRecord.Root(AgentId.NewId(), At(3), Beta, "openrouter"),
      // Beta's newest row is the newest row in the whole store: it must still never appear
      // in alpha's listing.
      AgentRecord.Root(AgentId.NewId(), At(9), Beta, "openrouter"),
    })
    {
      _ = await _store.SaveAsync(record, ct: TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
  }

  [Fact]
  public async Task List_Returns_Only_The_Stated_Workspace_Newest_First()
  {
    await SeedTwoWorkspacesAsync();

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(Alpha,
        TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    Assert.Equal(3, listed.Value.Count);
    Assert.All(listed.Value, entry => Assert.Equal(Alpha, entry.WorkspaceId));
    // Newest first, and the store hands rows back in creation order.
    Assert.Equal([At(2), At(1), At(0)], listed.Value.Select(entry => entry.CreatedAt).ToList());
  }

  [Fact]
  public async Task List_Another_Workspace_Newest_Row_Never_Appears()
  {
    await SeedTwoWorkspacesAsync();

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(Alpha,
        TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    // At(9) is beta's and the store's newest row; At(3) is beta's other one.
    Assert.DoesNotContain(listed.Value, entry => entry.CreatedAt == At(9) || entry.CreatedAt == At(3));
  }

  [Fact]
  public async Task List_Workspace_With_No_Sessions_Succeeds_Empty()
  {
    await SeedTwoWorkspacesAsync();

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(Gamma,
        TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    Assert.Empty(listed.Value);
  }

  [Fact]
  public async Task List_Blank_Workspace_Refuses_With_InvalidWorkspace()
  {
    await SeedTwoWorkspacesAsync();

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync("  ",
        TestContext.Current.CancellationToken);

    Assert.False(listed.IsSuccess);
    Assert.Equal("InvalidWorkspace", listed.Error.Code);
  }

  [Fact]
  public async Task List_With_No_Workspace_At_All_Refuses_Rather_Than_Listing_Everything()
  {
    await SeedTwoWorkspacesAsync();

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(
        ct: TestContext.Current.CancellationToken);

    Assert.False(listed.IsSuccess);
    Assert.Equal("InvalidWorkspace", listed.Error.Code);
  }

  [Fact]
  public async Task List_Normalizes_The_Stated_Workspace_Before_Comparing()
  {
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), Alpha, "openrouter"),
        ct: TestContext.Current.CancellationToken);

    // A trailing separator and surrounding space are one spelling of one directory, and
    // Windows reports one directory in either case.
    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(@"  c:\work\alpha\  ",
        TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    _ = Assert.Single(listed.Value);
  }

  [Fact]
  public async Task ForWorkspace_Binds_The_Scope_So_A_Repeated_Listing_States_It_Once()
  {
    await SeedTwoWorkspacesAsync();
    SessionCatalogQueryHandler alpha = _handler.ForWorkspace(Alpha);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await alpha.ListAsync(
        ct: TestContext.Current.CancellationToken);

    Assert.Equal(Alpha, alpha.BoundWorkspaceId);
    Assert.True(listed.IsSuccess);
    Assert.Equal(3, listed.Value.Count);
    Assert.All(listed.Value, entry => Assert.Equal(Alpha, entry.WorkspaceId));
  }

  [Fact]
  public async Task ForWorkspace_Carries_The_Provider_Gate_Over()
  {
    SessionCatalogQueryHandler gated = new SessionCatalogQueryHandler(
        _store, provider => provider == "openrouter").ForWorkspace(Alpha);
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), Alpha, "openrouter"),
        ct: TestContext.Current.CancellationToken);
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(1), Alpha, "zai"),
        ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await gated.ListAsync(
        ct: TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    Assert.Equal("openrouter", Assert.Single(listed.Value).Provider);
  }

  [Fact]
  public async Task List_Skips_Unbound_Rows()
  {
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), Alpha, "openrouter"),
        ct: TestContext.Current.CancellationToken);
    // A root persisted before the workspace-binding migration carries no binding.
    _ = await _store.SaveAsync(new AgentRecord(AgentId.NewId(), AgentStatus.Completed,
        null, "unassigned", "root", "conversation root", At(2), At(3), null),
        ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(Alpha,
        TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    _ = Assert.Single(listed.Value);
    Assert.Equal(Alpha, listed.Value[0].WorkspaceId);
  }

  [Fact]
  public async Task List_Omits_Rows_Whose_Provider_Is_No_Longer_Resumable()
  {
    // A session persisted under a provider this build no longer knows (the retired z.ai)
    // cannot be resumed, so the catalog must not offer it. The gate is injected at the
    // composition root; here it stands in for Providers.IsKnown.
    SessionCatalogQueryHandler handler = new(_store, provider => provider == "openrouter");
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(0), Alpha, "openrouter"),
        ct: TestContext.Current.CancellationToken);
    _ = await _store.SaveAsync(AgentRecord.Root(AgentId.NewId(), At(1), Alpha, "zai"),
        ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await handler.ListAsync(Alpha,
        TestContext.Current.CancellationToken);

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
    AgentRecord stepSession = AgentRecord.Root(AgentId.NewId(), At(0), Alpha, "openrouter") with
    {
      Status = AgentStatus.Completed,
      CompletedAt = At(30),
    };
    _ = await _store.SaveAsync(stepSession, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(Alpha,
        TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    SessionCatalogEntry entry = Assert.Single(listed.Value);
    Assert.Equal(stepSession.Id, entry.Id);
    Assert.Equal(AgentStatus.Completed, entry.Status);
  }

  [Fact]
  public async Task List_Store_Failure_Surfaces_Untouched()
  {
    _store.ListAllFailure = new DomainError("DbDown", "nope");

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(Alpha,
        TestContext.Current.CancellationToken);

    Assert.False(listed.IsSuccess);
    Assert.Equal("DbDown", listed.Error.Code);
  }
}
