using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed append-only audit trail of watchdog decisions. Rows are written
///     best-effort by the watchdog; retry attempts are derived here by counting
///     RetrySpawned rows per agent.</summary>
public sealed class SqliteWatchdogEventStore(AppDatabase database) : IWatchdogEventStore
{
  private readonly AppDatabase _db = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(evt);
#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "INSERT INTO watchdog_events (id, agent_id, kind, detail, attempt, rss_mb, created_at) VALUES ($id, $agent, $kind, $detail, $attempt, $rss, $created);";
    _ = command.Parameters.AddWithValue("$id", evt.Id.ToString());
    _ = command.Parameters.AddWithValue("$agent", evt.AgentId?.Value.ToString() ?? (object)DBNull.Value);
    _ = command.Parameters.AddWithValue("$kind", evt.Kind.ToString());
    _ = command.Parameters.AddWithValue("$detail", evt.Detail);
    _ = command.Parameters.AddWithValue("$attempt", evt.Attempt);
    _ = command.Parameters.AddWithValue("$rss", evt.RssMb ?? (object)DBNull.Value);
    _ = command.Parameters.AddWithValue("$created", evt.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    return Result.Success(evt.Id.ToString());
  }

  public async Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
  {
#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT id, agent_id, kind, detail, attempt, rss_mb, created_at FROM watchdog_events ORDER BY created_at DESC LIMIT $limit;";
    _ = command.Parameters.AddWithValue("$limit", limit);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    List<WatchdogEvent> events = [];
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      if (ReadEvent(reader) is { } evt)
      {
        events.Add(evt);
      }
    }

    return Result.Success<IReadOnlyList<WatchdogEvent>>(events);
  }

  public async Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
  {
#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM watchdog_events WHERE agent_id = $agent AND kind = $kind;";
    _ = command.Parameters.AddWithValue("$agent", agentId.Value.ToString());
    _ = command.Parameters.AddWithValue("$kind", kind.ToString());
    object? scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return Result.Success(Convert.ToInt32(scalar, CultureInfo.InvariantCulture));
  }

  /// <summary>Reads one row; a kind string that no longer parses is schema corruption and
  ///     surfaces as a skipped row rather than a silently defaulted one.</summary>
  private static WatchdogEvent? ReadEvent(SqliteDataReader reader)
  {
    Guid id = Guid.Parse(reader.GetString(0));
    string? agentRaw = reader.IsDBNull(1) ? null : reader.GetString(1);
    AgentId? agentId = agentRaw is null ? null : new AgentId(Guid.Parse(agentRaw));
    if (!Enum.TryParse(reader.GetString(2), out WatchdogEventKind kind))
    {
      return null;
    }

    string detail = reader.GetString(3);
    int attempt = reader.GetInt32(4);
    double? rssMb = reader.IsDBNull(5) ? null : reader.GetDouble(5);
    DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
    return new WatchdogEvent(id, agentId, kind, detail, attempt, rssMb, createdAt);
  }
}
