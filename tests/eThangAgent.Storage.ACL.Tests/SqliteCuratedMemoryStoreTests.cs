using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Storage.ACL.Tests;

public class SqliteCuratedMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp
        = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-curated-{Guid.NewGuid():N}.db");
    private readonly AppDatabase _database;
    private readonly SqliteCuratedMemoryStore _store;

    public SqliteCuratedMemoryStoreTests()
    {
        _database = new AppDatabase(_dbPath);
        _store = new SqliteCuratedMemoryStore(_database);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private static CuratedMemory MakeMemory(
        Guid? id = null,
        string workspaceId = "ws-1",
        MemoryScope scope = MemoryScope.Workspace,
        MemoryCategory category = MemoryCategory.Convention,
        IReadOnlyList<string>? tags = null,
        string content = "default content body",
        string? usageHint = null,
        string? provenanceSession = null,
        int version = 1,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
        => new(
            id ?? Guid.NewGuid(),
            workspaceId,
            category,
            tags ?? ["api"],
            content,
            usageHint,
            scope,
            provenanceSession,
            version,
            createdAt ?? Timestamp,
            updatedAt ?? Timestamp);

    /// <summary>Full-field comparison: tags compare as sequences, since
    /// IReadOnlyList equality on the record is referential and a persisted row's
    /// deserialized list will never be the same instance as the original.</summary>
    private static void AssertSameFields(CuratedMemory expected, CuratedMemory actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.WorkspaceId, actual.WorkspaceId);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.UsageHint, actual.UsageHint);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.ProvenanceSession, actual.ProvenanceSession);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    /// <summary>Asserts a Result succeeded, surfacing the error message on failure.
    /// Null-safe: a successful Result carries no error.</summary>
    private static void AssertSuccess<T>(Result<T> result)
        => Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task Add_ThenGet_RoundTripsEveryField_ProvenanceNullAndSet()
    {
        var withProvenance = MakeMemory(
            workspaceId: "ws-a",
            category: MemoryCategory.Failure,
            tags: ["api", "sql"],
            content: "Retry with backoff when the API rate-limits.",
            usageHint: "Cite when writing retry logic.",
            scope: MemoryScope.Workspace,
            provenanceSession: "session-abc123",
            version: 1,
            createdAt: Timestamp,
            updatedAt: Timestamp.AddMinutes(1));
        var withoutProvenance = MakeMemory(
            workspaceId: "",
            scope: MemoryScope.Global,
            category: MemoryCategory.Preference,
            tags: ["style"],
            content: "Prefer explicit over clever.",
            usageHint: null,
            provenanceSession: null,
            version: 3);

        var addedWith = await _store.AddAsync(withProvenance);
        var addedWithout = await _store.AddAsync(withoutProvenance);
        var fetchedWith = await _store.GetAsync(withProvenance.Id);
        var fetchedWithout = await _store.GetAsync(withoutProvenance.Id);

        AssertSuccess(addedWith);
        Assert.Equal(withProvenance, addedWith.Value);
        AssertSuccess(addedWithout);
        Assert.Equal(withoutProvenance, addedWithout.Value);

        AssertSuccess(fetchedWith);
        var storedWith = fetchedWith.Value;
        Assert.NotNull(storedWith);
        Assert.Equal(withProvenance.Id, storedWith!.Id);
        Assert.Equal("ws-a", storedWith.WorkspaceId);
        Assert.Equal(MemoryCategory.Failure, storedWith.Category);
        Assert.Equal(new[] { "api", "sql" }, storedWith.Tags);
        Assert.Equal("Retry with backoff when the API rate-limits.", storedWith.Content);
        Assert.Equal("Cite when writing retry logic.", storedWith.UsageHint);
        Assert.Equal(MemoryScope.Workspace, storedWith.Scope);
        Assert.Equal("session-abc123", storedWith.ProvenanceSession);
        Assert.Equal(1, storedWith.Version);
        Assert.Equal(Timestamp, storedWith.CreatedAt);
        Assert.Equal(Timestamp.AddMinutes(1), storedWith.UpdatedAt);

        AssertSuccess(fetchedWithout);
        var storedWithout = fetchedWithout.Value;
        Assert.NotNull(storedWithout);
        AssertSameFields(withoutProvenance, storedWithout!);
        Assert.Null(storedWithout!.ProvenanceSession);
        Assert.Null(storedWithout.UsageHint);
    }

    [Fact]
    public async Task Search_Visibility_GlobalVisibleViaAnyWorkspace_WorkspaceRowOnlyViaMatchingId()
    {
        var globalRow = MakeMemory(workspaceId: "", scope: MemoryScope.Global, content: "global rule about deploys");
        var wsARow = MakeMemory(workspaceId: "ws-a", scope: MemoryScope.Workspace, content: "workspace rule about deploys");
        await _store.AddAsync(globalRow);
        await _store.AddAsync(wsARow);

        var viaOwner = await _store.SearchAsync("ws-a", null, null, null, 10);
        var viaOther = await _store.SearchAsync("ws-b", null, null, null, 10);
        var viaNull = await _store.SearchAsync(null, null, null, null, 10);

        AssertSuccess(viaOwner);
        Assert.Equal(
            new[] { globalRow.Id, wsARow.Id }.OrderBy(_ => _),
            viaOwner.Value!.Select(m => m.Id).OrderBy(_ => _));
        AssertSuccess(viaOther);
        Assert.Equal(new[] { globalRow.Id }, viaOther.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(viaNull);
        Assert.Equal(new[] { globalRow.Id }, viaNull.Value!.Select(m => m.Id).ToArray());

        // Both ways: a workspace-scoped query term surfaces the owner's row and never another workspace's.
        var ownerHit = await _store.SearchAsync("ws-a", "workspace", null, null, 10);
        AssertSuccess(ownerHit);
        Assert.Equal(new[] { wsARow.Id }, ownerHit.Value!.Select(m => m.Id).ToArray());
        var otherHit = await _store.SearchAsync("ws-b", "workspace", null, null, 10);
        AssertSuccess(otherHit);
        Assert.Empty(otherHit.Value!);
    }

    [Fact]
    public async Task Search_Query_FtsHitRanksMatchingRowFirst_NonMatchingExcluded()
    {
        var strong = MakeMemory(content: "quantum flux calibration quantum flux alignment quantum");
        var weak = MakeMemory(content: "a note about quantum mechanics");
        var unrelated = MakeMemory(content: "gardening tips for spring");
        await _store.AddAsync(strong);
        await _store.AddAsync(weak);
        await _store.AddAsync(unrelated);

        var searched = await _store.SearchAsync("ws-1", "quantum", null, null, 10);

        AssertSuccess(searched);
        Assert.Equal(new[] { strong.Id, weak.Id }, searched.Value!.Select(m => m.Id).ToArray());
        Assert.DoesNotContain(unrelated.Id, searched.Value!.Select(m => m.Id));
    }

    [Fact]
    public async Task Search_MultiTokenQuery_RequiresAllTokens()
    {
        var both = MakeMemory(content: "alpha beta deployment");
        var onlyAlpha = MakeMemory(content: "alpha gamma deployment");
        await _store.AddAsync(both);
        await _store.AddAsync(onlyAlpha);

        var searched = await _store.SearchAsync("ws-1", "alpha beta", null, null, 10);

        AssertSuccess(searched);
        Assert.Equal(new[] { both.Id }, searched.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Search_QueryWithFtsSpecialChars_ExecutesSafely_AndMatchesNothing()
    {
        await _store.AddAsync(MakeMemory(content: "safe content about migrations"));

        var injection = await _store.SearchAsync("ws-1", "test; DROP", null, null, 10);
        var embeddedQuote = await _store.SearchAsync("ws-1", "a\"b", null, null, 10);

        AssertSuccess(injection);
        Assert.Empty(injection.Value!);
        AssertSuccess(embeddedQuote);
        Assert.Empty(embeddedQuote.Value!);

        // The store is unharmed: normal search still works after the hostile input.
        var stillWorking = await _store.SearchAsync("ws-1", "migrations", null, null, 10);
        AssertSuccess(stillWorking);
        Assert.Single(stillWorking.Value!);
    }

    [Fact]
    public async Task Search_CategoryFilter_MatchesExactly()
    {
        var convention = MakeMemory(category: MemoryCategory.Convention, content: "naming conventions");
        var insight = MakeMemory(category: MemoryCategory.Insight, content: "insight about caching");
        await _store.AddAsync(convention);
        await _store.AddAsync(insight);

        var conventions = await _store.SearchAsync("ws-1", null, MemoryCategory.Convention, null, 10);
        var insights = await _store.SearchAsync("ws-1", null, MemoryCategory.Insight, null, 10);
        var any = await _store.SearchAsync("ws-1", null, null, null, 10);

        AssertSuccess(conventions);
        Assert.Equal(new[] { convention.Id }, conventions.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(insights);
        Assert.Equal(new[] { insight.Id }, insights.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(any);
        Assert.Equal(2, any.Value!.Count);

        // The filter also constrains FTS mode.
        var conventionsViaQuery = await _store.SearchAsync("ws-1", "caching", MemoryCategory.Convention, null, 10);
        AssertSuccess(conventionsViaQuery);
        Assert.Empty(conventionsViaQuery.Value!);
        var insightsViaQuery = await _store.SearchAsync("ws-1", "caching", MemoryCategory.Insight, null, 10);
        AssertSuccess(insightsViaQuery);
        Assert.Equal(new[] { insight.Id }, insightsViaQuery.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Search_TagsFilter_RowFoundBySubsetAndFullSet_NotByUnknownTag()
    {
        var tagged = MakeMemory(tags: ["api", "sql"], content: "connection pooling advice");
        var untagged = MakeMemory(tags: ["style"], content: "style guidance");
        await _store.AddAsync(tagged);
        await _store.AddAsync(untagged);

        var bySql = await _store.SearchAsync("ws-1", null, null, ["sql"], 10);
        var byApiAndSql = await _store.SearchAsync("ws-1", null, null, ["api", "sql"], 10);
        var byNope = await _store.SearchAsync("ws-1", null, null, ["nope"], 10);

        AssertSuccess(bySql);
        Assert.Equal(new[] { tagged.Id }, bySql.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(byApiAndSql);
        Assert.Equal(new[] { tagged.Id }, byApiAndSql.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(byNope);
        Assert.Empty(byNope.Value!);

        // An empty tags list imposes no constraint.
        var byEmpty = await _store.SearchAsync("ws-1", null, null, Array.Empty<string>(), 10);
        AssertSuccess(byEmpty);
        Assert.Equal(2, byEmpty.Value!.Count);
    }

    [Fact]
    public async Task Search_TagsFilter_MatchesWholeJsonElements_NotSubstrings()
    {
        // "mysql" contains "sql" and "apidocs" contains "api": a bare substring
        // LIKE over the serialized JSON array would return wrong rows as hits.
        var mysql = MakeMemory(tags: ["mysql"], content: "mysql connection advice");
        var apidocs = MakeMemory(tags: ["apidocs"], content: "api documentation advice");
        await _store.AddAsync(mysql);
        await _store.AddAsync(apidocs);

        var bySql = await _store.SearchAsync("ws-1", null, null, ["sql"], 10);
        var byApi = await _store.SearchAsync("ws-1", null, null, ["api"], 10);
        var byMySql = await _store.SearchAsync("ws-1", null, null, ["mysql"], 10);
        var byApiDocs = await _store.SearchAsync("ws-1", null, null, ["apidocs"], 10);

        AssertSuccess(bySql);
        Assert.Empty(bySql.Value!);
        AssertSuccess(byApi);
        Assert.Empty(byApi.Value!);
        AssertSuccess(byMySql);
        Assert.Equal(new[] { mysql.Id }, byMySql.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(byApiDocs);
        Assert.Equal(new[] { apidocs.Id }, byApiDocs.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Search_RecencyMode_EqualTimestamps_OrderedByDeterministicIdTiebreaker()
    {
        // Six rows share one timestamp; insertion order is the REVERSE of stored-id
        // order, so only an explicit id tiebreaker — not arrival order — can yield
        // a deterministic sequence.
        var idsAscending = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid())
            .OrderBy(g => g.ToString(), StringComparer.Ordinal)
            .ToList();
        foreach (var id in Enumerable.Reverse(idsAscending))
            await _store.AddAsync(MakeMemory(id: id, content: "tiebreaker probe"));

        var searched = await _store.SearchAsync("ws-1", null, null, null, 10);

        AssertSuccess(searched);
        Assert.Equal(idsAscending, searched.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Update_HappyPath_BumpsVersionAndUpdatedAt_HistoryFree()
    {
        var original = MakeMemory(content: "old advice about indexes", tags: ["sql"]);
        await _store.AddAsync(original);
        var updated = original with
        {
            Content = "new advice about covering indexes",
            Tags = ["sql", "perf"],
            Version = 2,
            UpdatedAt = Timestamp.AddHours(1),
        };

        var result = await _store.UpdateAsync(updated);

        AssertSuccess(result);
        Assert.Equal(updated, result.Value);

        var fetched = await _store.GetAsync(original.Id);
        AssertSuccess(fetched);
        Assert.Equal(2, fetched.Value!.Version);
        Assert.Equal("new advice about covering indexes", fetched.Value.Content);
        Assert.Equal(new[] { "sql", "perf" }, fetched.Value.Tags);
        Assert.Equal(original.CreatedAt, fetched.Value.CreatedAt);
        Assert.Equal(Timestamp.AddHours(1), fetched.Value.UpdatedAt);

        // History-free: still exactly one row, and the FTS index tracks the new content only.
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM curated_memories WHERE id = '{original.Id}';"));
        var oldTerm = await _store.SearchAsync("ws-1", "old", null, null, 10);
        AssertSuccess(oldTerm);
        Assert.Empty(oldTerm.Value!);
        var newTerm = await _store.SearchAsync("ws-1", "covering", null, null, 10);
        AssertSuccess(newTerm);
        Assert.Equal(new[] { updated.Id }, newTerm.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Update_StaleVersion_FailsVersionConflict_NamingCurrentVersion()
    {
        var original = MakeMemory(content: "versioned note");
        await _store.AddAsync(original);
        var current = await _store.UpdateAsync(original with { Content = "second write", Version = 2 });
        AssertSuccess(current);
        var stale = original with { Content = "stale write", Version = 1 };

        var result = await _store.UpdateAsync(stale);

        Assert.False(result.IsSuccess);
        Assert.Equal(CuratedMemoryErrors.VersionConflict, result.Error!.Code);
        Assert.Contains("2", result.Error.Message);

        // The conflicting write changed nothing.
        var fetched = await _store.GetAsync(original.Id);
        Assert.Equal("second write", fetched.Value!.Content);
        Assert.Equal(2, fetched.Value.Version);
    }

    [Fact]
    public async Task Update_UnknownMemory_FailsMemoryNotFound()
    {
        var ghost = MakeMemory(content: "never added");

        var result = await _store.UpdateAsync(ghost);

        Assert.False(result.IsSuccess);
        Assert.Equal(CuratedMemoryErrors.MemoryNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task Delete_RemovesRowAndFtsEntry_UnknownReturnsFalse()
    {
        var doomed = MakeMemory(content: "ephemeral zebra note");
        await _store.AddAsync(doomed);
        var before = await _store.SearchAsync("ws-1", "zebra", null, null, 10);
        Assert.Single(before.Value!);

        var deleted = await _store.DeleteAsync(doomed.Id);

        AssertSuccess(deleted);
        Assert.True(deleted.Value);
        Assert.Null((await _store.GetAsync(doomed.Id)).Value);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM curated_memories;"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM curated_memories_fts;"));

        var after = await _store.SearchAsync("ws-1", "zebra", null, null, 10);
        AssertSuccess(after);
        Assert.Empty(after.Value!);

        var unknown = await _store.DeleteAsync(Guid.NewGuid());
        AssertSuccess(unknown);
        Assert.False(unknown.Value);
    }

    [Fact]
    public void Migration_CreatesFtsIndexAndTriggers_CountSucceedsPostInit()
    {
        var exception = Record.Exception(() => Scalar("SELECT COUNT(*) FROM curated_memories_fts;"));
        Assert.Null(exception);

        Assert.Equal(3L, Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('curated_ai','curated_ad','curated_au');"));
        Assert.Equal(1L, Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='curated_memories';"));

        // Re-initializing the same file is idempotent.
        Assert.Null(Record.Exception(() => _ = new AppDatabase(_dbPath)));
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsNewestUpdatedFirst()
    {
        var oldest = MakeMemory(content: "first written", updatedAt: Timestamp);
        var newest = MakeMemory(content: "last written", updatedAt: Timestamp.AddHours(2));
        var middle = MakeMemory(content: "in between", updatedAt: Timestamp.AddHours(1));
        await _store.AddAsync(oldest);
        await _store.AddAsync(newest);
        await _store.AddAsync(middle);

        var byNullQuery = await _store.SearchAsync("ws-1", null, null, null, 10);
        var byEmptyQuery = await _store.SearchAsync("ws-1", "", null, null, 10);
        var byWhitespaceQuery = await _store.SearchAsync("ws-1", "   ", null, null, 10);

        AssertSuccess(byNullQuery);
        Assert.Equal(new[] { newest.Id, middle.Id, oldest.Id }, byNullQuery.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(byEmptyQuery);
        Assert.Equal(new[] { newest.Id, middle.Id, oldest.Id }, byEmptyQuery.Value!.Select(m => m.Id).ToArray());
        AssertSuccess(byWhitespaceQuery);
        Assert.Equal(new[] { newest.Id, middle.Id, oldest.Id }, byWhitespaceQuery.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Search_Limit_Respected()
    {
        for (var i = 0; i < 5; i++)
            await _store.AddAsync(MakeMemory(content: $"limit probe {i}", updatedAt: Timestamp.AddMinutes(i)));

        var limited = await _store.SearchAsync("ws-1", null, null, null, 3);
        var all = await _store.SearchAsync("ws-1", null, null, null, 10);

        AssertSuccess(limited);
        Assert.Equal(3, limited.Value!.Count);
        AssertSuccess(all);
        Assert.Equal(5, all.Value!.Count);
        // Limit keeps the newest-updated prefix of the unbounded ordering.
        Assert.Equal(all.Value!.Take(3).Select(m => m.Id), limited.Value!.Select(m => m.Id));
    }

    [Fact]
    public async Task Content_AtMaxBoundary_RoundTrips_AndRemainsSearchable()
    {
        var body = string.Concat("boundary ", new string('x', CuratedMemorySpecifications.MaxContentChars - 18), " zebrafin");
        Assert.Equal(CuratedMemorySpecifications.MaxContentChars, body.Length);
        var memory = MakeMemory(content: body);

        var added = await _store.AddAsync(memory);
        var fetched = await _store.GetAsync(memory.Id);

        AssertSuccess(added);
        Assert.Equal(memory, added.Value);
        AssertSuccess(fetched);
        Assert.NotNull(fetched.Value);
        Assert.Equal(CuratedMemorySpecifications.MaxContentChars, fetched.Value!.Content.Length);
        Assert.Equal(body, fetched.Value.Content);

        var searched = await _store.SearchAsync("ws-1", "zebrafin", null, null, 10);
        AssertSuccess(searched);
        Assert.Equal(new[] { memory.Id }, searched.Value!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ReopenedDatabase_AllRowsStillSearchable()
    {
        var first = MakeMemory(content: "durable note about indexing", category: MemoryCategory.Reference);
        var second = MakeMemory(workspaceId: "ws-2", content: "another durable note about vacuums");
        await _store.AddAsync(first);
        await _store.AddAsync(second);

        var reopenedStore = new SqliteCuratedMemoryStore(new AppDatabase(_dbPath));

        var fetched = await reopenedStore.GetAsync(first.Id);
        AssertSuccess(fetched);
        Assert.NotNull(fetched.Value);
        AssertSameFields(first, fetched.Value!);

        var searched = await reopenedStore.SearchAsync("ws-1", "durable indexing", null, null, 10);
        AssertSuccess(searched);
        Assert.Equal(new[] { first.Id }, searched.Value!.Select(m => m.Id).ToArray());

        var otherWorkspace = await reopenedStore.SearchAsync("ws-2", "vacuums", null, null, 10);
        AssertSuccess(otherWorkspace);
        Assert.Equal(new[] { second.Id }, otherWorkspace.Value!.Select(m => m.Id).ToArray());

        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM curated_memories;"));
    }
}
