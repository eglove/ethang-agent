using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain.Tests;

public class SearchServiceTests
{
    private static readonly DateTimeOffset Base =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minutes) => Base.AddMinutes(minutes);

    private static string Rendered(Result<SessionScope> result)
        => $"Error [{result.Error!.Code}]: {result.Error.Message}";

    // ---- SessionScope.Parse ----

    [Theory]
    [InlineData("global")]
    [InlineData("GLOBAL")]
    [InlineData("Global")]
    public void Parse_GlobalSpellingAnyCase_ParsesToGlobal(string raw)
    {
        var result = SessionScope.Parse(raw);

        Assert.True(result.IsSuccess);
        Assert.IsType<SessionScope.Global>(result.Value);
    }

    [Fact]
    public void Parse_Null_ParsesToGlobal()
    {
        var result = SessionScope.Parse(null);

        Assert.True(result.IsSuccess);
        Assert.IsType<SessionScope.Global>(result.Value);
    }

    [Fact]
    public void Parse_SessionGuidExactDFormat_ParsesToSessionWithId()
    {
        var id = Guid.NewGuid();

        var result = SessionScope.Parse($"session:{id:D}");

        Assert.True(result.IsSuccess);
        var session = Assert.IsType<SessionScope.Session>(result.Value);
        Assert.Equal(new AgentId(id), session.Id);
    }

    [Fact]
    public void Parse_SessionGuidNFormat_IsRejected_ExactDFormatRequired()
    {
        var result = SessionScope.Parse($"session:{Guid.NewGuid():N}");

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidScope", result.Error!.Code);
    }

    [Fact]
    public void Parse_SessionEmptyRemainder_FailsWithRawInMessage()
    {
        var result = SessionScope.Parse("session:");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Error [InvalidScope]: Unknown scope 'session:'. Valid scopes: global | session:<agentId>.",
            Rendered(result));
    }

    [Fact]
    public void Parse_UppercaseSessionPrefix_FailsWithRawVerbatim()
    {
        // Only 'global' is case-insensitive by design; the session prefix is strict.
        var raw = $"SESSION:{Guid.NewGuid():D}";

        var result = SessionScope.Parse(raw);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            $"Error [InvalidScope]: Unknown scope '{raw}'. Valid scopes: global | session:<agentId>.",
            Rendered(result));
    }

    [Fact]
    public void Parse_MalformedSessionValue_FailsWithRawVerbatim()
    {
        var result = SessionScope.Parse("session:not-a-guid");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Error [InvalidScope]: Unknown scope 'session:not-a-guid'. Valid scopes: global | session:<agentId>.",
            Rendered(result));
    }

    [Fact]
    public void Parse_UnknownSpelling_FailsNamingBothValidForms()
    {
        var result = SessionScope.Parse("project:x");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Error [InvalidScope]: Unknown scope 'project:x'. Valid scopes: global | session:<agentId>.",
            Rendered(result));
    }

    // ---- Scope filtering ----

    [Fact]
    public void Search_SessionScope_KeepsOnlyTheMatchingSession()
    {
        var a = new AgentId(Guid.NewGuid());
        var b = new AgentId(Guid.NewGuid());
        var sessions = new List<SessionCorpus>
        {
            new(a, null, 0, [new(a, 1, "user", "alpha from a", At(1))]),
            new(b, null, 0, [new(b, 1, "user", "beta from b", At(2))]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.Browse(), SessionScope.Parse($"session:{b}").Value!,
            BranchMode.AllBranches, null, 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(["beta from b"], ok.Result.Hits.Select(h => h.Entry.Content));
        Assert.Equal(1, ok.Result.TotalMatched);
    }

    // ---- Branch resolution ----

    [Fact]
    public void Search_ActivePath_KeepsRootLineageOnly_ExcludesOrphanChains()
    {
        var rootId = NewId();
        var childId = NewId();
        var grandchildId = NewId();
        var missingAncestorId = NewId();
        var orphanId = NewId();
        var orphanChildId = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(rootId, null, 0, [new(rootId, 1, "user", "root line", At(0))]),
            new(childId, rootId, 1, [new(childId, 1, "user", "child line", At(1))]),
            new(grandchildId, childId, 2, [new(grandchildId, 1, "user", "grandchild line", At(2))]),
            // Orphan chain: ancestor row absent from the set — excluded under ActivePath.
            new(orphanId, missingAncestorId, 1, [new(orphanId, 1, "user", "orphan line", At(3))]),
            new(orphanChildId, orphanId, 2, [new(orphanChildId, 1, "user", "orphan child line", At(4))]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 50);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(
            ["grandchild line", "child line", "root line"],
            ok.Result.Hits.Select(h => h.Entry.Content));
        Assert.Equal(3, ok.Result.TotalMatched);
    }

    [Fact]
    public void Search_AllBranches_KeepsOrphanChainsToo()
    {
        var rootId = NewId();
        var missingAncestorId = NewId();
        var orphanId = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(rootId, null, 0, [new(rootId, 1, "user", "root line", At(0))]),
            new(orphanId, missingAncestorId, 1, [new(orphanId, 1, "user", "orphan line", At(3))]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.AllBranches, null, 1, 50);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(["orphan line", "root line"], ok.Result.Hits.Select(h => h.Entry.Content));
        Assert.Equal(2, ok.Result.TotalMatched);
    }

    // ---- Terms (literal) matching ----

    [Fact]
    public void Search_Terms_EntryMatchesOnlyWhenEveryTokenIsPresent()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0,
            [
                new(id, 3, "assistant", "alpha beta together", At(3)),
                new(id, 2, "user", "alpha alone", At(2)),
                new(id, 1, "tool", "BETA-ALPHA both canonical tokens", At(1)),
            ]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, MemoryQueryPlan.Plan("alpha beta"), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(
            ["alpha beta together", "BETA-ALPHA both canonical tokens"],
            ok.Result.Hits.Select(h => h.Entry.Content));
        Assert.Equal(2, ok.Result.TotalMatched);
    }

    [Fact]
    public void Search_Terms_TokenAbsentEverywhere_YieldsZeroHitsAndOnePage()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0,
            [
                new(id, 2, "user", "alpha here", At(2)),
                new(id, 1, "user", "zebra there", At(1)),
            ]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, MemoryQueryPlan.Plan("alpha zebra"), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Empty(ok.Result.Hits);
        Assert.Equal(0, ok.Result.TotalMatched);
        Assert.Equal(1, ok.Result.Pages);
    }

    // ---- Regex mode ----

    [Fact]
    public void Search_RegexPattern_KeepsMatchedEntries_InCanonicalOrder()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0,
            [
                new(id, 4, "user", "catch22 details", At(4)),
                new(id, 3, "assistant", "nothing relevant here", At(3)),
                new(id, 2, "user", "CAT-22 report", At(2)),
            ]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.RegexPattern("cat.{0,2}22"), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(["catch22 details", "CAT-22 report"], ok.Result.Hits.Select(h => h.Entry.Content));
        Assert.Equal(2, ok.Result.TotalMatched);
    }

    [Fact]
    public void Search_RegexPattern_InvalidPattern_SurfacesTypedFailure()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0, [new(id, 1, "user", "content", At(1))]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.RegexPattern("(["), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 10);

        var fail = Assert.IsType<SearchOutcome.Fail>(outcome);
        Assert.StartsWith("Error [invalid_regex]:", fail.Error);
    }

    [Fact]
    public void Search_RegexPattern_OversizedPattern_SurfacesExactTooLargeFailure()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0, [new(id, 1, "user", "content", At(1))]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.RegexPattern(new string('a', 1100)),
            new SessionScope.Global(), BranchMode.ActivePath, null, 1, 10);

        var fail = Assert.IsType<SearchOutcome.Fail>(outcome);
        Assert.Equal(
            "Error [regex_pattern_too_large]: Regex pattern exceeds 1024 bytes.",
            fail.Error);
    }

    // ---- Role filter ----

    [Fact]
    public void Search_RoleFilter_DropsNonMatchingEntriesBeforeSearch()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0,
            [
                new(id, 2, "assistant", "alpha from assistant", At(2)),
                new(id, 1, "user", "alpha from user", At(1)),
            ]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, MemoryQueryPlan.Plan("alpha"), new SessionScope.Global(),
            BranchMode.ActivePath, "user", 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(["alpha from user"], ok.Result.Hits.Select(h => h.Entry.Content));
        Assert.Equal(1, ok.Result.TotalMatched);
    }

    [Fact]
    public void Search_RoleFilter_IsCaseInsensitive()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0,
            [
                new(id, 2, "assistant", "assistant line", At(2)),
                new(id, 1, "user", "user line", At(1)),
            ]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, "USER", 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(["user line"], ok.Result.Hits.Select(h => h.Entry.Content));
    }

    [Fact]
    public void Search_RoleFilter_NullOrWhitespace_MeansNoFiltering()
    {
        var id = NewId();
        var sessions = new List<SessionCorpus>
        {
            new(id, null, 0,
            [
                new(id, 2, "assistant", "assistant line", At(2)),
                new(id, 1, "user", "user line", At(1)),
            ]),
        };
        var service = new SearchService();

        foreach (var role in new[] { null, "", "   " })
        {
            var outcome = service.Search(
                sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
                BranchMode.ActivePath, role, 1, 10);

            var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
            Assert.Equal(2, ok.Result.TotalMatched);
        }
    }

    // ---- Browse ordering ----

    [Fact]
    public void Search_Browse_OrdersByTimestampDescThenSeqDescThenSessionOrdinalAscending()
    {
        // Two fresh ids, relabelled so `first` sorts before `second` ordinally —
        // pins the final tiebreak without depending on guid randomness.
        var left = NewId();
        var right = NewId();
        if (string.CompareOrdinal(left.Value.ToString(), right.Value.ToString()) > 0)
            (left, right) = (right, left);

        var sessions = new List<SessionCorpus>
        {
            new(right, null, 0,
            [
                new(right, 3, "user", "late right", At(5)),
                new(right, 4, "user", "early right seq4", At(0)),
            ]),
            new(left, null, 0,
            [
                new(left, 7, "user", "late left", At(5)),
                new(left, 4, "user", "early left seq4", At(0)),
            ]),
        };
        var service = new SearchService();

        var outcome = service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 50);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Equal(
            ["late left", "late right", "early left seq4", "early right seq4"],
            ok.Result.Hits.Select(h => h.Entry.Content));
    }

    // ---- Paging ----

    [Fact]
    public void Search_Paging_Total25PageSize10_YieldsThreePages_LastHasFive()
    {
        var id = NewId();
        var entries = Enumerable.Range(1, 25)
            .Select(n => new MemoryEntry(id, n, "user", $"entry {n}", Base.AddSeconds(n)))
            .ToList();
        var sessions = new List<SessionCorpus> { new(id, null, 0, entries) };
        var service = new SearchService();

        var page1 = Assert.IsType<SearchOutcome.Ok>(service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 10)).Result;
        var page2 = Assert.IsType<SearchOutcome.Ok>(service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, 2, 10)).Result;
        var page3 = Assert.IsType<SearchOutcome.Ok>(service.Search(
            sessions, new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, 3, 10)).Result;

        Assert.Equal(25, page1.TotalMatched);
        Assert.Equal(3, page1.Pages);
        Assert.Equal(10, page1.Hits.Count);
        Assert.Equal("entry 25", page1.Hits[0].Entry.Content);

        Assert.Equal(10, page2.Hits.Count);
        Assert.Equal("entry 15", page2.Hits[0].Entry.Content);

        Assert.Equal(5, page3.Hits.Count);
        Assert.Equal(["entry 5", "entry 4", "entry 3", "entry 2", "entry 1"],
            page3.Hits.Select(h => h.Entry.Content));
        Assert.Equal(3, page3.Page);
    }

    [Fact]
    public void Search_EmptyCorpus_YieldsZeroHitsAndOnePage()
    {
        var service = new SearchService();

        var outcome = service.Search(
            [], new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, 1, 10);

        var ok = Assert.IsType<SearchOutcome.Ok>(outcome);
        Assert.Empty(ok.Result.Hits);
        Assert.Equal(0, ok.Result.TotalMatched);
        Assert.Equal(1, ok.Result.Pages);
        Assert.Equal(1, ok.Result.Page);
    }

    // ---- Programmer-error guards (capability layer validates wire input first) ----

    [Fact]
    public void Search_PageBelowOne_IsProgrammerError()
    {
        var service = new SearchService();

        Assert.Throws<ArgumentException>(() => service.Search(
            [], new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, page: 0, pageSize: 10));
    }

    [Fact]
    public void Search_PageSizeBelowOne_IsProgrammerError()
    {
        var service = new SearchService();

        Assert.Throws<ArgumentException>(() => service.Search(
            [], new MemoryQueryPlan.Browse(), new SessionScope.Global(),
            BranchMode.ActivePath, null, page: 1, pageSize: 0));
    }

    private static AgentId NewId() => new(Guid.NewGuid());
}
