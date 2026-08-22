using eThangAgent.Agent.Application.Memory;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests.Memory;

/// <summary>Recall query handler over a seeded branched fake store: every validation
///     string verbatim, browse ordering, literal AND semantics, bounded regex modes,
///     active-vs-all branch resolution through an orphan fixture, and paging math.</summary>
public class RecallQueryHandlerTests
{
    private static readonly DateTimeOffset Base =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minutes) => Base.AddMinutes(minutes);

    private readonly FakeAgentStore _store = new();
    private readonly RecallQueryHandler _handler;

    public RecallQueryHandlerTests()
        => _handler = new RecallQueryHandler(_store);

    /// <summary>Seeds the orphan fixture: a root session with two turns and an ORPHAN child
    ///     whose parent row was never saved — the observable active-vs-all difference.</summary>
    private async Task<(AgentId RootId, AgentId OrphanId)> SeedBranchedCorpusAsync()
    {
        var rootId = AgentId.NewId();
        var orphanId = AgentId.NewId();
        var missingAncestor = AgentId.NewId();

        await _store.SaveAsync(AgentRecord.Root(rootId, At(0)));
        // Orphan child: references an absent parent row.
        await _store.SaveAsync(AgentRecord.Spawned(orphanId, missingAncestor, depth: 1,
            modelUsed: "mock/model", label: "orphan", taskPrompt: "lost lineage", createdAt: At(1)));

        await _store.AppendMessageAsync(rootId,
            new Message(Role.User, "alpha kickoff note", At(2)));
        await _store.AppendMessageAsync(rootId,
            new Message(Role.Assistant, "beta follow-up with alpha context", At(3)));
        await _store.AppendMessageAsync(orphanId,
            new Message(Role.User, "orphan alpha line", At(4)));

        return (rootId, orphanId);
    }

    private static string Rendered(Result<RecallPage> result)
        => $"Error [{result.Error!.Code}]: {result.Error.Message}";

    // ---- Validation: exact strings and exact order ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Execute_PageBelowOne_FailsWithExactString(int page)
    {
        var result = await _handler.Execute(null, "literal", null, "active", null, page, 25);

        Assert.False(result.IsSuccess);
        Assert.Equal("Error [InvalidArgument]: page must be at least 1.", Rendered(result));
    }

    [Fact]
    public async Task Execute_PageSizeZero_FailsWithExactString()
    {
        var result = await _handler.Execute(null, "literal", null, "active", null, 1, 0);

        Assert.Equal("Error [InvalidArgument]: pageSize must be between 1 and 200.", Rendered(result));
    }

    [Fact]
    public async Task Execute_PageSizeAbove200_FailsWithExactString()
    {
        var result = await _handler.Execute(null, "literal", null, "active", null, 1, 201);

        Assert.Equal("Error [InvalidArgument]: pageSize must be between 1 and 200.", Rendered(result));
    }

    [Fact]
    public async Task Execute_UnknownScope_SurfacesParseFailureUntouched()
    {
        var result = await _handler.Execute(null, "literal", "bogus", "active", null, 1, 25);

        Assert.Equal(
            "Error [InvalidScope]: Unknown scope 'bogus'. Valid scopes: global | session:<agentId>.",
            Rendered(result));
    }

    [Fact]
    public async Task Execute_MalformedSessionScope_SurfacesRawVerbatim()
    {
        var raw = "session:not-a-guid";

        var result = await _handler.Execute(null, "literal", raw, "active", null, 1, 25);

        Assert.Equal(
            $"Error [InvalidScope]: Unknown scope '{raw}'. Valid scopes: global | session:<agentId>.",
            Rendered(result));
    }

    [Fact]
    public async Task Execute_UnknownQueryMode_FailsWithExactString()
    {
        var result = await _handler.Execute("x", "fuzzy", null, "active", null, 1, 25);

        Assert.Equal("Error [InvalidArgument]: queryMode must be 'literal' or 'regex'.", Rendered(result));
    }

    [Fact]
    public async Task Execute_UnknownRole_FailsWithExactString()
    {
        var result = await _handler.Execute("x", "literal", null, "active", "system", 1, 25);

        Assert.Equal(
            "Error [InvalidArgument]: role must be 'user', 'assistant', or 'tool'.",
            Rendered(result));
    }

    [Fact]
    public async Task Execute_RoleIsCaseInsensitive_AcceptsAnyCaseSpelling()
    {
        await SeedBranchedCorpusAsync();

        foreach (var role in new[] { "USER", "Assistant", "TOOL" })
        {
            var result = await _handler.Execute(null, "literal", null, "all", role, 1, 25);
            Assert.True(result.IsSuccess, $"role '{role}' should be accepted");
        }
    }

    [Fact]
    public async Task Execute_EmptyRoleString_IsRejected_NotTreatedAsAbsent()
    {
        var result = await _handler.Execute("x", "literal", null, "active", "", 1, 25);

        Assert.Equal(
            "Error [InvalidArgument]: role must be 'user', 'assistant', or 'tool'.",
            Rendered(result));
    }

    [Fact]
    public async Task Execute_UnknownBranches_FailsWithExactString_BeforeAnyStoreRead()
    {
        var result = await _handler.Execute(null, "literal", null, "ACTIVE", null, 1, 25);

        Assert.Equal("Error [InvalidArgument]: branches must be 'active' or 'all'.", Rendered(result));
        Assert.Empty(_store.Saved); // nothing was read: branches is checked before corpus building
    }

    [Fact]
    public async Task Execute_ValidationOrder_PageBeforePageSizeBeforeScopeBeforeModeBeforeRole()
    {
        var pageFirst = await _handler.Execute("q", "fuzzy", "bogus", "active", "system", 0, 0);
        Assert.Equal("Error [InvalidArgument]: page must be at least 1.", Rendered(pageFirst));

        var pageSizeSecond = await _handler.Execute("q", "fuzzy", "bogus", "active", "system", 1, 500);
        Assert.Equal("Error [InvalidArgument]: pageSize must be between 1 and 200.", Rendered(pageSizeSecond));

        var scopeThird = await _handler.Execute("q", "fuzzy", "bogus", "active", "system", 1, 25);
        Assert.StartsWith("Error [InvalidScope]:", Rendered(scopeThird));

        var modeFourth = await _handler.Execute("q", "fuzzy", null, "active", "system", 1, 25);
        Assert.Equal("Error [InvalidArgument]: queryMode must be 'literal' or 'regex'.", Rendered(modeFourth));

        var roleFifth = await _handler.Execute("q", "literal", null, "active", "system", 1, 25);
        Assert.Equal(
            "Error [InvalidArgument]: role must be 'user', 'assistant', or 'tool'.",
            Rendered(roleFifth));
    }

    // ---- Browse ordering (newest-first) ----

    [Fact]
    public async Task Execute_Browse_ReturnsAllEntriesNewestFirst_AcrossSessions()
    {
        var (rootId, orphanId) = await SeedBranchedCorpusAsync();

        var result = await _handler.Execute(null, "literal", null, "all", null, 1, 25);

        Assert.True(result.IsSuccess);
        var page = result.Value!;
        Assert.Equal(3, page.TotalMatched);
        Assert.Equal(1, page.Pages);
        Assert.Equal(
        [
            (orphanId, 0, "User", "orphan alpha line"),
            (rootId, 1, "Assistant", "beta follow-up with alpha context"),
            (rootId, 0, "User", "alpha kickoff note"),
        ], page.Hits.Select(h => (h.Session, h.Seq, h.Role, h.Content)).ToList());
    }

    // ---- Literal mode: AND across tokens ----

    [Fact]
    public async Task Execute_LiteralTokens_MatchOnlyWhenEveryTokenIsPresent()
    {
        var (rootId, _) = await SeedBranchedCorpusAsync();

        var result = await _handler.Execute("alpha beta", "literal", null, "all", null, 1, 25);

        Assert.True(result.IsSuccess);
        var page = result.Value!;
        Assert.Equal(1, page.TotalMatched);
        var hit = Assert.Single(page.Hits);
        Assert.Equal(rootId, hit.Session);
        Assert.Equal(1, hit.Seq);
        Assert.Equal("Assistant", hit.Role);
        Assert.Equal("beta follow-up with alpha context", hit.Content);
    }

    [Fact]
    public async Task Execute_LiteralRegexMetacharacters_TreatedAsTerms_NeverCompiled()
    {
        var rootId = AgentId.NewId();
        await _store.SaveAsync(AgentRecord.Root(rootId, At(0)));
        await _store.AppendMessageAsync(rootId,
            new Message(Role.User, "the price is a.c literally", At(1)));
        await _store.AppendMessageAsync(rootId,
            new Message(Role.Assistant, "abc would match the regex but lacks the dot token", At(2)));

        // "a.c" as a regex would match "abc"; as literal terms it needs tokens {a, c}.
        var result = await _handler.Execute("a.c", "literal", null, "active", null, 1, 25);

        Assert.True(result.IsSuccess);
        var hit = Assert.Single(result.Value!.Hits);
        Assert.Equal("the price is a.c literally", hit.Content);
    }

    // ---- Regex mode reaches BoundedRegex ----

    [Fact]
    public async Task Execute_RegexMode_MatchesThroughBoundedRegex_CaseInsensitively()
    {
        var (rootId, orphanId) = await SeedBranchedCorpusAsync();

        var result = await _handler.Execute(@"^alpha \w+", "regex", null, "all", null, 1, 25);

        Assert.True(result.IsSuccess);
        var page = result.Value!;
        // Anchored pattern: only the root's user turn begins with "alpha";
        // "orphan alpha line" starts with "orphan".
        Assert.Equal(1, page.TotalMatched);
        Assert.Equal(["alpha kickoff note"],
            page.Hits.Select(h => h.Content).ToList());
    }

    [Fact]
    public async Task Execute_RegexMode_TimeoutSurfacesAsTypedFailResult()
    {
        var rootId = AgentId.NewId();
        await _store.SaveAsync(AgentRecord.Root(rootId, At(0)));
        await _store.AppendMessageAsync(rootId,
            new Message(Role.User, $"{new string('a', 5000)}b", At(1)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _handler.Execute("(a+)+$", "regex", null, "active", null, 1, 25);
        stopwatch.Stop();

        Assert.False(result.IsSuccess);
        Assert.Equal("Error [regex_timeout]: Regex exceeded the 250 ms budget.", Rendered(result));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"timeout should end promptly, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Execute_RegexMode_OversizedPattern_SurfacesTypedFailResult()
    {
        await SeedBranchedCorpusAsync();

        var result = await _handler.Execute(new string('a', 1100), "regex", null, "active", null, 1, 25);

        Assert.Equal("Error [regex_pattern_too_large]: Regex pattern exceeds 1024 bytes.", Rendered(result));
    }

    // ---- Branch resolution through the orphan fixture ----

    [Fact]
    public async Task Execute_ActivePath_ExcludesOrphanChain_AllBranches_IncludesIt()
    {
        await SeedBranchedCorpusAsync();

        var active = await _handler.Execute(null, "literal", null, "active", null, 1, 25);
        var all = await _handler.Execute(null, "literal", null, "all", null, 1, 25);

        Assert.True(active.IsSuccess);
        Assert.True(all.IsSuccess);
        Assert.Equal(2, active.Value!.TotalMatched);   // root's two turns only
        Assert.Equal(3, all.Value!.TotalMatched);      // plus the orphan's turn
        Assert.Contains(all.Value.Hits, h => h.Content == "orphan alpha line");
        Assert.All(active.Value.Hits, h => Assert.NotEqual("orphan alpha line", h.Content));
    }

    // ---- Session scope ----

    [Fact]
    public async Task Execute_SessionScope_ReturnsOnlyThatSession_WithIndexedSeqs()
    {
        var (rootId, _) = await SeedBranchedCorpusAsync();

        var result = await _handler.Execute(null, "literal", $"session:{rootId}", "all", null, 1, 25);

        Assert.True(result.IsSuccess);
        var page = result.Value!;
        Assert.Equal(2, page.TotalMatched);
        Assert.All(page.Hits, h => Assert.Equal(rootId, h.Session));
        // Newest-first: the later turn (higher seq) leads.
        Assert.Equal([1, 0], page.Hits.Select(h => h.Seq).ToList());
    }

    [Fact]
    public async Task Execute_SessionScope_MissingId_SurfacesStoreErrorUntouched()
    {
        var unknown = AgentId.NewId();

        var result = await _handler.Execute(null, "literal", $"session:{unknown}", "active", null, 1, 25);

        Assert.False(result.IsSuccess);
        Assert.Equal("NotFound", result.Error!.Code);
        Assert.Contains(unknown.ToString(), result.Error.Message);
    }

    // ---- Paging passthrough ----

    [Fact]
    public async Task Execute_Paging_PreservesTotalPageAndPages()
    {
        var rootId = AgentId.NewId();
        await _store.SaveAsync(AgentRecord.Root(rootId, At(0)));
        for (var n = 1; n <= 5; n++)
            await _store.AppendMessageAsync(rootId,
                new Message(Role.User, $"turn {n}", Base.AddSeconds(n)));

        var result = await _handler.Execute(null, "literal", null, "active", null, 2, 2);

        Assert.True(result.IsSuccess);
        var page = result.Value!;
        Assert.Equal(5, page.TotalMatched);
        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.Pages);
        Assert.Equal(2, page.Hits.Count);
        Assert.Equal(["turn 3", "turn 2"], page.Hits.Select(h => h.Content).ToList());
    }

    // ---- Role filter passthrough ----

    [Fact]
    public async Task Execute_RoleFilter_DropsOtherRolesBeforeSearch()
    {
        await SeedBranchedCorpusAsync();

        var result = await _handler.Execute("alpha", "literal", null, "all", "assistant", 1, 25);

        Assert.True(result.IsSuccess);
        var hit = Assert.Single(result.Value!.Hits);
        Assert.Equal("beta follow-up with alpha context", hit.Content);
    }

    // ---- Empty store ----

    [Fact]
    public async Task Execute_EmptyStore_YieldsZeroHitsAndOnePage()
    {
        var result = await _handler.Execute(null, "literal", null, "active", null, 1, 25);

        Assert.True(result.IsSuccess);
        var page = result.Value!;
        Assert.Empty(page.Hits);
        Assert.Equal(0, page.TotalMatched);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.Pages);
    }
}
