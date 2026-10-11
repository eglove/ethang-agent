using System.Globalization;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed persistence for a provider's model catalog: one row per
///     provider holding the serialized entries and the fetch timestamp, so the model
///     picker and smart-selection previews serve instantly from disk between app runs
///     and revalidate in the background. Provider-scoped (not workspace-scoped) - the
///     catalog is one global list per provider. Expected failures flow as DomainErrors
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
      List<ModelProviderEntry>? entries;
      try
      {
        entries = JsonSerializer.Deserialize<List<ModelProviderEntry>>(payload);
      }
      catch (JsonException ex)
      {
        return Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheCorrupt",
            $"The persisted catalog for provider '{provider}' could not be parsed: {ex.Message}"));
      }

      return entries is null
          ? Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheCorrupt",
              $"The persisted catalog for provider '{provider}' parsed to null."))
          : Result.Success(new ModelCatalogSnapshot(entries, fetchedAt));
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
    string payload = JsonSerializer.Serialize(snapshot.Entries);
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

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the model catalog cache could not complete the operation: {ex.Message}");
}
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
