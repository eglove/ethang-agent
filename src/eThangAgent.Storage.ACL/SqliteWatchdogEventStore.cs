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

  /// <summary>How long an identical event coalesces into the row already there. Inside the
  ///     window the occurrences advance and the row's id does not change; outside it, a new row
  ///     starts, so a condition that keeps recurring over hours still leaves a trail of when it
  ///     began rather than one row with a number nobody can place in time.</summary>
  public static readonly TimeSpan CoalescingWindow = TimeSpan.FromHours(1);

  /// <summary>Records one event, coalescing it onto the row that already carries the same
  ///     (agent, kind, detail) when one was seen inside <see cref="CoalescingWindow"/>.
  ///     <para>THE SAME EVENT REPEATED IS ONE FACT: one orphan-repair line was written 596
  ///     times, and a retention sweep cannot fix that - it would delete the older 500 and leave
  ///     the 596th to be written tomorrow. Advancing a count and a last-seen stamp on the row
  ///     that is already there is what makes the table's distinct-to-total ratio describe the
  ///     events rather than the clock.</para>
  ///     <para>THE MATCH IS THE WHOLE IDENTITY, agent included: with a null agent_id the
  ///     process-scope rows (RSS breaches, sustained usage) coalesce among themselves, and an
  ///     agent-scoped row never absorbs another agent's.</para>
  ///     <para>THE ID RETURNED IS THE ROW THAT NOW HOLDS THE EVENT, which is the existing row's
  ///     when it coalesced. A caller that records the returned id records where the event
  ///     actually lives.</para></summary>
  /// <param name="evt">The event to record.</param>
  /// <param name="ct">Cancellation for the write.</param>
  public async Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(evt);
#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    string created = evt.CreatedAt.ToString("o", CultureInfo.InvariantCulture);
    string cutoff = evt.CreatedAt.Subtract(CoalescingWindow).ToString("o", CultureInfo.InvariantCulture);

    // One statement, so the lookup and the write cannot be split by a concurrent append: the
    // UPDATE either finds the row the coalescing index names or it does not, and the INSERT that
    // follows a miss is the same row a second writer would have updated.
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        UPDATE watchdog_events
           SET occurrences = occurrences + 1, last_seen_at = $created
         WHERE agent_id IS $agent AND kind = $kind AND detail = $detail AND last_seen_at >= $cutoff;
        """;
    Add(command, evt, created);
    _ = command.Parameters.AddWithValue("$cutoff", cutoff);
    if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0)
    {
      return Result.Success(await CoalescedRowIdAsync(connection, evt, cutoff, ct).ConfigureAwait(false));
    }

    using SqliteCommand insert = connection.CreateCommand();
    insert.CommandText = "INSERT INTO watchdog_events (id, agent_id, kind, detail, attempt, rss_mb, created_at, occurrences, last_seen_at) VALUES ($id, $agent, $kind, $detail, $attempt, $rss, $created, 1, $created);";
    _ = insert.Parameters.AddWithValue("$id", evt.Id.ToString());
    Add(insert, evt, created);
    _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    return Result.Success(evt.Id.ToString());
  }

  /// <summary>The id of the row an event coalesced onto: the one the coalescing index names,
  ///     newest first, which is the row the update just advanced.</summary>
  private static async Task<string> CoalescedRowIdAsync(SqliteConnection connection, WatchdogEvent evt,
      string cutoff, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        SELECT id FROM watchdog_events
         WHERE agent_id IS $agent AND kind = $kind AND detail = $detail
           AND last_seen_at >= $cutoff
         ORDER BY last_seen_at DESC LIMIT 1;
        """;
    Add(command, evt, created: string.Empty);
    _ = command.Parameters.AddWithValue("$cutoff", cutoff);
    return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string id ? id : evt.Id.ToString();
  }

  /// <summary>Binds the columns every statement here matches on. The agent is bound as a
  ///     parameter or as NULL, which is what makes 'IS $agent' match both cases.</summary>
  private static void Add(SqliteCommand command, WatchdogEvent evt, string created)
  {
    _ = command.Parameters.AddWithValue("$agent", evt.AgentId?.Value.ToString() ?? (object)DBNull.Value);
    _ = command.Parameters.AddWithValue("$kind", evt.Kind.ToString());
    _ = command.Parameters.AddWithValue("$detail", evt.Detail);
    if (created.Length > 0)
    {
      _ = command.Parameters.AddWithValue("$created", created);
      _ = command.Parameters.AddWithValue("$attempt", evt.Attempt);
      _ = command.Parameters.AddWithValue("$rss", evt.RssMb ?? (object)DBNull.Value);
    }
  }

  public async Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
  {
    if (limit < 0)
    {
      // Strict boundary: SQLite would read a negative LIMIT as unbounded.
      return Result.Failure<IReadOnlyList<WatchdogEvent>>(new DomainError("InvalidLimit",
          "limit must not be negative."));
    }

#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT id, agent_id, kind, detail, attempt, rss_mb, created_at, occurrences, last_seen_at FROM watchdog_events ORDER BY COALESCE(last_seen_at, created_at) DESC LIMIT $limit;";
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

  public async Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
      WatchdogEventKind kind, int limit, CancellationToken ct = default)
  {
    if (limit < 0)
    {
      // Strict boundary: SQLite would read a negative LIMIT as unbounded.
      return Result.Failure<IReadOnlyList<WatchdogEvent>>(new DomainError("InvalidLimit",
          "limit must not be negative."));
    }

#pragma warning disable CA2007
    await using SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT id, agent_id, kind, detail, attempt, rss_mb, created_at, occurrences, last_seen_at FROM watchdog_events WHERE kind = $kind ORDER BY COALESCE(last_seen_at, created_at) DESC LIMIT $limit;";
    _ = command.Parameters.AddWithValue("$kind", kind.ToString());
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
    command.CommandText = "SELECT COALESCE(SUM(occurrences), 0) FROM watchdog_events WHERE agent_id = $agent AND kind = $kind;";
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
    // A row written before the count existed holds 1, which is what it was. The default keeps
    // that meaning readable here rather than making every caller handle a null count.
    int occurrences = reader.IsDBNull(7) ? 1 : reader.GetInt32(7);
    DateTimeOffset? lastSeen = reader.IsDBNull(8)
        ? null
        : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
    return new WatchdogEvent(id, agentId, kind, detail, attempt, rssMb, createdAt, occurrences, lastSeen);
  }
}
