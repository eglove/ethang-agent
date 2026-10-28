using System.Globalization;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The persisted model-catalog cache (V17): round-trips entries and the fetch
///     timestamp, misses with a named error, refuses to serve a corrupt payload, and
///     overwrites on re-write. The migration adds the table without touching earlier
///     data.</summary>
public sealed class SqliteModelCatalogCacheTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-catcache-{Guid.NewGuid():N}.db");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  private static ModelProviderEntry Entry(string modelId, string? provider, decimal prompt, decimal completion) =>
      new(modelId, provider, prompt, null, completion, 128_000, 4096, true, false, true, 80.0, 70.0, 60.0, 1.5, 90.0,
          "desc");

  [Fact]
  public async Task ReadAsync_UnknownProvider_ReturnsCatalogCacheMiss()
  {
    AppDatabase db = new(_dbPath);
    SqliteModelCatalogCache cache = new(db);

    Result<ModelCatalogSnapshot> result = await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogCacheMiss", result.Error.Code);
  }

  [Fact]
  public async Task WriteThenRead_RoundTripsEntriesAndTimestamp()
  {
    AppDatabase db = new(_dbPath);
    SqliteModelCatalogCache cache = new(db);
    DateTimeOffset fetchedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    ModelCatalogSnapshot snapshot = new(
        [Entry("google/gemini", "Google", 0.000001m, 0.000002m), Entry("meta/llama", "Meta", 0.0000005m, 0.0000008m)],
        fetchedAt);

    Result<bool> write = await cache.WriteAsync("openrouter", snapshot, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(write.IsSuccess);

    Result<ModelCatalogSnapshot> read = await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(read.IsSuccess);
    Assert.Equal(fetchedAt, read.Value.FetchedAt);
    Assert.Equal(2, read.Value.Entries.Count);
    ModelProviderEntry gemini = Assert.Single(read.Value.Entries, e => e.ModelId == "google/gemini");
    Assert.Equal("Google", gemini.ProviderName);
    Assert.Equal(0.000001m, gemini.PromptPricePerToken);
    Assert.Equal(0.000002m, gemini.CompletionPricePerToken);
    Assert.Equal(128_000, gemini.ContextLength);
    Assert.Equal(80.0, gemini.IntelligenceScore);
  }

  [Fact]
  public async Task WriteAsync_SecondWrite_ReplacesSnapshot()
  {
    AppDatabase db = new(_dbPath);
    SqliteModelCatalogCache cache = new(db);
    _ = await cache.WriteAsync("openrouter",
        new ModelCatalogSnapshot([Entry("a/one", "A", 1m, 2m)], DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    DateTimeOffset newer = DateTimeOffset.UtcNow.AddMinutes(5);
    _ = await cache.WriteAsync("openrouter",
        new ModelCatalogSnapshot([Entry("b/two", "B", 3m, 4m), Entry("c/three", "C", 5m, 6m)], newer),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<ModelCatalogSnapshot> read = await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(read.IsSuccess);
    Assert.Equal(2, read.Value.Entries.Count);
    Assert.Equal(newer, read.Value.FetchedAt);
    Assert.DoesNotContain(read.Value.Entries, e => e.ModelId == "a/one");
  }

  [Fact]
  public async Task ReadAsync_CorruptPayload_ReturnsCatalogCacheCorrupt()
  {
    AppDatabase db = new(_dbPath);
    using (SqliteConnection connection = db.Open())
    {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText =
          "INSERT INTO model_catalog_cache (provider, payload_json, fetched_at) VALUES ('openrouter', 'not json', $t);";
      _ = command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
      _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    SqliteModelCatalogCache cache = new(db);
    Result<ModelCatalogSnapshot> read = await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(read.IsSuccess);
    Assert.Equal("CatalogCacheCorrupt", read.Error.Code);
  }

  [Fact]
  public async Task ReadAsync_PayloadWrittenBeforeVariants_IsAMiss_NotACollapsedCatalog()
  {
    // A LEGACY ARRAY PAYLOAD IS REFUSED (issue #296 follow-up). It was written before endpoint
    // variants existed, so every entry in it deserializes with the unspecified variant and
    // collapses back onto the pair key - five grok-4.7 endpoints become one row. Serving it
    // would show the user the exact collapsed list variants were added to fix, for as long as
    // the TTL lasts, so the cache reports it as stale and the caller refetches.
    const string legacyPayload =
        "[{\"ModelId\":\"google/gemini\",\"ProviderName\":\"Google\",\"PromptPricePerToken\":0.000001," +
        "\"PromptCacheReadPricePerToken\":null,\"CompletionPricePerToken\":0.000002,\"ContextLength\":128000," +
        "\"MaxCompletionTokens\":4096,\"SupportsToolUse\":true,\"SupportsVision\":false," +
        "\"SupportsTextOutput\":true,\"IntelligenceScore\":80.0,\"CodingScore\":70.0," +
        "\"AgenticScore\":60.0,\"LatencyMs\":null,\"ThroughputTokensPerSec\":null," +
        "\"Description\":\"desc\",\"IsRouter\":false}]";
    AppDatabase db = new(_dbPath);
    using (SqliteConnection connection = db.Open())
    {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText =
          "INSERT INTO model_catalog_cache (provider, payload_json, fetched_at) VALUES ('openrouter', $p, $t);";
      _ = command.Parameters.AddWithValue("$p", legacyPayload);
      _ = command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
      _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    SqliteModelCatalogCache cache = new(db);
    Result<ModelCatalogSnapshot> read =
        await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(read.IsSuccess);
    Assert.Equal("CatalogCacheStaleShape", read.Error.Code);
  }

  [Fact]
  public async Task ReadAsync_EntryMissingTheNewerMembers_DeserializesWithThemDefaulted()
  {
    // ACCEPTANCE CRITERION (issue #293), kept: the newer members are nullable/defaulted, so an
    // entry that omits them still deserializes. The versioned envelope is what makes this
    // distinguishable from a payload that predates VARIANTS - here the shape is current, so the
    // snapshot is served, and the absent members stay absent rather than being falsely
    // populated.
    const string sparseEnvelope =
        "{\"ShapeVersion\":1,\"Entries\":[{\"ModelId\":\"google/gemini\",\"ProviderName\":\"Google\"," +
        "\"PromptPricePerToken\":0.000001,\"PromptCacheReadPricePerToken\":null," +
        "\"CompletionPricePerToken\":0.000002,\"ContextLength\":128000,\"MaxCompletionTokens\":4096," +
        "\"SupportsToolUse\":true,\"SupportsVision\":false,\"SupportsTextOutput\":true," +
        "\"IntelligenceScore\":80.0,\"CodingScore\":70.0,\"AgenticScore\":60.0,\"LatencyMs\":null," +
        "\"ThroughputTokensPerSec\":null,\"Description\":\"desc\",\"IsRouter\":false}]}";
    AppDatabase db = new(_dbPath);
    using (SqliteConnection connection = db.Open())
    {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText =
          "INSERT INTO model_catalog_cache (provider, payload_json, fetched_at) VALUES ('openrouter', $p, $t);";
      _ = command.Parameters.AddWithValue("$p", sparseEnvelope);
      _ = command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
      _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    SqliteCatalogCacheResult read = await ReadLegacyAsync(db);

    Assert.True(read.IsSuccess);
    ModelProviderEntry entry = Assert.Single(read.Entries);
    Assert.Equal(0.000001m, entry.PromptPricePerToken);
    // The newer members are ABSENT, not falsely populated.
    Assert.Null(entry.UptimeLast30m);
    Assert.Null(entry.SupportsImplicitCaching);
    Assert.Null(entry.PriceOverrides);
    Assert.False(entry.HasPriceOverrides);
    // An entry with no variant is the unspecified one, and its key falls back to the pair.
    Assert.True(entry.Variant.IsUnspecified);
    Assert.Equal("google/gemini:Google", entry.Key);
    // And the price resolution still works: the base prices apply.
    Assert.Equal(0.000001m, entry.PricesFor(1_000_000).PromptPricePerToken);
  }

  [Fact]
  public async Task WriteThenRead_PreservesVariantIdentity()
  {
    // The round trip that matters for the picker: five endpoints of one provider stay five
    // rows, each with its own routable slug.
    AppDatabase db = new(_dbPath);
    SqliteModelCatalogCache cache = new(db);
    ModelProviderEntry[] entries =
    [
      VariantEntry("xai"),
      VariantEntry("xai/zdr"),
      VariantEntry("xai/zdr/us"),
      VariantEntry("xai/priority"),
      VariantEntry("xai/zdr/priority"),
    ];
    _ = await cache.WriteAsync("openrouter",
        new ModelCatalogSnapshot(entries, DateTimeOffset.UtcNow, ModelCatalogSnapshot.CurrentShapeVersion),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<ModelCatalogSnapshot> read =
        await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(read.IsSuccess);
    Assert.Equal(5, read.Value.Entries.Count);
    Assert.Equal(5, read.Value.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
    Assert.Equal("xai/zdr/us",
        read.Value.Entries.Single(e => e.Variant.Slug == "xai/zdr/us").RoutingSlug);
  }

  private static ModelProviderEntry VariantEntry(string tag) =>
      Entry("x-ai/grok-4.7", "xAI", 0.000002m, 0.000006m) with
      {
        Variant = EndpointVariant.Parse(tag, null, null),
      };

  private static async Task<SqliteCatalogCacheResult> ReadLegacyAsync(AppDatabase db)
  {
    SqliteModelCatalogCache cache = new(db);
    Result<ModelCatalogSnapshot> read =
        await cache.ReadAsync("openrouter", TestContext.Current.CancellationToken).ConfigureAwait(true);
    return read.IsSuccess
        ? new SqliteCatalogCacheResult(true, read.Value.Entries, null)
        : new SqliteCatalogCacheResult(false, [], read.Error.Code);
  }

  private sealed record SqliteCatalogCacheResult(bool IsSuccess, IReadOnlyList<ModelProviderEntry> Entries,
      string? ErrorCode);

  [Fact]
  public async Task V17_AddsTable_LeavingEarlierRowsUntouched()
  {
    // Build a fully-migrated database with a real row, then drop the V17 table and
    // stamp user_version 16 so the constructor's migration re-applies it.
    AppDatabase db = new(_dbPath);
    using (SqliteConnection connection = db.Open())
    {
      using SqliteCommand seed = connection.CreateCommand();
      seed.CommandText = "INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at) " +
          "VALUES ('ws', 'ns', 'seed', 'v', 1, '2026-01-01T00:00:00Z');";
      _ = await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      // A V16 database still carried plan_steps.todo_id, which V24 drops.
      LegacyPlanStepsShape.Restore(connection);
      using SqliteCommand drop = connection.CreateCommand();
      drop.CommandText = "DROP TABLE IF EXISTS model_catalog_cache; PRAGMA user_version = 16;";
      _ = await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    AppDatabase migrated = new(_dbPath); // constructor migrates

    using SqliteConnection check = migrated.Open();
    using SqliteCommand version = check.CreateCommand();
    version.CommandText = "PRAGMA user_version;";
    Assert.Equal(AppDatabase.LatestSchemaVersion, Convert.ToInt32(await version.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), CultureInfo.InvariantCulture));
    using SqliteCommand table = check.CreateCommand();
    table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='model_catalog_cache';";
    Assert.Equal(1L, Convert.ToInt64(await table.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), CultureInfo.InvariantCulture));
    using SqliteCommand earlier = check.CreateCommand();
    earlier.CommandText = "SELECT COUNT(*) FROM state_keys;";
    Assert.Equal(1L, Convert.ToInt64(await earlier.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), CultureInfo.InvariantCulture));
  }
}
