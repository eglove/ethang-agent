using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed consented-link persistence: one row per link, keyed by
///     (workspace_id, name) with replace-by-name upserts (W2). Synchronous by design — the
///     registry's consent decisions are synchronous and local SQLite writes are fast; the
///     write gate serializes writers just like SqliteMailboxStore. Storage faults surface as
///     Result failures, never exceptions, so the consent door can render them.</summary>
// Named decision (CA1001): process-lifetime singleton owned by the composition root.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class SqliteLinkStore(AppDatabase database) : ILinkStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public Result<IReadOnlyList<StoredLink>> List(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    try
    {
      using SqliteConnection connection = _database.Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          SELECT name, container, agent_address, created_at FROM agent_links
          WHERE workspace_id = $ws
          ORDER BY created_at DESC, name;
          """;
      _ = command.Parameters.AddWithValue("$ws", workspaceId);
      List<StoredLink> links = [];
      using SqliteDataReader reader = command.ExecuteReader();
      while (reader.Read())
      {
        links.Add(new StoredLink(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
      }

      return Result.Success<IReadOnlyList<StoredLink>>(links);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<StoredLink>>(Unavailable(ex));
    }
  }

  public Result<string> Upsert(string workspaceId, StoredLink link)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentNullException.ThrowIfNull(link);
    ArgumentException.ThrowIfNullOrWhiteSpace(link.Name);
    _writeGate.Wait();
    try
    {
      using SqliteConnection connection = _database.Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          INSERT INTO agent_links (workspace_id, name, container, agent_address, created_at)
          VALUES ($ws, $name, $container, $address, $created)
          ON CONFLICT(workspace_id, name) DO UPDATE SET
              container = excluded.container,
              agent_address = excluded.agent_address,
              created_at = excluded.created_at;
          """;
      AddLink(command, workspaceId, link);
      _ = command.ExecuteNonQuery();
      return Result.Success(link.Name);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<string>(Unavailable(ex));
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  public Result<bool> Delete(string workspaceId, string name)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    _writeGate.Wait();
    try
    {
      using SqliteConnection connection = _database.Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "DELETE FROM agent_links WHERE workspace_id = $ws AND name = $name;";
      _ = command.Parameters.AddWithValue("$ws", workspaceId);
      _ = command.Parameters.AddWithValue("$name", name);
      return Result.Success(command.ExecuteNonQuery() > 0);
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

  private static void AddLink(SqliteCommand command, string workspaceId, StoredLink link)
  {
    _ = command.Parameters.AddWithValue("$ws", workspaceId);
    _ = command.Parameters.AddWithValue("$name", link.Name);
    _ = command.Parameters.AddWithValue("$container", link.Container);
    _ = command.Parameters.AddWithValue("$address", link.AgentAddress);
    _ = command.Parameters.AddWithValue("$created", link.LinkedAt.ToString("o", CultureInfo.InvariantCulture));
  }

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the link store could not complete the operation: {ex.Message}");
}
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
