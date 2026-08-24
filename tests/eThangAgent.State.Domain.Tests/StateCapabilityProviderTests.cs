using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateCapabilityProviderTests
{
    private static StateCapabilityProvider Create(FakeStateService? service = null)
        => new(service ?? new FakeStateService());

    [Fact]
    public void Provider_ExposesNineActions_UnderStateId()
    {
        var provider = Create();

        Assert.Equal("state", provider.Id);
        Assert.Equal(9, provider.Actions.Count);
        Assert.Contains(provider.Actions, a => a.Name == "search" && a.Summary.Contains("Full-text"));
        Assert.Contains(provider.Actions, a => a.Name == "transition" && a.Summary.Contains("evidence"));
        Assert.Contains(provider.Actions, a => a.Name == "verify" && a.Description.Contains("fail-closed"));
    }

    [Fact]
    public async Task Get_Delegates_AndReturnsContent()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Success("hello");

        var result = await Create(service).InvokeAsync("get", """{"key":"current/head"}""");

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
        Assert.Equal("current/head", service.LastKey);
    }

    [Fact]
    public async Task Set_PassesExpectedVersion_AndFormatsContent()
    {
        var service = new FakeStateService();
        service.SetResult = Result<StateKeyValue>.Success(new StateKeyValue("current", "head", "x", 3));

        var result = await Create(service).InvokeAsync("set",
            """{"key":"current/head","value":"x","expectedVersion":2}""");

        Assert.False(result.IsError);
        Assert.Contains("current/head v3", result.Content);
        Assert.Equal(2, service.LastExpectedVersion);
    }

    [Fact]
    public async Task Set_ReservedTodoNamespace_Rejected_AtBoundary()
    {
        var service = new FakeStateService();

        var result = await Create(service).InvokeAsync("set",
            """{"key":"todo/list","value":"[]"}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [ReservedNamespace]:", result.Content);
        Assert.Contains("'todo'", result.Content);
        Assert.Null(service.LastKey); // never reached the service
    }

    [Fact]
    public async Task Delete_ReservedTodoNamespace_Rejected_AtBoundary()
    {
        var service = new FakeStateService();

        var result = await Create(service).InvokeAsync("delete",
            """{"key":"todo/list","expectedVersion":1}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [ReservedNamespace]:", result.Content);
        Assert.Contains("'todo'", result.Content);
        Assert.Null(service.LastKey); // never reached the service
    }

    [Fact]
    public async Task ReservedTodoNamespace_DoesNotAffectOtherNamespaces()
    {
        var service = new FakeStateService();

        var set = await Create(service).InvokeAsync("set", """{"key":"notes/scratch","value":"{}"}""");
        var delete = await Create(service).InvokeAsync("delete", """{"key":"notes/scratch"}""");

        Assert.False(set.IsError);
        Assert.False(delete.IsError);
        Assert.Equal("notes/scratch", service.LastKey);
    }

    [Fact]
    public async Task Transition_ParsesEvidenceArray_AndReturnsId()
    {
        var service = new FakeStateService();
        service.TransitionResult = Result<string>.Success("tr-abc");

        var result = await Create(service).InvokeAsync("transition",
            """{"from":"coding","to":"done","summary":"work","evidence":["Write-Output ok"]}""");

        Assert.False(result.IsError);
        Assert.Equal("tr-abc", result.Content);
        Assert.NotNull(service.LastEvidence);
        Assert.Equal("Write-Output ok", service.LastEvidence![0]);
    }

    [Fact]
    public async Task Verify_ReturnsSerializedReport()
    {
        var service = new FakeStateService();
        service.VerifyResult = new CertificationReport(true, false,
            [new EvidenceResult("Write-Output ok", true, "")], []);

        var result = await Create(service).InvokeAsync("verify", "{}");

        Assert.False(result.IsError);
        Assert.Contains("\"Certified\":true", result.Content);
    }

    [Fact]
    public async Task ServiceError_SurfacesAsGutter()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Failure(new Error("KeyNotFound", "current/head does not exist."));

        var result = await Create(service).InvokeAsync("get", """{"key":"current/head"}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [KeyNotFound]:", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await Create().InvokeAsync("get", """{"key":"a","extra":1}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidActionInput]:", result.Content);
        Assert.Contains("Unknown parameter", result.Content);
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var result = await Create().InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Contains("Error [UnknownAction]:", result.Content);
    }

    [Fact]
    public async Task Search_FormatsHits_UnderContractHeader()
    {
        var service = new FakeStateService();
        service.SearchResult = Result<IReadOnlyList<StateSearchHit>>.Success(
        [
            new StateSearchHit("plans", "alpha", "rewrite the [ledger] flow"),
            new StateSearchHit("specs", "beta", "second [hit]"),
        ]);

        var result = await Create(service).InvokeAsync("search", "{\"query\":\"ledger\",\"limit\":5}");

        Assert.False(result.IsError);
        Assert.Equal(
            "[state.search 'ledger'] 2 hit(s)\nplans/alpha\n  rewrite the [ledger] flow\nspecs/beta\n  second [hit]",
            result.Content);
        Assert.Equal(5, service.LastSearchLimit);
    }

    [Fact]
    public async Task Search_ZeroHits_PrintsHeaderOnly()
    {
        var result = await Create().InvokeAsync("search", "{\"query\":\"nothing\"}");
        Assert.False(result.IsError);
        Assert.Equal("[state.search 'nothing'] 0 hit(s)", result.Content);
    }

    [Fact]
    public async Task Search_DefaultLimit_Is20()
    {
        var service = new FakeStateService();
        await Create(service).InvokeAsync("search", "{\"query\":\"x\"}");
        Assert.Equal(20, service.LastSearchLimit);
    }

    [Fact]
    public async Task Search_ServiceError_SurfacesAsGutter()
    {
        var service = new FakeStateService();
        service.SearchResult = Result<IReadOnlyList<StateSearchHit>>.Failure(new Error("InvalidQuery", "bad fts syntax"));

        var result = await Create(service).InvokeAsync("search", "{\"query\":\"AND (\"}");

        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidQuery]:", result.Content);
    }

    [Fact]
    public async Task Get_WithRange_ReturnsEnvelopeAndRequestedLines()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Success("one\ntwo\nthree");
        service.ListResult = Result<IReadOnlyList<string>>.Success(["current/head v7"]);

        var result = await Create(service).InvokeAsync("get", "{\"key\":\"current/head\",\"startLine\":2,\"endLine\":3}");

        Assert.False(result.IsError);
        Assert.Equal("[current/head v7 | lines 2-3 of 3]\ntwo\nthree", result.Content);
    }

    [Fact]
    public async Task Get_RangeBeyondLastLine_ClampsWithVisibleWarning()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Success("a\nb");
        service.ListResult = Result<IReadOnlyList<string>>.Success(["current/head v2"]);

        var result = await Create(service).InvokeAsync("get", "{\"key\":\"current/head\",\"startLine\":1,\"endLine\":10}");

        Assert.False(result.IsError);
        Assert.Equal("[current/head v2 | lines 1-2 of 2]\na\nb\n[note] endLine 10 exceeds last line 2; clamped.", result.Content);
    }

    [Fact]
    public async Task Get_RangeWhenVersionUnresolvable_OmitsVersionSegment()
    {
        var service = new FakeStateService();
        service.GetResult = Result<string>.Success("solo");
        service.ListResult = Result<IReadOnlyList<string>>.Success([]);

        var result = await Create(service).InvokeAsync("get", "{\"key\":\"current/head\",\"startLine\":1,\"endLine\":1}");

        Assert.False(result.IsError);
        Assert.Equal("[current/head | lines 1-1 of 1]\nsolo", result.Content);
    }

    [Fact]
    public async Task Get_OnlyOneRangeParameter_Rejected()
    {
        var r1 = await Create().InvokeAsync("get", "{\"key\":\"current/head\",\"startLine\":1}");
        var r2 = await Create().InvokeAsync("get", "{\"key\":\"current/head\",\"endLine\":5}");
        Assert.True(r1.IsError);
        Assert.Contains("InvalidActionInput", r1.Content);
        Assert.True(r2.IsError);
        Assert.Contains("InvalidActionInput", r2.Content);
    }

    [Fact]
    public async Task Get_StartLineBelowOne_Rejected()
    {
        var r = await Create().InvokeAsync("get", "{\"key\":\"current/head\",\"startLine\":0,\"endLine\":2}");
        Assert.True(r.IsError);
        Assert.Contains("InvalidActionInput", r.Content);
    }

    [Fact]
    public async Task Get_EndLineBeforeStartLine_Rejected()
    {
        var r = await Create().InvokeAsync("get", "{\"key\":\"current/head\",\"startLine\":5,\"endLine\":4}");
        Assert.True(r.IsError);
        Assert.Contains("InvalidActionInput", r.Content);
    }
    private sealed class FakeStateService : IStateService
    {
        public Result<string> GetResult { get; set; } = Result<string>.Success("v1");
        public Result<StateKeyValue> SetResult { get; set; } =
            Result<StateKeyValue>.Success(new StateKeyValue("current", "head", "x", 2));
        public Result<string> DeleteResult { get; set; } = Result<string>.Success("deleted");
        public Result<IReadOnlyList<string>> ListResult { get; set; } =
            Result<IReadOnlyList<string>>.Success(["current/head v2"]);
        public Result<string> TransitionResult { get; set; } = Result<string>.Success("tr-1");
        public CertificationReport VerifyResult { get; set; } =
            new(true, false, [], []);
        public CertificationReport GoalResult { get; set; } =
            new(true, false, [], []);
        public Result<IReadOnlyList<string>> HistoryResult { get; set; } =
            Result<IReadOnlyList<string>>.Success([]);

        public string? LastKey { get; private set; }
        public int? LastExpectedVersion { get; private set; }
        public IReadOnlyList<string>? LastEvidence { get; private set; }

        public Task<Result<string>> GetAsync(string key, CancellationToken ct = default)
        { LastKey = key; return Task.FromResult(GetResult); }

        public Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, CancellationToken ct = default)
        { LastKey = key; LastExpectedVersion = expectedVersion; return Task.FromResult(SetResult); }

        public Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
        { LastKey = key; LastExpectedVersion = expectedVersion; return Task.FromResult(DeleteResult); }

        public Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
            => Task.FromResult(ListResult);

        public Result<IReadOnlyList<StateSearchHit>> SearchResult { get; set; } =
            Result<IReadOnlyList<StateSearchHit>>.Success([]);
        public string? LastSearchQuery { get; private set; }
        public int LastSearchLimit { get; private set; }

        public Task<Result<IReadOnlyList<StateSearchHit>>> SearchAsync(string query, int limit, CancellationToken ct = default)
        { LastSearchQuery = query; LastSearchLimit = limit; return Task.FromResult(SearchResult); }

        public Task<Result<string>> TransitionAsync(string from, string to, string summary,
            IReadOnlyList<string> evidence, CancellationToken ct = default)
        { LastEvidence = evidence; return Task.FromResult(TransitionResult); }

        public Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
            => Task.FromResult(VerifyResult);

        public Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
            => Task.FromResult(GoalResult);

        public Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
            => Task.FromResult(HistoryResult);
    }
}
