using System.Globalization;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>The usage ledger's read model (issue #24) over the append-only
///     <c>agent_events</c> rows the loop already writes — no new table, no migration.
///     <para>SESSION ATTRIBUTION IS A RECURSIVE WALK (B11): the CTE starts at the root
///     session id and follows <c>agents.parent_id</c> down, then joins the usage rows onto
///     that subtree. A child stamps its OWN id as its session id, so a
///     <c>session_id = root</c> filter would omit every subagent's spend — the walk is
///     what makes a child's rows count toward the session that caused them, and it works
///     retroactively for rows already written.</para>
///     <para>The root id seeds the CTE DIRECTLY rather than by selecting it from
///     <c>agents</c>: a session whose own row is missing (an interrupted first write) must
///     still report the rows it did record, and <c>UNION</c> (not <c>UNION ALL</c>) keeps a
///     corrupt parent cycle from looping forever.</para>
///     <para>Reads run on the READ-ONLY connection: a rollup can never mutate the ledger it
///     measures. A busy timeout is raised first, because the app writes while this reads
///     and an instant "database is locked" would surface as a status-bar error.</para>
///     <para>Payload parsing is TOLERANT BY DESIGN: a row written before the price, class,
///     model, or attribution members existed reads with them absent, and the legacy rule
///     (a null class is <c>main_loop</c>) is applied here — the one place a stored payload
///     becomes a <see cref="UsageRow"/>.</para></summary>
public sealed class SqliteUsageQueries(AppDatabase database) : IUsageQueries
{
  /// <summary>The persisted wire name of an ordinary loop call: what a row written before
  ///     the call class existed means. Kept as a literal here because the closed set is
  ///     owned by the Agent Domain, which this ACL's read path must not depend on.</summary>
  private const string MainLoopClass = "main_loop";

  private const string BusyTimeoutCommand = "PRAGMA busy_timeout = 2000;";
  private const string QueryFailedCode = "UsageQueryFailed";

  /// <summary>The subtree walk plus the usage join. Parameterized on the root id only.</summary>
  private const string SubtreeUsageSql =
      @"WITH RECURSIVE subtree(id) AS (
            SELECT @root
            UNION
            SELECT a.id FROM agents a JOIN subtree s ON a.parent_id = s.id
        )
        SELECT e.payload_json
        FROM agent_events e
        JOIN subtree s ON e.agent_id = s.id
        WHERE e.type = 'usage'
        ORDER BY e.id;";

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<UsageSummary>> SessionSummaryAsync(string sessionId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.OpenReadOnly();
#pragma warning restore CA2007
      await RaiseBusyTimeoutAsync(connection, ct).ConfigureAwait(false);
      List<UsageRow> rows = await ReadRowsAsync(connection, sessionId, ct).ConfigureAwait(false);
      return Result.Success(Rollup(rows));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<UsageSummary>(new DomainError(QueryFailedCode, ex.Message));
    }
  }

  /// <summary>Builds the summary from the rows in scope: the overall totals, one total per
  ///     call class, and one per model. The cost arithmetic is the Model Domain's single
  ///     formula (<see cref="UsageCost"/>), never a second implementation in SQL.</summary>
  private static UsageSummary Rollup(List<UsageRow> rows)
  {
    if (rows.Count == 0)
    {
      return UsageSummary.Empty;
    }

    UsageTally overall = new();
    Dictionary<string, UsageTally> byClass = new(StringComparer.Ordinal);
    Dictionary<string, UsageTally> byModel = new(StringComparer.Ordinal);
    foreach (UsageRow row in rows)
    {
      overall.Add(row);
      TallyFor(byClass, row.CallClass).Add(row);
      // A row whose model could not be named still belongs to the session's spend, so it
      // is grouped under a stable empty key and reported with a null model id - never
      // dropped, and never attributed to a model that did not run.
      TallyFor(byModel, row.ModelId ?? string.Empty).Add(row);
    }

    return new UsageSummary(overall.Totals,
        [.. byClass.Select(entry => new UsageClassTotal(entry.Key, entry.Value.Totals))],
        [.. byModel.Select(entry => new UsageModelTotal(
            entry.Key.Length == 0 ? null : entry.Key, entry.Value.Totals))]);
  }

  private static UsageTally TallyFor(Dictionary<string, UsageTally> tallies, string key)
  {
    if (!tallies.TryGetValue(key, out UsageTally? tally))
    {
      tally = new UsageTally();
      tallies[key] = tally;
    }

    return tally;
  }

  private static async Task<List<UsageRow>> ReadRowsAsync(SqliteConnection connection, string rootId,
      CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
    command.CommandText = SubtreeUsageSql;
    _ = command.Parameters.AddWithValue("@root", rootId);
    List<UsageRow> rows = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      if (ParseRow(reader.GetString(0)) is { } row)
      {
        rows.Add(row);
      }
    }

    return rows;
  }

  /// <summary>Maps one stored payload onto the read model. Null when the payload is not a
  ///     usage object at all (a corrupt row is skipped, never rendered as zero spend).</summary>
  private static UsageRow? ParseRow(string payloadJson)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(payloadJson);
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      // A null class reads as main_loop: every row written before the class existed came
      // from the loop, so null has exactly one correct meaning.
      string callClass = String(root, "CallClass") ?? MainLoopClass;
      return new UsageRow(callClass,
          String(root, "ModelId"),
          String(root, "Provider"),
          String(root, "WorkspaceId"),
          Integer(root, "InputTokens") ?? 0,
          Integer(root, "OutputTokens") ?? 0,
          Integer(root, "CachedInputTokens"),
          Integer(root, "ReasoningTokens"),
          Decimal(root, "PromptPricePerToken"),
          Decimal(root, "PromptCacheReadPricePerToken"),
          Decimal(root, "CompletionPricePerToken"));
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static string? String(JsonElement element, string name)
      => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
          ? value.GetString()
          : null;

  private static int? Integer(JsonElement element, string name)
      => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
          && value.TryGetInt32(out int parsed)
          ? parsed
          : null;

  /// <summary>A price as stored. A string value is accepted too: a payload written by a
  ///     serializer configured for invariant-culture decimals stores the number as text,
  ///     and refusing it would silently unpriced every such row.</summary>
  private static decimal? Decimal(JsonElement element, string name)
      => element.TryGetProperty(name, out JsonElement value) ? ParseDecimal(value) : null;

  /// <summary>One JSON value as a price, or null when it is not a number or a numeric
  ///     string. Everything else (absent, null, boolean, object, array) is "unknown".</summary>
  private static decimal? ParseDecimal(JsonElement value)
      => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)
          ? number
          : ParseDecimalText(value);

  /// <summary>The text form of a stored price, or null when the value is not a string that
  ///     parses as one.</summary>
  private static decimal? ParseDecimalText(JsonElement value)
      => value.ValueKind == JsonValueKind.String
          && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture,
              out decimal parsed)
          ? parsed
          : null;
  /// <summary>Raises the read connection's busy timeout so a rollup that lands mid-write
  ///     waits for the file lock instead of failing instantly.</summary>
  private static async Task RaiseBusyTimeoutAsync(SqliteConnection connection, CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand busy = connection.CreateCommand();
#pragma warning restore CA2007
    busy.CommandText = BusyTimeoutCommand;
    _ = await busy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }
}
