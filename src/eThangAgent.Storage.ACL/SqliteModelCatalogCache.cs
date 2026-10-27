using System.Globalization;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed persistence for a provider's model catalog: one row per
///     provider holding the serialized entries and the fetch timestamp, so the model
///     picker and smart-selection previews serve instantly from disk between app runs
///     and revalidate in the background. Source-scoped (not workspace-scoped) - the
///     provider column stores the ACL's opaque provider-plus-source identity. Expected failures flow as DomainErrors
///     through Result; a storage fault surfaces StorageUnavailable and a corrupt
///     payload CatalogCacheCorrupt, never an exception.</summary>
// Named decision (CA1001): process-lifetime singleton owned by the composition root.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class SqliteModelCatalogCache(AppDatabase database) : IModelCatalogCache
{
  private const string SelectSql =
      "SELECT payload_json, fetched_at FROM model_catalog_cache WHERE provider = @provider;";

  private const string UpsertSql =
      "INSERT INTO model_catalog_cache (provider, payload_json, fetched_at) VALUES (@provider, @payload, @fetched) " +
      "ON CONFLICT(provider) DO UPDATE SET payload_json = excluded.payload_json, fetched_at = excluded.fetched_at;";

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public async Task<Result<ModelCatalogSnapshot>> ReadAsync(string provider, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(provider);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = SelectSql;
      _ = command.Parameters.AddWithValue("@provider", provider);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      if (!await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        return Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheMiss",
            $"No persisted catalog is stored for provider '{provider}'."));
      }

      string payload = reader.GetString(0);
      DateTimeOffset fetchedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
      ModelCatalogSnapshot? snapshot;
      try
      {
        snapshot = Deserialize(payload);
      }
      catch (JsonException ex)
      {
        return Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheCorrupt",
            $"The persisted catalog for provider '{provider}' could not be parsed: {ex.Message}"));
      }

      if (snapshot is null)
      {
        return Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheCorrupt",
            $"The persisted catalog for provider '{provider}' parsed to null."));
      }

      // A LEGACY SNAPSHOT IS A MISS, NOT A HIT (issue #296 follow-up). It was written before
      // endpoint variants existed, so every entry deserializes with the unspecified variant and
      // collapses back onto the pair key - serving it would show the user the exact collapsed
      // list variants were added to fix, for as long as the TTL lasts. Refetching is the honest
      // answer, and the caller already treats a miss as 'fetch'.
      return snapshot.HasCurrentShape
          ? Result.Success(snapshot with { FetchedAt = fetchedAt })
          : Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheStaleShape",
              $"The persisted catalog for provider '{provider}' predates endpoint variants "
              + $"(shape {snapshot.ShapeVersion}, need {ModelCatalogSnapshot.CurrentShapeVersion}); refetching."));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ModelCatalogSnapshot>(Unavailable(ex));
    }
  }

  public async Task<Result<bool>> WriteAsync(string provider, ModelCatalogSnapshot snapshot, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(provider);
    ArgumentNullException.ThrowIfNull(snapshot);
    string payload = Serialize(snapshot);
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = UpsertSql;
      _ = command.Parameters.AddWithValue("@provider", provider);
      _ = command.Parameters.AddWithValue("@payload", payload);
      _ = command.Parameters.AddWithValue("@fetched", snapshot.FetchedAt.ToString("O", CultureInfo.InvariantCulture));
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<bool>(Unavailable(ex));
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  /// <summary>The persisted envelope: the entries plus the shape version they were written
  ///     with. An ENVELOPE rather than a bare array so a future entry-shape change can be
  ///     detected instead of silently misread - the legacy array is still read, and reports
  ///     shape 0 so the caller can tell it apart.
  ///     <para>WHY NOT A SCHEMA MIGRATION: the payload is a cache, not a record. A stale cache
  ///     entry has no history worth preserving, so invalidating it costs one refetch, while a
  ///     migration would have to rewrite JSON in place and could still be wrong about what the
  ///     old rows meant.</para></summary>
  private static string Serialize(ModelCatalogSnapshot snapshot)
      => JsonSerializer.Serialize(new PersistedCatalog(
          ModelCatalogSnapshot.CurrentShapeVersion, snapshot.Entries));

  /// <summary>Reads either the current envelope or the legacy bare array, reporting the shape
  ///     each one carries.</summary>
  private static ModelCatalogSnapshot? Deserialize(string payload)
  {
    using JsonDocument document = JsonDocument.Parse(payload);
    if (document.RootElement.ValueKind == JsonValueKind.Array)
    {
      List<ModelProviderEntry>? legacy = JsonSerializer.Deserialize<List<ModelProviderEntry>>(payload);
      return legacy is null
          ? null
          : new ModelCatalogSnapshot(legacy, DateTimeOffset.MinValue,
              ModelCatalogSnapshot.LegacyShapeVersion);
    }

    PersistedCatalog? stored = JsonSerializer.Deserialize<PersistedCatalog>(payload);
    return stored?.Entries is null
        ? null
        : new ModelCatalogSnapshot(stored.Entries, DateTimeOffset.MinValue, stored.ShapeVersion);
  }

  /// <summary>The persisted payload shape. Internal so the storage ACL owns its own wire form
  ///     while the domain owns the contract.</summary>
  private sealed record PersistedCatalog(int ShapeVersion, IReadOnlyList<ModelProviderEntry> Entries);

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the model catalog cache could not complete the operation: {ex.Message}");
}
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
