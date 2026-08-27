using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateCapabilityProviderTests
{
  private static StateCapabilityProvider Create(FakeStateService? service = null)
      => new(service ?? new FakeStateService());

  [Fact]
  public void Provider_ExposesElevenActions_UnderStateId()
  {
    StateCapabilityProvider provider = Create();

    Assert.Equal("state", provider.Id);
    Assert.Equal(11, provider.Actions.Count);
    Assert.Contains(provider.Actions, a => a.Name == "find" && a.Summary.Contains("Full-text", StringComparison.Ordinal));
    Assert.Contains(provider.Actions, a => a.Name == "transition" && a.Summary.Contains("evidence", StringComparison.Ordinal));
    Assert.Contains(provider.Actions, a => a.Name == "verify" && a.Description.Contains("fail-closed", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Get_Delegates_AndReturnsContent()
  {
    FakeStateService service = new()
    {
      GetResult = Result.Success("hello")
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("get", /*lang=json,strict*/ """{"key":"current/head"}""");

    Assert.False(result.IsError);
    Assert.Equal("hello", result.Content);
    Assert.Equal("current/head", service.LastKey);
  }

  [Fact]
  public async Task Set_PassesExpectedVersion_AndFormatsContent()
  {
    FakeStateService service = new()
    {
      SetResult = Result.Success(new StateKeyValue("current", "head", "x", 3))
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("set",
                                 /*lang=json,strict*/
                                 """{"key":"current/head","value":"x","expectedVersion":2}""");

    Assert.False(result.IsError);
    Assert.Contains("current/head v3", result.Content, StringComparison.Ordinal);
    Assert.Equal(2, service.LastExpectedVersion);
  }

  [Fact]
  public async Task Set_ReservedTodoNamespace_Rejected_AtBoundary()
  {
    FakeStateService service = new();

    CapabilityInvocationResult result = await Create(service).InvokeAsync("set",
                                 /*lang=json,strict*/
                                 """{"key":"todo/list","value":"[]"}""");

    Assert.True(result.IsError);
    Assert.Contains("Error [ReservedNamespace]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("'todo'", result.Content, StringComparison.Ordinal);
    Assert.Null(service.LastKey); // never reached the service
  }

  [Fact]
  public async Task Delete_ReservedTodoNamespace_Rejected_AtBoundary()
  {
    FakeStateService service = new();

    CapabilityInvocationResult result = await Create(service).InvokeAsync("delete",
                                 /*lang=json,strict*/
                                 """{"key":"todo/list","expectedVersion":1}""");

    Assert.True(result.IsError);
    Assert.Contains("Error [ReservedNamespace]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("'todo'", result.Content, StringComparison.Ordinal);
    Assert.Null(service.LastKey); // never reached the service
  }

  [Fact]
  public async Task ReservedTodoNamespace_DoesNotAffectOtherNamespaces()
  {
    FakeStateService service = new();

    CapabilityInvocationResult set = await Create(service).InvokeAsync("set", /*lang=json,strict*/ """{"key":"notes/scratch","value":"{}"}""");
    CapabilityInvocationResult delete = await Create(service).InvokeAsync("delete", /*lang=json,strict*/ """{"key":"notes/scratch"}""");

    Assert.False(set.IsError);
    Assert.False(delete.IsError);
    Assert.Equal("notes/scratch", service.LastKey);
  }

  [Fact]
  public async Task Transition_ParsesEvidenceArray_AndReturnsId()
  {
    FakeStateService service = new()
    {
      TransitionResult = Result.Success("tr-abc")
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("transition",
                                 /*lang=json,strict*/
                                 """{"from":"coding","to":"done","summary":"work","evidence":["Write-Output ok"]}""");

    Assert.False(result.IsError);
    Assert.Equal("tr-abc", result.Content);
    Assert.NotNull(service.LastEvidence);
    Assert.Equal("Write-Output ok", service.LastEvidence![0]);
  }

  [Fact]
  public async Task Verify_ReturnsSerializedReport()
  {
    FakeStateService service = new()
    {
      VerifyResult = new CertificationReport(true, false,
          [new EvidenceResult("Write-Output ok", true, "")], [])
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("verify", "{}");

    Assert.False(result.IsError);
    Assert.Contains("\"Certified\":true", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ServiceError_SurfacesAsGutter()
  {
    FakeStateService service = new()
    {
      GetResult = Result.Failure<string>(new DomainError("KeyNotFound", "current/head does not exist."))
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("get", /*lang=json,strict*/ """{"key":"current/head"}""");

    Assert.True(result.IsError);
    Assert.Contains("Error [KeyNotFound]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    CapabilityInvocationResult result = await Create().InvokeAsync("get", /*lang=json,strict*/ """{"key":"a","extra":1}""");

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownAction_ReturnsError()
  {
    CapabilityInvocationResult result = await Create().InvokeAsync("nope", "{}");

    Assert.True(result.IsError);
    Assert.Contains("Error [UnknownAction]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Search_FormatsHits_UnderContractHeader()
  {
    FakeStateService service = new()
    {
      SearchResult = Result.Success<IReadOnlyList<StateSearchHit>>(
      [
          new StateSearchHit("plans", "alpha", "rewrite the [ledger] flow"),
              new StateSearchHit("specs", "beta", "second [hit]"),
          ])
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("find", /*lang=json,strict*/ "{\"query\":\"ledger\",\"limit\":5}");

    Assert.False(result.IsError);
    Assert.Equal(
        "[state.find 'ledger'] 2 hit(s)\nplans/alpha\n  rewrite the [ledger] flow\nspecs/beta\n  second [hit]",
        result.Content);
    Assert.Equal(5, service.LastSearchLimit);
  }

  [Fact]
  public async Task Search_ZeroHits_PrintsHeaderOnly()
  {
    CapabilityInvocationResult result = await Create().InvokeAsync("find", /*lang=json,strict*/ "{\"query\":\"nothing\"}");
    Assert.False(result.IsError);
    Assert.Equal("[state.find 'nothing'] 0 hit(s)", result.Content);
  }

  [Fact]
  public async Task Search_DefaultLimit_Is20()
  {
    FakeStateService service = new();
    _ = await Create(service).InvokeAsync("find", /*lang=json,strict*/ "{\"query\":\"x\"}");
    Assert.Equal(20, service.LastSearchLimit);
  }

  [Fact]
  public async Task Search_ServiceError_SurfacesAsGutter()
  {
    FakeStateService service = new()
    {
      SearchResult = Result.Failure<IReadOnlyList<StateSearchHit>>(new DomainError("InvalidQuery", "bad fts syntax"))
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("find", /*lang=json,strict*/ "{\"query\":\"AND (\"}");

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidQuery]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Get_WithRange_ReturnsEnvelopeAndRequestedLines()
  {
    FakeStateService service = new()
    {
      GetResult = Result.Success("one\ntwo\nthree"),
      ListResult = Result.Success<IReadOnlyList<string>>(["current/head v7"])
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"startLine\":2,\"endLine\":3}");

    Assert.False(result.IsError);
    Assert.Equal("[current/head v7 | lines 2-3 of 3]\ntwo\nthree", result.Content);
  }

  [Fact]
  public async Task Get_RangeBeyondLastLine_ClampsWithVisibleWarning()
  {
    FakeStateService service = new()
    {
      GetResult = Result.Success("a\nb"),
      ListResult = Result.Success<IReadOnlyList<string>>(["current/head v2"])
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"startLine\":1,\"endLine\":10}");

    Assert.False(result.IsError);
    Assert.Equal("[current/head v2 | lines 1-2 of 2]\na\nb\n[note] endLine 10 exceeds last line 2; clamped.", result.Content);
  }

  [Fact]
  public async Task Get_RangeWhenVersionUnresolvable_OmitsVersionSegment()
  {
    FakeStateService service = new()
    {
      GetResult = Result.Success("solo"),
      ListResult = Result.Success<IReadOnlyList<string>>([])
    };

    CapabilityInvocationResult result = await Create(service).InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"startLine\":1,\"endLine\":1}");

    Assert.False(result.IsError);
    Assert.Equal("[current/head | lines 1-1 of 1]\nsolo", result.Content);
  }

  [Fact]
  public async Task Get_OnlyOneRangeParameter_Rejected()
  {
    CapabilityInvocationResult r1 = await Create().InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"startLine\":1}");
    CapabilityInvocationResult r2 = await Create().InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"endLine\":5}");
    Assert.True(r1.IsError);
    Assert.Contains("InvalidActionInput", r1.Content, StringComparison.Ordinal);
    Assert.True(r2.IsError);
    Assert.Contains("InvalidActionInput", r2.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Get_StartLineBelowOne_Rejected()
  {
    CapabilityInvocationResult r = await Create().InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"startLine\":0,\"endLine\":2}");
    Assert.True(r.IsError);
    Assert.Contains("InvalidActionInput", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Get_EndLineBeforeStartLine_Rejected()
  {
    CapabilityInvocationResult r = await Create().InvokeAsync("get", /*lang=json,strict*/ "{\"key\":\"current/head\",\"startLine\":5,\"endLine\":4}");
    Assert.True(r.IsError);
    Assert.Contains("InvalidActionInput", r.Content, StringComparison.Ordinal);
  }
  private sealed class FakeStateService : IStateService
  {
    public Result<string> GetResult { get; set; } = Result.Success("v1");
    public Result<StateKeyValue> SetResult { get; set; } =
        Result.Success(new StateKeyValue("current", "head", "x", 2));
    public Result<string> DeleteResult { get; set; } = Result.Success("deleted");
    public Result<IReadOnlyList<string>> ListResult { get; set; } =
        Result.Success<IReadOnlyList<string>>(["current/head v2"]);
    public Result<string> TransitionResult { get; set; } = Result.Success("tr-1");
    public CertificationReport VerifyResult { get; set; } =
        new(true, false, [], []);
    public CertificationReport GoalResult { get; set; } =
        new(true, false, [], []);
    public Result<IReadOnlyList<string>> HistoryResult { get; set; } =
        Result.Success<IReadOnlyList<string>>([]);

    public string? LastKey { get; private set; }
    public int? LastExpectedVersion { get; private set; }
    public IReadOnlyList<string>? LastEvidence { get; private set; }

    public Task<Result<string>> GetAsync(string key, CancellationToken ct = default)
    {
      LastKey = key;
      return Task.FromResult(GetResult);
    }

    public Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, CancellationToken ct = default)
    {
      LastKey = key;
      LastExpectedVersion = expectedVersion;
      return Task.FromResult(SetResult);
    }

    public Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
    {
      LastKey = key;
      LastExpectedVersion = expectedVersion;
      return Task.FromResult(DeleteResult);
    }

    public Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
        => Task.FromResult(ListResult);

    public Result<IReadOnlyList<StateSearchHit>> SearchResult { get; set; } =
        Result.Success<IReadOnlyList<StateSearchHit>>([]);
    public string? LastSearchQuery { get; private set; }
    public int LastSearchLimit { get; private set; }

    public Task<Result<StateKeyValue>> AppendAsync(string key, string text, int? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new StateKeyValue("sdd.x", "ledger", text, 1)));

    public Task<Result<int>> DeletePrefixAsync(string nsPrefix, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
      LastSearchQuery = query;
      LastSearchLimit = limit;
      return Task.FromResult(SearchResult);
    }

    public Task<Result<string>> TransitionAsync(string from, string to, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default)
    {
      LastEvidence = evidence;
      return Task.FromResult(TransitionResult);
    }

    public Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
        => Task.FromResult(VerifyResult);

    public Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
        => Task.FromResult(GoalResult);

    public Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
        => Task.FromResult(HistoryResult);
  }
}
