using System.Globalization;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteCuratedMemoryStoreTests : IDisposable
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
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
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
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): test helper runs test-authored constant SQL only.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  [Fact]
  public async Task Add_ThenGet_RoundTripsEveryField_ProvenanceNullAndSet()
  {
    CuratedMemory withProvenance = MakeMemory(
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
    CuratedMemory withoutProvenance = MakeMemory(
        workspaceId: "",
        scope: MemoryScope.Global,
        category: MemoryCategory.Preference,
        tags: ["style"],
        content: "Prefer explicit over clever.",
        usageHint: null,
        provenanceSession: null,
        version: 3);

    Result<CuratedMemory> addedWith = await _store.AddAsync(withProvenance, ct: TestContext.Current.CancellationToken);
    Result<CuratedMemory> addedWithout = await _store.AddAsync(withoutProvenance, ct: TestContext.Current.CancellationToken);
    Result<CuratedMemory?> fetchedWith = await _store.GetAsync(withProvenance.Id, ct: TestContext.Current.CancellationToken);
    Result<CuratedMemory?> fetchedWithout = await _store.GetAsync(withoutProvenance.Id, ct: TestContext.Current.CancellationToken);

    AssertSuccess(addedWith);
    Assert.Equal(withProvenance, addedWith.Value);
    AssertSuccess(addedWithout);
    Assert.Equal(withoutProvenance, addedWithout.Value);

    AssertSuccess(fetchedWith);
    CuratedMemory? storedWith = fetchedWith.Value;
    Assert.NotNull(storedWith);
    Assert.Equal(withProvenance.Id, storedWith.Id);
    Assert.Equal("ws-a", storedWith.WorkspaceId);
    Assert.Equal(MemoryCategory.Failure, storedWith.Category);
    Assert.Equal(["api", "sql"], storedWith.Tags);
    Assert.Equal("Retry with backoff when the API rate-limits.", storedWith.Content);
    Assert.Equal("Cite when writing retry logic.", storedWith.UsageHint);
    Assert.Equal(MemoryScope.Workspace, storedWith.Scope);
    Assert.Equal("session-abc123", storedWith.ProvenanceSession);
    Assert.Equal(1, storedWith.Version);
    Assert.Equal(Timestamp, storedWith.CreatedAt);
    Assert.Equal(Timestamp.AddMinutes(1), storedWith.UpdatedAt);

    AssertSuccess(fetchedWithout);
    CuratedMemory? storedWithout = fetchedWithout.Value;
    Assert.NotNull(storedWithout);
    AssertSameFields(withoutProvenance, storedWithout);
    Assert.Null(storedWithout.ProvenanceSession);
    Assert.Null(storedWithout.UsageHint);
  }

  [Fact]
  public async Task Search_Visibility_GlobalVisibleViaAnyWorkspace_WorkspaceRowOnlyViaMatchingId()
  {
    CuratedMemory globalRow = MakeMemory(workspaceId: "", scope: MemoryScope.Global, content: "global rule about deploys");
    CuratedMemory wsARow = MakeMemory(workspaceId: "ws-a", scope: MemoryScope.Workspace, content: "workspace rule about deploys");
    _ = await _store.AddAsync(globalRow, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(wsARow, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> viaOwner = await _store.SearchAsync("ws-a", null, null, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> viaOther = await _store.SearchAsync("ws-b", null, null, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> viaNull = await _store.SearchAsync(null, null, null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(viaOwner);
    Assert.Equal(
        new[] { globalRow.Id, wsARow.Id }.OrderBy(_ => _),
        viaOwner.Value!.Select(m => m.Id).OrderBy(_ => _));
    AssertSuccess(viaOther);
    Assert.Equal([globalRow.Id], [.. viaOther.Value!.Select(m => m.Id)]);
    AssertSuccess(viaNull);
    Assert.Equal([globalRow.Id], [.. viaNull.Value!.Select(m => m.Id)]);

    // Both ways: a workspace-scoped query term surfaces the owner's row and never another workspace's.
    Result<IReadOnlyList<CuratedMemory>> ownerHit = await _store.SearchAsync("ws-a", "workspace", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(ownerHit);
    Assert.Equal([wsARow.Id], [.. ownerHit.Value!.Select(m => m.Id)]);
    Result<IReadOnlyList<CuratedMemory>> otherHit = await _store.SearchAsync("ws-b", "workspace", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(otherHit);
    Assert.Empty(otherHit.Value!);
  }

  [Fact]
  public async Task Search_Query_FtsHitRanksMatchingRowFirst_NonMatchingExcluded()
  {
    CuratedMemory strong = MakeMemory(content: "quantum flux calibration quantum flux alignment quantum");
    CuratedMemory weak = MakeMemory(content: "a note about quantum mechanics");
    CuratedMemory unrelated = MakeMemory(content: "gardening tips for spring");
    _ = await _store.AddAsync(strong, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(weak, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(unrelated, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> searched = await _store.SearchAsync("ws-1", "quantum", null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(searched);
    Assert.Equal([strong.Id, weak.Id], [.. searched.Value!.Select(m => m.Id)]);
    Assert.DoesNotContain(unrelated.Id, searched.Value!.Select(m => m.Id));
  }

  [Fact]
  public async Task Search_MultiTokenQuery_RequiresAllTokens()
  {
    CuratedMemory both = MakeMemory(content: "alpha beta deployment");
    CuratedMemory onlyAlpha = MakeMemory(content: "alpha gamma deployment");
    _ = await _store.AddAsync(both, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(onlyAlpha, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> searched = await _store.SearchAsync("ws-1", "alpha beta", null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(searched);
    Assert.Equal([both.Id], [.. searched.Value!.Select(m => m.Id)]);
  }

  [Fact]
  public async Task Search_QueryWithFtsSpecialChars_ExecutesSafely_AndMatchesNothing()
  {
    _ = await _store.AddAsync(MakeMemory(content: "safe content about migrations"), ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> injection = await _store.SearchAsync("ws-1", "test; DROP", null, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> embeddedQuote = await _store.SearchAsync("ws-1", "a\"b", null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(injection);
    Assert.Empty(injection.Value!);
    AssertSuccess(embeddedQuote);
    Assert.Empty(embeddedQuote.Value!);

    // The store is unharmed: normal search still works after the hostile input.
    Result<IReadOnlyList<CuratedMemory>> stillWorking = await _store.SearchAsync("ws-1", "migrations", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(stillWorking);
    _ = Assert.Single(stillWorking.Value!);
  }

  [Fact]
  public async Task Search_CategoryFilter_MatchesExactly()
  {
    CuratedMemory convention = MakeMemory(category: MemoryCategory.Convention, content: "naming conventions");
    CuratedMemory insight = MakeMemory(category: MemoryCategory.Insight, content: "insight about caching");
    _ = await _store.AddAsync(convention, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(insight, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> conventions = await _store.SearchAsync("ws-1", null, MemoryCategory.Convention, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> insights = await _store.SearchAsync("ws-1", null, MemoryCategory.Insight, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> any = await _store.SearchAsync("ws-1", null, null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(conventions);
    Assert.Equal([convention.Id], [.. conventions.Value!.Select(m => m.Id)]);
    AssertSuccess(insights);
    Assert.Equal([insight.Id], [.. insights.Value!.Select(m => m.Id)]);
    AssertSuccess(any);
    Assert.Equal(2, any.Value!.Count);

    // The filter also constrains FTS mode.
    Result<IReadOnlyList<CuratedMemory>> conventionsViaQuery = await _store.SearchAsync("ws-1", "caching", MemoryCategory.Convention, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(conventionsViaQuery);
    Assert.Empty(conventionsViaQuery.Value!);
    Result<IReadOnlyList<CuratedMemory>> insightsViaQuery = await _store.SearchAsync("ws-1", "caching", MemoryCategory.Insight, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(insightsViaQuery);
    Assert.Equal([insight.Id], [.. insightsViaQuery.Value!.Select(m => m.Id)]);
  }

  [Fact]
  public async Task Search_TagsFilter_RowFoundBySubsetAndFullSet_NotByUnknownTag()
  {
    CuratedMemory tagged = MakeMemory(tags: ["api", "sql"], content: "connection pooling advice");
    CuratedMemory untagged = MakeMemory(tags: ["style"], content: "style guidance");
    _ = await _store.AddAsync(tagged, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(untagged, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> bySql = await _store.SearchAsync("ws-1", null, null, ["sql"], 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byApiAndSql = await _store.SearchAsync("ws-1", null, null, ["api", "sql"], 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byNope = await _store.SearchAsync("ws-1", null, null, ["nope"], 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(bySql);
    Assert.Equal([tagged.Id], [.. bySql.Value!.Select(m => m.Id)]);
    AssertSuccess(byApiAndSql);
    Assert.Equal([tagged.Id], [.. byApiAndSql.Value!.Select(m => m.Id)]);
    AssertSuccess(byNope);
    Assert.Empty(byNope.Value!);

    // An empty tags list imposes no constraint.
    Result<IReadOnlyList<CuratedMemory>> byEmpty = await _store.SearchAsync("ws-1", null, null, [], 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(byEmpty);
    Assert.Equal(2, byEmpty.Value!.Count);
  }

  [Fact]
  public async Task Search_TagsFilter_MatchesWholeJsonElements_NotSubstrings()
  {
    // "mysql" contains "sql" and "apidocs" contains "api": a bare substring
    // LIKE over the serialized JSON array would return wrong rows as hits.
    CuratedMemory mysql = MakeMemory(tags: ["mysql"], content: "mysql connection advice");
    CuratedMemory apidocs = MakeMemory(tags: ["apidocs"], content: "api documentation advice");
    _ = await _store.AddAsync(mysql, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(apidocs, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> bySql = await _store.SearchAsync("ws-1", null, null, ["sql"], 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byApi = await _store.SearchAsync("ws-1", null, null, ["api"], 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byMySql = await _store.SearchAsync("ws-1", null, null, ["mysql"], 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byApiDocs = await _store.SearchAsync("ws-1", null, null, ["apidocs"], 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(bySql);
    Assert.Empty(bySql.Value!);
    AssertSuccess(byApi);
    Assert.Empty(byApi.Value!);
    AssertSuccess(byMySql);
    Assert.Equal([mysql.Id], [.. byMySql.Value!.Select(m => m.Id)]);
    AssertSuccess(byApiDocs);
    Assert.Equal([apidocs.Id], [.. byApiDocs.Value!.Select(m => m.Id)]);
  }

  [Fact]
  public async Task Search_RecencyMode_EqualTimestamps_OrderedByDeterministicIdTiebreaker()
  {
    // Six rows share one timestamp; insertion order is the REVERSE of stored-id
    // order, so only an explicit id tiebreaker — not arrival order — can yield
    // a deterministic sequence.
    List<Guid> idsAscending = [.. Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).OrderBy(g => g.ToString(), StringComparer.Ordinal)];
    foreach (Guid id in Enumerable.Reverse(idsAscending))
    {
      _ = await _store.AddAsync(MakeMemory(id: id, content: "tiebreaker probe"), ct: TestContext.Current.CancellationToken);
    }

    Result<IReadOnlyList<CuratedMemory>> searched = await _store.SearchAsync("ws-1", null, null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(searched);
    Assert.Equal(idsAscending, searched.Value!.Select(m => m.Id).ToArray());
  }

  [Fact]
  public async Task Update_HappyPath_BumpsVersionAndUpdatedAt_HistoryFree()
  {
    CuratedMemory original = MakeMemory(content: "old advice about indexes", tags: ["sql"]);
    _ = await _store.AddAsync(original, ct: TestContext.Current.CancellationToken);
    CuratedMemory updated = original with
    {
      Content = "new advice about covering indexes",
      Tags = ["sql", "perf"],
      Version = 2,
      UpdatedAt = Timestamp.AddHours(1),
    };

    Result<CuratedMemory> result = await _store.UpdateAsync(updated, ct: TestContext.Current.CancellationToken);

    AssertSuccess(result);
    Assert.Equal(updated, result.Value);

    Result<CuratedMemory?> fetched = await _store.GetAsync(original.Id, ct: TestContext.Current.CancellationToken);
    AssertSuccess(fetched);
    Assert.Equal(2, fetched.Value!.Version);
    Assert.Equal("new advice about covering indexes", fetched.Value.Content);
    Assert.Equal(["sql", "perf"], fetched.Value.Tags);
    Assert.Equal(original.CreatedAt, fetched.Value.CreatedAt);
    Assert.Equal(Timestamp.AddHours(1), fetched.Value.UpdatedAt);

    // History-free: still exactly one row, and the FTS index tracks the new content only.
    Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM curated_memories WHERE id = '{original.Id}';"));
    Result<IReadOnlyList<CuratedMemory>> oldTerm = await _store.SearchAsync("ws-1", "old", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(oldTerm);
    Assert.Empty(oldTerm.Value!);
    Result<IReadOnlyList<CuratedMemory>> newTerm = await _store.SearchAsync("ws-1", "covering", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(newTerm);
    Assert.Equal([updated.Id], [.. newTerm.Value!.Select(m => m.Id)]);
  }

  [Fact]
  public async Task Update_StaleVersion_FailsVersionConflict_NamingCurrentVersion()
  {
    CuratedMemory original = MakeMemory(content: "versioned note");
    _ = await _store.AddAsync(original, ct: TestContext.Current.CancellationToken);
    Result<CuratedMemory> current = await _store.UpdateAsync(original with { Content = "second write", Version = 2 }, ct: TestContext.Current.CancellationToken);
    AssertSuccess(current);
    CuratedMemory stale = original with { Content = "stale write", Version = 1 };

    Result<CuratedMemory> result = await _store.UpdateAsync(stale, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(CuratedMemoryErrors.VersionConflict, result.Error.Code);
    Assert.Contains("2", result.Error.Message, StringComparison.Ordinal);

    // The conflicting write changed nothing.
    Result<CuratedMemory?> fetched = await _store.GetAsync(original.Id, ct: TestContext.Current.CancellationToken);
    Assert.Equal("second write", fetched.Value!.Content);
    Assert.Equal(2, fetched.Value.Version);
  }

  [Fact]
  public async Task Update_UnknownMemory_FailsMemoryNotFound()
  {
    CuratedMemory ghost = MakeMemory(content: "never added");

    Result<CuratedMemory> result = await _store.UpdateAsync(ghost, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(CuratedMemoryErrors.MemoryNotFound, result.Error.Code);
  }

  [Fact]
  public async Task Delete_RemovesRowAndFtsEntry_UnknownReturnsFalse()
  {
    CuratedMemory doomed = MakeMemory(content: "ephemeral zebra note");
    _ = await _store.AddAsync(doomed, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> before = await _store.SearchAsync("ws-1", "zebra", null, null, 10, ct: TestContext.Current.CancellationToken);
    _ = Assert.Single(before.Value!);

    Result<bool> deleted = await _store.DeleteAsync(doomed.Id, ct: TestContext.Current.CancellationToken);

    AssertSuccess(deleted);
    Assert.True(deleted.Value);
    Assert.Null((await _store.GetAsync(doomed.Id, ct: TestContext.Current.CancellationToken)).Value);
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM curated_memories;"));
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM curated_memories_fts;"));

    Result<IReadOnlyList<CuratedMemory>> after = await _store.SearchAsync("ws-1", "zebra", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(after);
    Assert.Empty(after.Value!);

    Result<bool> unknown = await _store.DeleteAsync(Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
    AssertSuccess(unknown);
    Assert.False(unknown.Value);
  }

  [Fact]
  public void Migration_CreatesFtsIndexAndTriggers_CountSucceedsPostInit()
  {
    Exception? exception = Record.Exception(() => Scalar("SELECT COUNT(*) FROM curated_memories_fts;"));
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
    CuratedMemory oldest = MakeMemory(content: "first written", updatedAt: Timestamp);
    CuratedMemory newest = MakeMemory(content: "last written", updatedAt: Timestamp.AddHours(2));
    CuratedMemory middle = MakeMemory(content: "in between", updatedAt: Timestamp.AddHours(1));
    _ = await _store.AddAsync(oldest, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(newest, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(middle, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<CuratedMemory>> byNullQuery = await _store.SearchAsync("ws-1", null, null, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byEmptyQuery = await _store.SearchAsync("ws-1", "", null, null, 10, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> byWhitespaceQuery = await _store.SearchAsync("ws-1", "   ", null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(byNullQuery);
    Assert.Equal([newest.Id, middle.Id, oldest.Id], [.. byNullQuery.Value!.Select(m => m.Id)]);
    AssertSuccess(byEmptyQuery);
    Assert.Equal([newest.Id, middle.Id, oldest.Id], [.. byEmptyQuery.Value!.Select(m => m.Id)]);
    AssertSuccess(byWhitespaceQuery);
    Assert.Equal([newest.Id, middle.Id, oldest.Id], [.. byWhitespaceQuery.Value!.Select(m => m.Id)]);
  }

  [Fact]
  public async Task Search_Limit_Respected()
  {
    for (int i = 0; i < 5; i++)
    {
      _ = await _store.AddAsync(MakeMemory(content: $"limit probe {i}", updatedAt: Timestamp.AddMinutes(i)), ct: TestContext.Current.CancellationToken);
    }

    Result<IReadOnlyList<CuratedMemory>> limited = await _store.SearchAsync("ws-1", null, null, null, 3, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<CuratedMemory>> all = await _store.SearchAsync("ws-1", null, null, null, 10, ct: TestContext.Current.CancellationToken);

    AssertSuccess(limited);
    Assert.Equal(3, limited.Value!.Count);
    AssertSuccess(all);
    Assert.Equal(5, all.Value!.Count);
    // Limit keeps the newest-updated prefix of the unbounded ordering.
    Assert.Equal(all.Value.Take(3).Select(m => m.Id), limited.Value.Select(m => m.Id));
  }

  [Fact]
  public async Task Content_AtMaxBoundary_RoundTrips_AndRemainsSearchable()
  {
    string body = string.Concat("boundary ", new string('x', CuratedMemorySpecifications.MaxContentChars - 18), " zebrafin");
    Assert.Equal(CuratedMemorySpecifications.MaxContentChars, body.Length);
    CuratedMemory memory = MakeMemory(content: body);

    Result<CuratedMemory> added = await _store.AddAsync(memory, ct: TestContext.Current.CancellationToken);
    Result<CuratedMemory?> fetched = await _store.GetAsync(memory.Id, ct: TestContext.Current.CancellationToken);

    AssertSuccess(added);
    Assert.Equal(memory, added.Value);
    AssertSuccess(fetched);
    Assert.NotNull(fetched.Value);
    Assert.Equal(CuratedMemorySpecifications.MaxContentChars, fetched.Value.Content.Length);
    Assert.Equal(body, fetched.Value.Content);

    Result<IReadOnlyList<CuratedMemory>> searched = await _store.SearchAsync("ws-1", "zebrafin", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(searched);
    Assert.Equal([memory.Id], [.. searched.Value!.Select(m => m.Id)]);
  }

  [Fact]
  public async Task ReopenedDatabase_AllRowsStillSearchable()
  {
    CuratedMemory first = MakeMemory(content: "durable note about indexing", category: MemoryCategory.Reference);
    CuratedMemory second = MakeMemory(workspaceId: "ws-2", content: "another durable note about vacuums");
    _ = await _store.AddAsync(first, ct: TestContext.Current.CancellationToken);
    _ = await _store.AddAsync(second, ct: TestContext.Current.CancellationToken);

    SqliteCuratedMemoryStore reopenedStore = new(new AppDatabase(_dbPath));

    Result<CuratedMemory?> fetched = await reopenedStore.GetAsync(first.Id, ct: TestContext.Current.CancellationToken);
    AssertSuccess(fetched);
    Assert.NotNull(fetched.Value);
    AssertSameFields(first, fetched.Value);

    Result<IReadOnlyList<CuratedMemory>> searched = await reopenedStore.SearchAsync("ws-1", "durable indexing", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(searched);
    Assert.Equal([first.Id], [.. searched.Value!.Select(m => m.Id)]);

    Result<IReadOnlyList<CuratedMemory>> otherWorkspace = await reopenedStore.SearchAsync("ws-2", "vacuums", null, null, 10, ct: TestContext.Current.CancellationToken);
    AssertSuccess(otherWorkspace);
    Assert.Equal([second.Id], [.. otherWorkspace.Value!.Select(m => m.Id)]);

    Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM curated_memories;"));
  }
}
