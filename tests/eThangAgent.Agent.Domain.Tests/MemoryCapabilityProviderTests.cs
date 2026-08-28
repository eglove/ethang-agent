using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Coverage of the memory capability surface against fake queries: exact hit-line
///     and footer rendering with paging arithmetic, zero-hit footer-only output, newline
///     collapse and 120-char snippet truncation, session summary lines with the hot tier,
///     strict JSON argument parsing (unknown keys, wrong types, non-object roots), pass-
///     through of query failures untouched, and the standard unknown-action error.</summary>
public class MemoryCapabilityProviderTests
{
  private static readonly CancellationToken AnyToken = new CancellationTokenSource().Token;

  private sealed class FakeRecallQuery(Result<RecallPage>? reply = null) : IMemoryRecallQuery
  {
    public int _calls;
    public (string? Query, string QueryMode, string? Scope, string Branches, string? Role, int Page, int PageSize)? _lastArgs;
    public CancellationToken _lastCt;
    private readonly Result<RecallPage> _reply =
        reply ?? Result.Success(new RecallPage([], 0, 1, 1));

    public Task<Result<RecallPage>> Execute(RecallRequest request, CancellationToken ct = default)
    {
      _calls++;
      _lastArgs = (request.Query, request.QueryMode, request.Scope, request.Branches, request.Role, request.Page, request.PageSize);
      _lastCt = ct;
      return Task.FromResult(_reply);
    }
  }

  private sealed class FakeSessionsQuery(Result<IReadOnlyList<SessionSummary>>? reply = null) : IMemorySessionsQuery
  {
    public int _calls;
    public (string? Scope, string Branches, int Limit)? _lastArgs;
    public CancellationToken _lastCt;
    private readonly Result<IReadOnlyList<SessionSummary>> _reply =
        reply ?? Result.Success<IReadOnlyList<SessionSummary>>([]);

    public Task<Result<IReadOnlyList<SessionSummary>>> Execute(
        string? scope, string branches, int limit, CancellationToken ct = default)
    {
      _calls++;
      _lastArgs = (scope, branches, limit);
      _lastCt = ct;
      return Task.FromResult(_reply);
    }
  }

  private static (MemoryCapabilityProvider Provider, FakeRecallQuery Recall, FakeSessionsQuery Sessions)
      MakeProvider(Result<RecallPage>? recallReply = null,
          Result<IReadOnlyList<SessionSummary>>? sessionsReply = null)
  {
    FakeRecallQuery recall = new(recallReply);
    FakeSessionsQuery sessions = new(sessionsReply);
    return (new MemoryCapabilityProvider(recall, sessions), recall, sessions);
  }

  private static RecallPage PageOf(params RecallHit[] hits)
      => new(hits, TotalMatched: 12, Page: 3, Pages: 3);

