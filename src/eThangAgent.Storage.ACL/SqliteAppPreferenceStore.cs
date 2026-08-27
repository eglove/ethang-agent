using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed <see cref="IAppPreferenceStore"/> over the shared app database.
///     Rows are app-scoped (no workspace key) and keyed by preference name.</summary>
public sealed class SqliteAppPreferenceStore(AppDatabase database) : IAppPreferenceStore
{
  private readonly AppDatabase _db = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<string?> GetAsync(string key, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(key);

#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT value FROM app_preferences WHERE key = $key;";
    _ = command.Parameters.AddWithValue("$key", key);
    object? raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return raw is null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
  }

  public async Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    ArgumentException.ThrowIfNullOrWhiteSpace(value);

#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText =
        """
        INSERT INTO app_preferences (key, value, updated_at)
        VALUES ($key, $value, $updated)
        ON CONFLICT(key) DO UPDATE SET value = $value, updated_at = $updated;
        """;
    _ = command.Parameters.AddWithValue("$key", key);
    _ = command.Parameters.AddWithValue("$value", value);
    _ = command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
  }
}
