using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain.Tests;

public class CuratedMemoryCapabilityProviderTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Workspace = "ws-abc-123";
    private const string AmbientSession = "session-xyz";
    private static readonly Guid KnownId = Guid.Parse("3f2a9f0e-1111-2222-3333-444455556666");
    private const string KnownFirst8 = "3f2a9f0e";

    private sealed class Harness
    {
        public FakeCuratedMemoryStore Store { get; } = new();
        public int BumpCount { get; private set; }
        public string WorkspaceValue { get; set; } = Workspace;
        public string? AmbientSessionValue { get; set; } = AmbientSession;
        public DateTimeOffset ClockValue { get; set; } = FixedNow;
        public int ProvenanceCalls { get; private set; }

        public CuratedMemoryCapabilityProvider Provider() => new(
            Store,
            () => WorkspaceValue,
            Provenance,
            Bump,
            () => ClockValue);

        private string? Provenance()
        {
            ProvenanceCalls++;
            return AmbientSessionValue;
        }

        private int Bump()
        {
            BumpCount++;
            return BumpCount;
        }
    }

    private static CuratedMemory Row(
        Guid? id = null,
        string workspaceId = Workspace,
        MemoryCategory category = MemoryCategory.Insight,
        IReadOnlyList<string>? tags = null,
        string content = "seed content",
        string? usageHint = null,
        MemoryScope scope = MemoryScope.Workspace,
        string? provenance = null,
        int version = 1) => new(
        id ?? Guid.NewGuid(), workspaceId, category, tags ?? [], content, usageHint,
        scope, provenance, version, FixedNow, FixedNow);

    // ---- provider shape ----

    [Fact]
    public void Provider_ExposesFourActions_UnderMemoriesId()
    {
        var provider = new Harness().Provider();

        Assert.Equal("memories", provider.Id);
        Assert.Equal(["search", "add", "update", "remove"], provider.Actions.Select(a => a.Name));
    }

    // ---- search ----

    [Fact]
    public async Task Search_ZeroHits_ExactSingleLine()
    {
        var result = await new Harness().Provider().InvokeAsync("search", "{}");

        Assert.False(result.IsError);
        Assert.Equal("[memories] 0 hit(s)", result.Content);
    }

    [Fact]
    public async Task Search_RendersHeader_Row_AndHintLines_Exactly()
    {
        var h = new Harness();
        h.Store.Rows[KnownId] = Row(
            id: KnownId, scope: MemoryScope.Global, workspaceId: "",
            category: MemoryCategory.Preference, tags: ["api", "sql"],
            content: "Prefer explicit over implicit.", usageHint: "Cite in reviews.");

        var result = await h.Provider().InvokeAsync("search", "{}");

        Assert.False(result.IsError);
        Assert.Equal(
            "[memories] 1 hit(s)\n" +
            "[mem] id=3f2a9f0e v1 cat=preference scope=global tags=api,sql :: Prefer explicit over implicit.\n" +
            "     hint: Cite in reviews.",
            result.Content);
    }

    [Fact]
    public async Task Search_Defaults_ForwardWorkspaceAndLimit20()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("search", "{}");

        Assert.False(result.IsError);
        Assert.Equal(Workspace, h.Store.LastSearchWorkspaceId);
        Assert.Null(h.Store.LastSearchQuery);
        Assert.Null(h.Store.LastSearchCategory);
        Assert.Equal([], h.Store.LastSearchTags); // absent array forwards [], the store's "no constraint"
        Assert.Equal(20, h.Store.LastSearchLimit);
    }

    [Fact]
    public async Task Search_AllParameters_ParsedAndForwarded()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("search",
            """{"query":"ftx","category":"failure","tags":["api","sql"],"scope":"workspace","limit":5}""");

        Assert.False(result.IsError);
        Assert.Equal("ftx", h.Store.LastSearchQuery);
        Assert.Equal(MemoryCategory.Failure, h.Store.LastSearchCategory);
        Assert.NotNull(h.Store.LastSearchTags);
        Assert.Equal(["api", "sql"], h.Store.LastSearchTags!);
        Assert.Equal(5, h.Store.LastSearchLimit);
    }

    [Fact]
    public async Task Search_TruncatesContentAt120_AndHintAt80_WithoutMarkers()
    {
        var h = new Harness();
        var longContent = new string('x', 300);
        var longHint = new string('y', 150);
        h.Store.Rows[KnownId] = Row(id: KnownId, content: longContent, usageHint: longHint);

        var result = await h.Provider().InvokeAsync("search", "{}");

        var lines = result.Content.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("[memories] 1 hit(s)", lines[0]);
        Assert.Equal(
            $"[mem] id={KnownFirst8} v1 cat=insight scope=workspace tags= :: {new string('x', 120)}",
            lines[1]);
        Assert.Equal($"     hint: {new string('y', 80)}", lines[2]);
    }

    [Fact]
    public async Task Search_LimitOvershoot_ClampsTo100_WithVisibleWarningLine()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("search", """{"limit":250}""");

        Assert.False(result.IsError);
        Assert.Equal(100, h.Store.LastSearchLimit);
        Assert.EndsWith("[warning] limit clamped to 100", result.Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Search_LimitBelowOne_RejectedWithInvalidLimit(int limit)
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("search", $$"""{"limit":{{limit}}}""");

        Assert.True(result.IsError);
        Assert.Equal("Error [InvalidLimit]: 'limit' must be an integer >= 1.", result.Content);
        Assert.Null(h.Store.LastSearchWorkspaceId); // rejected before the store was ever consulted
    }

    [Fact]
    public async Task Search_Category_IsExactLowercaseOnly()
    {
        var result = await new Harness().Provider().InvokeAsync("search", """{"category":"Insight"}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidCategory]:", result.Content);
        foreach (var name in (string[])["convention", "preference", "insight", "failure", "reference"])
            Assert.Contains(name, result.Content);
    }

    [Fact]
    public async Task Search_Scope_IsExactLowercaseOnly()
    {
        var result = await new Harness().Provider().InvokeAsync("search", """{"scope":"Galaxy"}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidScope]:", result.Content);
        Assert.Contains("workspace | global", result.Content);
    }

    [Fact]
    public async Task Search_ScopeFilter_NarrowsVisibleRows()
    {
        var h = new Harness();
        var wsRow = Row(content: "ws note", scope: MemoryScope.Workspace, workspaceId: Workspace);
        var globalRow = Row(content: "global note", scope: MemoryScope.Global, workspaceId: "");
        h.Store.Rows[wsRow.Id] = wsRow;
        h.Store.Rows[globalRow.Id] = globalRow;

        var anyScope = await h.Provider().InvokeAsync("search", "{}");
        var workspaceOnly = await h.Provider().InvokeAsync("search", """{"scope":"workspace"}""");
        var globalOnly = await h.Provider().InvokeAsync("search", """{"scope":"global"}""");

        Assert.Contains("ws note", anyScope.Content);
        Assert.Contains("global note", anyScope.Content);
        Assert.Contains("ws note", workspaceOnly.Content);
        Assert.DoesNotContain("global note", workspaceOnly.Content);
        Assert.Contains("global note", globalOnly.Content);
        Assert.DoesNotContain("ws note", globalOnly.Content);
    }

    // ---- add ----

    [Fact]
    public async Task Add_HappyPath_WorkspaceScope_BuildsFullRecord_BumpsOnce()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("add",
            """{"content":"  Deploy via winget.  ","category":"convention","tags":["deploy","winget","deploy"],"usage_hint":"Check before releases","scope":"workspace"}""");

        Assert.False(result.IsError);
        Assert.Equal(1, h.Store.AddCallCount);
        var stored = h.Store.Rows.Values.Single();
        Assert.Equal(
            $"[memories] added {stored.Id.ToString("N")[..8]} v1 (cat=convention scope=workspace)",
            result.Content);

        Assert.Equal("Deploy via winget.", stored.Content); // trimmed
        Assert.Equal(MemoryCategory.Convention, stored.Category);
        Assert.Equal(["deploy", "winget"], stored.Tags); // deduplicated, first-seen order
        Assert.Equal("Check before releases", stored.UsageHint);
        Assert.Equal(MemoryScope.Workspace, stored.Scope);
        Assert.Equal(Workspace, stored.WorkspaceId); // keyed by the service's injected workspace id
        Assert.Equal(1, stored.Version);
        Assert.Equal(FixedNow, stored.CreatedAt);
        Assert.Equal(FixedNow, stored.UpdatedAt);
        Assert.Equal(AmbientSession, stored.ProvenanceSession); // ambient, captured via accessor
        Assert.Equal(1, h.ProvenanceCalls);
        Assert.Equal(1, h.BumpCount); // bumped exactly once per successful add
    }

    [Fact]
    public async Task Add_GlobalScope_EmptyWorkspaceKey()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("add",
            """{"content":"Pin .NET 10 SDK.","category":"reference","scope":"global"}""");

        Assert.False(result.IsError);
        var stored = h.Store.Rows.Values.Single();
        Assert.Equal("", stored.WorkspaceId); // empty string ⇒ Global scope row
        Assert.Equal(MemoryScope.Global, stored.Scope);
        Assert.Contains("(cat=reference scope=global)", result.Content);
        Assert.Equal([], stored.Tags);
        Assert.Null(stored.UsageHint);
        Assert.Equal(1, h.BumpCount);
    }

    [Theory]
    [InlineData("""{"category":"insight","scope":"workspace"}""")]
    [InlineData("""{"content":"","category":"insight","scope":"workspace"}""")]
    [InlineData("""{"content":"   ","category":"insight","scope":"workspace"}""")]
    public async Task Add_Content_RequiredNonEmptyAfterTrim(string json)
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("add", json);

        Assert.StartsWith("Error [MissingContent]:", result.Content);
        Assert.Equal(0, h.Store.AddCallCount);
        Assert.Equal(0, h.BumpCount); // failed validation never bumps the counter
    }

    [Fact]
    public async Task Add_ContentOver4000_NamesLimitAndActual()
    {
        var h = new Harness();
        var json = """{"content":""" + "\"" + new string('a', 4001) + "\""
                   + ""","category":"insight","scope":"global"}""";

        var result = await h.Provider().InvokeAsync("add", json);

        Assert.True(result.IsError);
        Assert.StartsWith("Error [ContentTooLong]:", result.Content);
        Assert.Contains("4000", result.Content);
        Assert.Contains("4001", result.Content);
        Assert.Equal(0, h.BumpCount);
    }

    [Fact]
    public async Task Add_MissingCategory_FailsBeforeStore()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("add",
            """{"content":"note","scope":"workspace"}""");

        Assert.StartsWith("Error [MissingCategory]:", result.Content);
        Assert.Equal(0, h.Store.AddCallCount);
        Assert.Equal(0, h.BumpCount);
    }

    [Fact]
    public async Task Add_InvalidCategory_ListsAllFiveCategories()
    {
        var result = await new Harness().Provider().InvokeAsync("add",
            """{"content":"note","category":"Curated","scope":"workspace"}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidCategory]:", result.Content);
        foreach (var name in (string[])["convention", "preference", "insight", "failure", "reference"])
            Assert.Contains(name, result.Content);
    }

    [Fact]
    public async Task Add_TooManyTags_NamesLimitAndActual()
    {
        var tags = Enumerable.Range(1, 13).Select(i => $"tag{i}");
        var json = """{"content":"note","category":"insight","scope":"global","tags":["""
                   + string.Join(",", tags.Select(t => $"\"{t}\"")) + "]}";

        var result = await new Harness().Provider().InvokeAsync("add", json);

        Assert.True(result.IsError);
        Assert.StartsWith("Error [TooManyTags]:", result.Content);
        Assert.Contains("12", result.Content);
        Assert.Contains("13", result.Content);
    }

    [Fact]
    public async Task Add_InvalidTag_QuotesTheRule()
    {
        var result = await new Harness().Provider().InvokeAsync("add",
            """{"content":"note","category":"insight","scope":"global","tags":["Bad Tag"]}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidTag]:", result.Content);
        Assert.Contains("Bad Tag", result.Content);
        Assert.Contains("^[a-z0-9][a-z0-9-_]{0,31}$", result.Content);
    }

    [Fact]
    public async Task Add_HintTooLong_NamesLimit()
    {
        var json = """{"content":"note","category":"insight","scope":"global","usage_hint":"""
                   + "\"" + new string('h', 201) + "\"}";

        var result = await new Harness().Provider().InvokeAsync("add", json);

        Assert.True(result.IsError);
        Assert.StartsWith("Error [HintTooLong]:", result.Content);
        Assert.Contains("200", result.Content);
    }

    [Fact]
    public async Task Add_MissingScope_Fails()
    {
        var result = await new Harness().Provider().InvokeAsync("add",
            """{"content":"note","category":"insight"}""");

        Assert.StartsWith("Error [MissingScope]:", result.Content);
    }

    [Fact]
    public async Task Add_Scope_IsExactLowercaseOnly()
    {
        var result = await new Harness().Provider().InvokeAsync("add",
            """{"content":"note","category":"insight","scope":"Workspace"}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidScope]:", result.Content);
        Assert.Contains("workspace | global", result.Content);
    }

    [Fact]
    public async Task Add_SessionParameter_Rejected_ProvenanceStaysAmbient()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("add",
            """{"content":"note","category":"insight","scope":"global","session":"forged-id"}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidActionInput]:", result.Content);
        Assert.Contains("Unknown parameter 'session'", result.Content);
        Assert.Equal(0, h.Store.AddCallCount);
        Assert.Equal(0, h.BumpCount);
    }

    [Fact]
    public async Task Add_StoreFailure_SurfacesTypedError_NeverBumps()
    {
        var h = new Harness();
        h.Store.AddResultOverride = Result<CuratedMemory>.Failure(
            new Error(CuratedMemoryErrors.StorageError, "disk unavailable"));

        var result = await h.Provider().InvokeAsync("add",
            """{"content":"note","category":"insight","scope":"global"}""");

        Assert.True(result.IsError);
        Assert.Equal("Error [StorageError]: disk unavailable", result.Content);
        Assert.Equal(0, h.BumpCount);
    }

    // ---- update ----

    [Fact]
    public async Task Update_HappyPath_AppliesDeltas_BumpsVersion_UsesClock_NeverBumpsCounter()
    {
        var h = new Harness();
        h.Store.Rows[KnownId] = Row(
            id: KnownId, version: 2, content: "old body", tags: ["legacy"],
            category: MemoryCategory.Insight, usageHint: "keep me", provenance: "orig-session");
        h.ClockValue = FixedNow.AddMinutes(5);

        var result = await h.Provider().InvokeAsync("update",
            $$"""{"id":"{{KnownId}}","expected_version":2,"content":"new body","tags":["fresh"]}""");

        Assert.False(result.IsError);
        Assert.Equal($"[memories] updated {KnownFirst8} v3", result.Content);
        var stored = h.Store.Rows[KnownId];
        Assert.Equal(3, stored.Version);
        Assert.Equal("new body", stored.Content);
        Assert.Equal(["fresh"], stored.Tags);
        Assert.Equal(MemoryCategory.Insight, stored.Category); // untouched delta survives
        Assert.Equal("keep me", stored.UsageHint); // untouched delta survives
        Assert.Equal("orig-session", stored.ProvenanceSession); // provenance never rewritten
        Assert.Equal(FixedNow, stored.CreatedAt); // creation facts immutable
        Assert.Equal(FixedNow.AddMinutes(5), stored.UpdatedAt); // clock applied
        Assert.Equal(0, h.BumpCount); // only adds drive the nudge counter
    }

    [Theory]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","content":"x"}""")]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","expected_version":0,"content":"x"}""")]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","expected_version":-2,"content":"x"}""")]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","expected_version":"2","content":"x"}""")]
    public async Task Update_ExpectedVersion_MustBePresentIntegerAtLeastOne(string json)
    {
        var result = await new Harness().Provider().InvokeAsync("update", json);

        Assert.StartsWith("Error [MissingVersion]:", result.Content);
    }

    [Fact]
    public async Task Update_UnparsableId_FailsInvalidId()
    {
        var result = await new Harness().Provider().InvokeAsync("update",
            """{"id":"not-a-guid","expected_version":1,"content":"x"}""");

        Assert.StartsWith("Error [InvalidId]:", result.Content);
        Assert.Contains("not-a-guid", result.Content);
    }

    [Fact]
    public async Task Update_NothingToUpdate_FailsBeforeFetch()
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("update",
            $$"""{"id":"{{KnownId}}","expected_version":2}""");

        Assert.StartsWith("Error [NothingToUpdate]:", result.Content);
        Assert.Equal(0, h.Store.GetCallCount);
    }

    [Fact]
    public async Task Update_StaleExpectedVersion_SurfacesVersionConflictNamingCurrent()
    {
        var h = new Harness();
        h.Store.Rows[KnownId] = Row(id: KnownId, version: 3, content: "current truth");

        var result = await h.Provider().InvokeAsync("update",
            $$"""{"id":"{{KnownId}}","expected_version":2,"content":"stale write"}""");

        Assert.True(result.IsError);
        Assert.Equal("Error [VersionConflict]: current stored version is 3.", result.Content);
        Assert.Equal(3, h.Store.Rows[KnownId].Version); // conflicting write changed nothing
        Assert.Equal("current truth", h.Store.Rows[KnownId].Content);
    }

    [Fact]
    public async Task Update_UnknownId_FailsMemoryNotFound()
    {
        var result = await new Harness().Provider().InvokeAsync("update",
            $$"""{"id":"{{KnownId}}","expected_version":1,"content":"x"}""");

        Assert.StartsWith("Error [MemoryNotFound]:", result.Content);
    }

    // ---- remove ----

    [Fact]
    public async Task Remove_HappyPath_RemovesRow_ExactOutput()
    {
        var h = new Harness();
        h.Store.Rows[KnownId] = Row(id: KnownId);

        var result = await h.Provider().InvokeAsync("remove",
            $$"""{"id":"{{KnownId}}","confirm":true}""");

        Assert.False(result.IsError);
        Assert.Equal($"[memories] removed {KnownFirst8}", result.Content);
        Assert.Equal([KnownId], h.Store.Deletes);
        Assert.Equal(0, h.BumpCount);
    }

    [Theory]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666"}""")]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","confirm":false}""")]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","confirm":"true"}""")]
    [InlineData("""{"id":"3f2a9f0e-1111-2222-3333-444455556666","confirm":1}""")]
    public async Task Remove_ConfirmGate_RequiresExactlyBooleanTrue(string json)
    {
        var h = new Harness();

        var result = await h.Provider().InvokeAsync("remove", json);

        Assert.StartsWith("Error [RemoveNotConfirmed]:", result.Content);
        Assert.Empty(h.Store.Deletes);
    }

    [Fact]
    public async Task Remove_UnknownId_FailsMemoryNotFound()
    {
        var result = await new Harness().Provider().InvokeAsync("remove",
            $$"""{"id":"{{KnownId}}","confirm":true}""");

        Assert.StartsWith("Error [MemoryNotFound]:", result.Content);
    }

    // ---- cross-cutting strictness ----

    [Theory]
    [InlineData("search", """{"bogus":1}""")]
    [InlineData("add", """{"bogus":1}""")]
    [InlineData("update", """{"bogus":1}""")]
    [InlineData("remove", """{"bogus":1}""")]
    public async Task UnknownParameter_Rejected_OnEveryAction(string action, string json)
    {
        var result = await new Harness().Provider().InvokeAsync(action, json);

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidActionInput]:", result.Content);
        Assert.Contains("Unknown parameter 'bogus'", result.Content);
    }

    [Fact]
    public async Task MalformedJson_TypedInputError()
    {
        var result = await new Harness().Provider().InvokeAsync("search", "{oops");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidActionInput]:", result.Content);
        Assert.Contains("not valid JSON", result.Content);
    }

    [Fact]
    public async Task NonObjectJson_Rejected()
    {
        var result = await new Harness().Provider().InvokeAsync("search", "[1,2]");

        Assert.True(result.IsError);
        Assert.StartsWith("Error [InvalidActionInput]:", result.Content);
        Assert.Contains("JSON object", result.Content);
    }

    [Fact]
    public async Task UnknownAction_ReturnsTypedError()
    {
        var result = await new Harness().Provider().InvokeAsync("upsert", "{}");

        Assert.True(result.IsError);
        Assert.Equal("Error [UnknownAction]: Unknown action: upsert.", result.Content);
    }

    private sealed class FakeCuratedMemoryStore : ICuratedMemoryStore
    {
        public Dictionary<Guid, CuratedMemory> Rows = [];
        public Result<CuratedMemory>? AddResultOverride;

        public int AddCallCount;
        public int GetCallCount;
        public string? LastSearchWorkspaceId;
        public string? LastSearchQuery;
        public MemoryCategory? LastSearchCategory;
        public IReadOnlyList<string>? LastSearchTags;
        public int LastSearchLimit;
        public List<Guid> Deletes = [];

        public Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
        {
            AddCallCount++;
            if (AddResultOverride is { } overridden)
                return Task.FromResult(overridden);
            Rows[memory.Id] = memory;
            return Task.FromResult(Result<CuratedMemory>.Success(memory));
        }

        public Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
        {
            GetCallCount++;
            return Task.FromResult(Result<CuratedMemory?>.Success(Rows.GetValueOrDefault(id)));
        }

        public Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
            string? workspaceId, string? query, MemoryCategory? category,
            IReadOnlyList<string>? tags, int limit, CancellationToken ct = default)
        {
            LastSearchWorkspaceId = workspaceId;
            LastSearchQuery = query;
            LastSearchCategory = category;
            LastSearchTags = tags;
            LastSearchLimit = limit;

            IEnumerable<CuratedMemory> visible = Rows.Values
                .Where(m => m.Scope == MemoryScope.Global || m.WorkspaceId == workspaceId)
                .OrderByDescending(m => m.UpdatedAt);
            if (!string.IsNullOrWhiteSpace(query))
                visible = visible.Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (category is not null)
                visible = visible.Where(m => m.Category == category);
            if (tags is { Count: > 0 })
                visible = visible.Where(m => tags.All(t => m.Tags.Contains(t)));
            return Task.FromResult(Result<IReadOnlyList<CuratedMemory>>.Success(visible.Take(limit).ToList()));
        }

        public Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
        {
            if (!Rows.TryGetValue(updated.Id, out var stored))
                return Task.FromResult(Result<CuratedMemory>.Failure(new Error(
                    CuratedMemoryErrors.MemoryNotFound,
                    $"No curated memory with id '{updated.Id}'.")));
            if (stored.Version != updated.Version - 1)
                return Task.FromResult(Result<CuratedMemory>.Failure(new Error(
                    CuratedMemoryErrors.VersionConflict,
                    $"current stored version is {stored.Version}.")));
            Rows[updated.Id] = updated;
            return Task.FromResult(Result<CuratedMemory>.Success(updated));
        }

        public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Deletes.Add(id);
            return Task.FromResult(Result<bool>.Success(Rows.Remove(id)));
        }
    }
}
