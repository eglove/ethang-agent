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

  /// <summary>The most rows one performance read will scan (issue #295). A cap exists
  ///     because the ledger grows without bound and a projection that took a second to open a
  ///     picker would be worse than one resting on the newest rows; when it bites, the report
  ///     SAYS SO (<see cref="ProviderPerformanceReport.Truncated"/>) instead of presenting a
  ///     partial read as complete history.</summary>
  private const int PerformanceRowCap = 20000;

  /// <summary>The 90th percentile, as the reliability signal's percentile (issue #295).</summary>
  private const double SlowTailPercentile = 0.9;

  /// <summary>One workspace's usage rows: the stamped workspace when the row carries one, else
  ///     the workspace of the agent that wrote it. A legacy row with no stamped workspace still
  ///     belongs to the session that produced it, so it is attributed through the agent row
  ///     rather than dropped. <c>json_valid</c> guards the extraction: a corrupt payload is
  ///     skipped by the reader anyway, and it must not fail the whole query on its way there.
  ///     </summary>
  private const string WorkspaceUsageSql =
      @"SELECT e.payload_json
        FROM agent_events e
        LEFT JOIN agents a ON a.id = e.agent_id
        WHERE e.type = 'usage'
          AND json_valid(e.payload_json)
          AND (json_extract(e.payload_json, '$.WorkspaceId') = @scope
               OR (json_extract(e.payload_json, '$.WorkspaceId') IS NULL AND a.workspace_id = @scope))
        ORDER BY e.id DESC
        LIMIT @cap;";

  /// <summary>Every usage row in the ledger, newest first, capped.</summary>
  private const string GlobalUsageSql =
      @"SELECT payload_json
        FROM agent_events
        WHERE type = 'usage' AND json_valid(payload_json)
        ORDER BY id DESC
        LIMIT @cap;";

  /// <summary>The subtree walk, newest first, capped — the session rung of the fallback chain.
  ///     </summary>
  private const string SubtreeUsageCappedSql =
      @"WITH RECURSIVE subtree(id) AS (
            SELECT @scope
            UNION
            SELECT a.id FROM agents a JOIN subtree s ON a.parent_id = s.id
        )
        SELECT e.payload_json
        FROM agent_events e
        JOIN subtree s ON e.agent_id = s.id
        WHERE e.type = 'usage'
        ORDER BY e.id DESC
        LIMIT @cap;";

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

  /// <summary>Observed per-provider performance (issue #295): the median call duration, the
  ///     median first-token latency, the median output rate, and the token counts the cache-hit
  ///     rate derives from — per model/provider pair, with the sample count behind each figure.
  ///     <para>THE FALLBACK CHAIN IS SESSION, THEN WORKSPACE, THEN GLOBAL (D5), and the report
  ///     names the rung that ANSWERED. A session with no rows of its own consults its
  ///     workspace's history next, because a fresh session still benefits from what this
  ///     workspace already learned about a provider; answering from a wider scope silently
  ///     would present another session's history as this one's.</para>
  ///     <para>THE AGGREGATION IS C# OVER THE SAME ROWS THE SUMMARY READS, never a second SQL
  ///     implementation: the medians, the zero-output exclusion, and the hit rate are the Model
  ///     Domain's pure functions (<see cref="UsageStatistics"/>), so the number the picker shows
  ///     and the number the status bar shows cannot disagree — the same reason
  ///     <see cref="UsageCost"/> is one function.</para>
  ///     <para>Reads run on the READ-ONLY connection, like every other member here.</para></summary>
  public async Task<Result<ProviderPerformanceReport>> ProviderPerformanceAsync(
      ProviderPerformanceRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.SessionId is null && request.WorkspaceId is null)
    {
      return Result.Failure<ProviderPerformanceReport>(new DomainError(QueryFailedCode,
          "A performance query needs a session id, a workspace id, or both."));
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.OpenReadOnly();
#pragma warning restore CA2007
      await RaiseBusyTimeoutAsync(connection, ct).ConfigureAwait(false);
      foreach ((UsageScope scope, string? id) in ScopeChain(request))
      {
        List<UsageRow> rows = await ReadScopedRowsAsync(connection, scope, id, ct).ConfigureAwait(false);
        if (rows.Count > 0)
        {
          return Result.Success(Aggregate(scope, rows, rows.Count >= PerformanceRowCap));
        }
      }

      // Nothing measured anywhere in the chain. That is an ANSWER, not a failure: a first-ever
      // session has no history, and a surface renders 'not measured' rather than an error.
      return Result.Success(ProviderPerformanceReport.Empty(UsageScope.Global));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ProviderPerformanceReport>(new DomainError(QueryFailedCode, ex.Message));
    }
  }

  /// <summary>The scopes to try, in precedence order, skipping any whose id is absent (D5).
  ///     The chain is fixed here rather than caller-supplied: two callers choosing different
  ///     chains could show the same figure attributed to different histories.</summary>
  private static IEnumerable<(UsageScope Scope, string? Id)> ScopeChain(ProviderPerformanceRequest request)
  {
    if (request.SessionId is { } sessionId)
    {
      yield return (UsageScope.Session, sessionId);
    }

    if (request.WorkspaceId is { } workspaceId)
    {
      yield return (UsageScope.Workspace, workspaceId);
    }

    yield return (UsageScope.Global, null);
  }

  /// <summary>Reads the rows for one rung of the chain, newest first and capped.</summary>
  private static async Task<List<UsageRow>> ReadScopedRowsAsync(SqliteConnection connection,
      UsageScope scope, string? id, CancellationToken ct)
  {
    string sql = scope switch
    {
      UsageScope.Session => SubtreeUsageCappedSql,
      UsageScope.Workspace => WorkspaceUsageSql,
      UsageScope.Global => GlobalUsageSql,
      _ => throw new ArgumentOutOfRangeException(nameof(scope), scope,
          "Unknown usage scope."),
    };
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
    command.CommandText = sql;
    if (id is not null)
    {
      _ = command.Parameters.AddWithValue("@scope", id);
    }

    _ = command.Parameters.AddWithValue("@cap", PerformanceRowCap);
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

  /// <summary>Turns one scope's rows into the report: the scope-wide hit rate, then one
  ///     measurement per model/provider pair, ordered by model then provider so the same rows
  ///     always produce the same order.</summary>
  private static ProviderPerformanceReport Aggregate(UsageScope scope, List<UsageRow> rows, bool truncated)
  {
    long inputTokens = rows.Sum(row => (long)row.InputTokens);
    long cachedTokens = rows.Sum(row => (long)(row.CachedInputTokens ?? 0));
    int unmeasured = rows.Count(row => row.DurationMs is null);
    List<ProviderPerformance> providers =
    [
        .. rows
            .GroupBy(row => (row.ModelId, row.ObservedProvider))
            .OrderBy(group => group.Key.ModelId ?? "\uFFFF", StringComparer.Ordinal)
            .ThenBy(group => group.Key.ObservedProvider ?? "\uFFFF", StringComparer.Ordinal)
            .Select(group => Measure(group.Key.ModelId, group.Key.ObservedProvider, [.. group])),
    ];
    return new ProviderPerformanceReport(scope, UsageStatistics.HitRate(cachedTokens, inputTokens),
        inputTokens, cachedTokens, rows.Count, unmeasured, providers, truncated);
  }

  /// <summary>One pair's measurements. Every figure is a MEDIAN over the calls that could
  ///     express it, and each carries its own sample count, because a median over three calls
  ///     and one over three thousand are different facts (D1).</summary>
  private static ProviderPerformance Measure(string? modelId, string? providerName, List<UsageRow> rows)
  {
    List<double> durations = [.. rows.Where(row => row.DurationMs is not null)
        .Select(row => (double)row.DurationMs!.Value)];
    List<double> firstTokens = [.. rows.Where(row => row.FirstTokenMs is not null)
        .Select(row => (double)row.FirstTokenMs!.Value)];
    // D3: a call that produced no output has no rate, and one nothing timed has none either.
    List<double> rates = [.. rows
        .Select(row => UsageStatistics.OutputTokensPerSecond(row.OutputTokens, row.DurationMs))
        .Where(rate => rate is not null)
        .Select(rate => rate!.Value)];
    return new ProviderPerformance(modelId, providerName,
        UsageStatistics.Median(durations),
        UsageStatistics.Percentile(durations, SlowTailPercentile),
        UsageStatistics.Median(firstTokens),
        UsageStatistics.Median(rates),
        durations.Count, firstTokens.Count, rates.Count,
        rows.Count(row => row.DurationMs is null),
        rows.Count,
        rows.Sum(row => (long)row.InputTokens),
        rows.Sum(row => (long)(row.CachedInputTokens ?? 0)));
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
          Decimal(root, "CompletionPricePerToken"),
          String(root, "RequestedModelId"),
          String(root, "ServedModelId"),
          // Issue #292: timing is read the same tolerant way every other member is - a row
          // written before it existed reads as unmeasured, never as zero.
          Long(root, "DurationMs"),
          Long(root, "FirstTokenMs"),
          String(root, "ServedProvider"));
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

  /// <summary>A 64-bit count as stored, or null when absent or not an integer. Separate from
  ///     <see cref="Integer"/> because a duration in milliseconds can exceed int's range on a
  ///     long call, and truncating one would report a duration that never happened.</summary>
  private static long? Long(JsonElement element, string name)
      => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
          && value.TryGetInt64(out long parsed)
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
