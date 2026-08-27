using System.Globalization;
using eThangAgent.StateDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed implementation of <see cref="IProviderExclusionStore"/>.
/// Exclusions are keyed by model+provider pair and workspace. Expired entries are
/// purged opportunistically on read.</summary>
public sealed class SqliteProviderExclusionStore(AppDatabase database, IWorkspaceContext workspace) : IProviderExclusionStore
{
  private readonly AppDatabase _db = database ?? throw new ArgumentNullException(nameof(database));
  private readonly IWorkspaceContext _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

  public async Task<IReadOnlySet<string>> GetActiveExclusionsAsync(CancellationToken ct = default)
  {
    string now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    HashSet<string> keys = [];
#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007

    using (SqliteCommand purge = connection.CreateCommand())
    {
      purge.CommandText = "DELETE FROM provider_exclusions WHERE workspace_id = $ws AND expires_at < $now;";
      _ = purge.Parameters.AddWithValue("$ws", _workspace.WorkspaceId);
      _ = purge.Parameters.AddWithValue("$now", now);
      _ = await purge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    using (SqliteCommand select = connection.CreateCommand())
    {
      select.CommandText = "SELECT model_provider_key FROM provider_exclusions WHERE workspace_id = $ws;";
      _ = select.Parameters.AddWithValue("$ws", _workspace.WorkspaceId);
      using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        _ = keys.Add(reader.GetString(0));
      }
    }
    return keys;
  }

  public async Task<bool> AddExclusionAsync(string modelProviderKey, TimeSpan ttl, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(modelProviderKey))
    {
      return false;
    }

    DateTimeOffset expiresAt = DateTimeOffset.UtcNow + ttl;
    string now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText =
      """
      INSERT INTO provider_exclusions (model_provider_key, workspace_id, expires_at, created_at)
      VALUES ($key, $ws, $expires, $created)
      ON CONFLICT(model_provider_key, workspace_id) DO UPDATE SET expires_at = $expires, created_at = $created;
      """;
    _ = command.Parameters.AddWithValue("$key", modelProviderKey);
    _ = command.Parameters.AddWithValue("$ws", _workspace.WorkspaceId);
    _ = command.Parameters.AddWithValue("$expires", expiresAt.ToString("o", CultureInfo.InvariantCulture));
    _ = command.Parameters.AddWithValue("$created", now);
    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
  }

  public async Task<bool> RemoveExclusionAsync(string modelProviderKey, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(modelProviderKey))
    {
      return false;
    }

#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "DELETE FROM provider_exclusions WHERE model_provider_key = $key AND workspace_id = $ws;";
    _ = command.Parameters.AddWithValue("$key", modelProviderKey);
    _ = command.Parameters.AddWithValue("$ws", _workspace.WorkspaceId);
    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
  }
}