  [Fact]
  public async Task Recall_WithHits_RendersHitLinesAndFooterArithmetic()
  {
    AgentId first = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    AgentId second = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    (MemoryCapabilityProvider? provider, FakeRecallQuery? recall, FakeSessionsQuery _) = MakeProvider(Result.Success(PageOf(
        new RecallHit(first, 2, "user", "hello world", DateTimeOffset.UtcNow),
        new RecallHit(second, 7, "assistant", "second line", DateTimeOffset.UtcNow))));

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", /*lang=json,strict*/ """{"pageSize":5,"page":3}""", AnyToken);

    Assert.False(result.IsError);
    Assert.Equal(
        $"[mem] session={first} seq=2 role=user hello world\n" +
        $"[mem] session={second} seq=7 role=assistant second line\n" +
        "--- memory: 12 hits, page 3/3 ---",
        result.Content);
    _ = Assert.NotNull(recall._lastArgs);
    Assert.Equal((null, "literal", "global", "active", null, 3, 5), recall._lastArgs);
    Assert.Equal(AnyToken, recall._lastCt);
  }

  [Fact]
  public async Task Recall_WithZeroHits_RendersFooterOnly()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", "{}");

    Assert.False(result.IsError);
    Assert.Equal("--- memory: 0 hits, page 1/1 ---", result.Content);
  }

  [Fact]
  public async Task Recall_Snippet_CollapsesNewlinesAndTruncatesAt120Chars()
  {
    // Collapsed content is 138 chars ("alpha bravo charlie " + 118 z) so the hit line
    // must carry exactly the first 120 chars of it.
    AgentId session = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    string content = "alpha\rbravo\ncharlie " + new string('z', 118);
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider(Result.Success(PageOf(new RecallHit(session, 4, "tool", content, DateTimeOffset.UtcNow))));

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", "{}");

    Assert.False(result.IsError);
    Assert.Equal(
        $"[mem] session={session} seq=4 role=tool alpha bravo charlie {new string('z', 100)}\n" +
        "--- memory: 12 hits, page 3/3 ---",
        result.Content);
  }

  [Fact]
  public async Task Sessions_RendersOneLinePerSummary_IncludingHotTier()
  {
    AgentId first = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    AgentId second = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery? sessions) = MakeProvider(sessionsReply: Result.Success<IReadOnlyList<SessionSummary>>(
    [
        new SessionSummary(first, "fix login loop", 1, 4, "Completed", "hot"),
            new SessionSummary(second, "", 0, 0, "Running", "hot"),
        ]));

    CapabilityInvocationResult result = await provider.InvokeAsync("sessions", "{}", AnyToken);

    Assert.False(result.IsError);
    Assert.Equal(
        $"session={first} label=fix login loop depth=1 entries=4 status=Completed tier=hot\n" +
        $"session={second} label= depth=0 entries=0 status=Running tier=hot",
        result.Content);
    Assert.Equal(("global", "active", 50), sessions._lastArgs);
    Assert.Equal(AnyToken, sessions._lastCt);
  }

  [Fact]
  public async Task Recall_ForwardsDocumentedDefaults_ForEmptyArguments()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery? recall, FakeSessionsQuery _) = MakeProvider();

    _ = await provider.InvokeAsync("recall", "{}");

    Assert.Equal(1, recall._calls);
    Assert.Equal((null, "literal", "global", "active", null, 1, 25), recall._lastArgs);
  }

  [Fact]
  public async Task Sessions_ForwardsDocumentedDefaults_ForEmptyArguments()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery? sessions) = MakeProvider();

    _ = await provider.InvokeAsync("sessions", "{}");

    Assert.Equal(1, sessions._calls);
    Assert.Equal(("global", "active", 50), sessions._lastArgs);
  }

  [Fact]
  public async Task Recall_UnknownArgument_IsRejectedWithoutCallingTheQuery()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery? recall, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", /*lang=json,strict*/ """{"bogus":1}""");

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidArgument]: unknown argument 'bogus'.", result.Content);
    Assert.Equal(0, recall._calls);
  }

  [Fact]
  public async Task Sessions_UnknownArgument_IsRejectedWithoutCallingTheQuery()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery? sessions) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("sessions", /*lang=json,strict*/ """{"since":"yesterday"}""");

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidArgument]: unknown argument 'since'.", result.Content);
    Assert.Equal(0, sessions._calls);
  }

  [Fact]
  public async Task Recall_PageSizeAsJsonString_IsRejected()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", /*lang=json,strict*/ """{"pageSize":"5"}""");

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidArgument]: argument 'pageSize' must be a number.", result.Content);
  }

  [Fact]
  public async Task Recall_QueryAsJsonNumber_IsRejected()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", /*lang=json,strict*/ """{"query":7}""");

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidArgument]: argument 'query' must be a string.", result.Content);
  }

  [Fact]
  public async Task Sessions_LimitAsJsonString_IsRejected()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("sessions", /*lang=json,strict*/ """{"limit":"50"}""");

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidArgument]: argument 'limit' must be a number.", result.Content);
  }

  [Fact]
  public async Task Recall_NonObjectArguments_AreRejected()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", "[1]");

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidArgument]: arguments must be a JSON object.", result.Content);
  }

  [Fact]
  public async Task Recall_MalformedScope_SurfacesQueryFailureUntouched()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider(recallReply: Result.Failure<RecallPage>(new DomainError(
        "InvalidScope",
        "Unknown scope 'session:nope'. Valid scopes: global | session:<agentId>.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("recall", /*lang=json,strict*/ """{"scope":"session:nope"}""");

    Assert.True(result.IsError);
    Assert.Equal(
        "Error [InvalidScope]: Unknown scope 'session:nope'. Valid scopes: global | session:<agentId>.",
        result.Content);
  }

  [Fact]
  public async Task Sessions_QueryFailure_SurfacesUntouched()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider(sessionsReply: Result.Failure<IReadOnlyList<SessionSummary>>(
        new DomainError("StorageUnavailable", "database locked.")));

    CapabilityInvocationResult result = await provider.InvokeAsync("sessions", "{}");

    Assert.True(result.IsError);
    Assert.Equal("Error [StorageUnavailable]: database locked.", result.Content);
  }

  [Fact]
  public async Task UnknownAction_YieldsStandardError()
  {
    (MemoryCapabilityProvider? provider, FakeRecallQuery _, FakeSessionsQuery _) = MakeProvider();

    CapabilityInvocationResult result = await provider.InvokeAsync("forget", "{}");

    Assert.True(result.IsError);
    Assert.Equal("Error [UnknownAction]: Unknown action: forget.", result.Content);
  }
}
