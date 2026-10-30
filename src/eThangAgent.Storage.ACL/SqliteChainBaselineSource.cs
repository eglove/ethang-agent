using System.Globalization;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>The subagent era's own rows, measured as the comparison baseline (issue #309 D3).
///     <para>WHY THIS READS THE DATABASE DIRECTLY: #305 retired the in-process subagent runtime
///     and #316 retired the messaging surface, so there is no live subagent path left to run.
///     What survives is the RECORD — child agent rows with start/completion clocks and a usage
///     ledger — and measuring that is a real baseline rather than a reconstruction.</para>
///     <para>IT IS DELIBERATELY UNSCOPED, and the scope is REPORTED rather than assumed. Measured
///     on this machine 2026-10-30: all 2,156 child agent rows carry a NULL workspace id, because
///     #305's stamping fix landed long after those runs. A workspace-scoped read would therefore
///     be empty in every workspace, and the report would claim there was nothing to compare
///     against. So this tries the workspace first and FALLS BACK to the global set, naming which
///     answered.</para>
///     <para>EVERY FIGURE IS A MEDIAN, and a run that cannot express one is EXCLUDED and
///     COUNTED. A child row with no completion clock has no duration; a run whose calls could not
///     be priced has no cost. Filling either in with zero would compare chains against a number
///     nobody measured — the <c>UsageStatistics</c> rule, delegated to rather than reimplemented.
///     </para></summary>
public sealed class SqliteChainBaselineSource(AppDatabase database, string workspaceId)
    : IChainBaselineSource
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = workspaceId ?? string.Empty;

  /// <summary>The baseline runs: every CHILD agent row, which is what the subagent era's work
  ///     actually was.
  ///     <para><c>parent_id IS NOT NULL</c> IS THE DEFINITION, not a filter of convenience: a
  ///     root row is a conversation, and a conversation is what the CHAIN era also has —
  ///     counting roots would compare the two eras' sessions against each other instead of
  ///     comparing their delegated work.</para>
  ///     <para>THE STATUS FILTER IS ABSENT ON PURPOSE: a failed run is still a run that was
  ///     started, and the started-versus-completed distinction is carried in the counts rather
  ///     than applied as a filter. A baseline of only successful runs would flatter itself.
  ///     </para></summary>
  private const string RunSql =
      @"SELECT a.id, a.status, a.created_at, a.completed_at
        FROM agents a
        WHERE a.parent_id IS NOT NULL
          AND (@ws = '' OR a.workspace_id = @ws)
        ORDER BY a.id;";

  /// <summary>The usage rows belonging to those runs, priced with the ledger's own formula.
  ///     <para>SEPARATE FROM <see cref="RunSql"/> because the two facts have different
  ///     cardinalities: one run has many calls, and joining them would multiply the run rows and
  ///     make a median duration depend on how talkative a run was.</para></summary>
  private const string CostSql =
      @"WITH RECURSIVE era(id) AS (
            SELECT id FROM agents WHERE parent_id IS NOT NULL AND (@ws = '' OR workspace_id = @ws)
            UNION
            SELECT a.id FROM agents a JOIN era e ON a.parent_id = e.id
        )
        SELECT e.agent_id, e.payload_json
        FROM agent_events e
        JOIN era x ON e.agent_id = x.id
        WHERE e.type = 'usage' AND json_valid(e.payload_json)
        ORDER BY e.id;";

  public async Task<Result<ChainTelemetryBaseline?>> ReadAsync(CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.OpenReadOnly();
#pragma warning restore CA2007
      foreach (string scope in new[] { _workspaceId, string.Empty })
      {
        Result<ChainTelemetryBaseline?> measured = await ReadScopeAsync(connection, scope, ct)
            .ConfigureAwait(false);
        if (!measured.IsSuccess || measured.Value is not null)
        {
          return measured;
        }
      }

      // Nothing anywhere: an ANSWER, not a failure. The report says INCOMPARABLE rather than
      // asserting a ratio it could not compute.
      return Result.Success<ChainTelemetryBaseline?>(null);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ChainTelemetryBaseline?>(new DomainError("StorageUnavailable",
          $"the subagent baseline could not be read: {ex.Message}"));
    }
  }

  /// <summary>One scope's baseline, or null when that scope holds no run.
  ///     <para>THE WORKSPACE SCOPE IS TRIED FIRST and the global set only when it is empty, so a
  ///     workspace that DOES have stamped subagent rows is compared against its own history
  ///     rather than against everyone else's.</para></summary>
  private static async Task<Result<ChainTelemetryBaseline?>> ReadScopeAsync(SqliteConnection connection,
      string scope, CancellationToken ct)
  {
    List<BaselineRun> runs = await ReadRunsAsync(connection, scope, ct).ConfigureAwait(false);
    if (runs.Count == 0)
    {
      return Result.Success<ChainTelemetryBaseline?>(null);
    }

    Dictionary<string, decimal> costs = await ReadCostsAsync(connection, scope, ct)
        .ConfigureAwait(false);
    return Result.Success<ChainTelemetryBaseline?>(Measure(runs, costs));
  }

  /// <summary>The runs in one scope.
  ///     <para>A row whose status is not a DEFINED member is skipped rather than counted: it
  ///     cannot be classified as completed, and a guessed classification would move the
  ///     completed count the baseline rests on.</para></summary>
  private static async Task<List<BaselineRun>> ReadRunsAsync(SqliteConnection connection,
      string scope, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = RunSql;
    _ = command.Parameters.AddWithValue("@ws", scope);
    List<BaselineRun> runs = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      if (!int.TryParse(reader.GetString(1), NumberStyles.Integer, CultureInfo.InvariantCulture,
          out int raw) || !Enum.IsDefined((AgentStatus)raw))
      {
        continue;
      }

      runs.Add(new BaselineRun(reader.GetString(0), (AgentStatus)raw,
          Clock(reader, 2), Clock(reader, 3)));
    }

    return runs;
  }

  /// <summary>Each run's total cost, priced with the ledger's ONE formula.
  ///     <para>A payload that is not a usage object, or that cannot be priced, contributes
  ///     nothing — so an unpriced run simply has no entry and is counted as unpriced by the
  ///     measurement, never priced at zero.</para></summary>
  private static async Task<Dictionary<string, decimal>> ReadCostsAsync(SqliteConnection connection,
      string scope, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = CostSql;
    _ = command.Parameters.AddWithValue("@ws", scope);
    Dictionary<string, decimal> totals = new(StringComparer.Ordinal);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      string agentId = reader.GetString(0);
      if (UsageRowOf(reader.GetString(1)) is not { } row || UsageCost.Of(row) is not { } cost)
      {
        continue;
      }

      totals[agentId] = (totals.TryGetValue(agentId, out decimal running) ? running : 0m) + cost;
    }

    return totals;
  }

  /// <summary>Turns runs plus per-run costs into the baseline record.
  ///     <para>A RUN'S COST IS PER RUN, so a run that could not be priced is EXCLUDED from the
  ///     median and COUNTED — including it at zero would drag the median down and make the
  ///     baseline look cheaper than it was, which would make every chain look expensive.
  ///     </para></summary>
  private static ChainTelemetryBaseline Measure(List<BaselineRun> runs,
      Dictionary<string, decimal> costs)
  {
    List<BaselineRun> completed = [.. runs.Where(run => run.Status == AgentStatus.Completed)];
    List<double> durations =
    [
        .. completed.Where(run => run is { StartedAt: not null, CompletedAt: not null })
            .Select(run => (run.CompletedAt!.Value - run.StartedAt!.Value).TotalSeconds)
            .Where(seconds => seconds >= 0d),
    ];
    List<decimal> priced =
    [
        .. completed.Select(run => costs.TryGetValue(run.AgentId, out decimal cost) ? cost : (decimal?)null)
            .Where(cost => cost is not null)
            .Select(cost => cost!.Value),
    ];
    return new ChainTelemetryBaseline(runs.Count, completed.Count,
        UsageStatistics.Median(durations), UsageStatistics.Median([.. priced.Select(c => (double)c)])
            is { } costMedian ? (decimal)costMedian : null,
        completed.Count - priced.Count);
  }

  /// <summary>One nullable clock column, parsed at invariant culture, or null when absent.
  ///     <para>AN UNPARSEABLE VALUE READS AS ABSENT rather than throwing: a baseline is a
  ///     comparison aid, and refusing the whole report because one historical row has a malformed
  ///     timestamp would be a worse answer than excluding it.</para></summary>
  private static DateTimeOffset? Clock(SqliteDataReader reader, int ordinal) =>
      reader.IsDBNull(ordinal) ? null : ParseClock(reader.GetString(ordinal));

  /// <summary>One stored timestamp as an instant, or null when it does not parse.</summary>
  private static DateTimeOffset? ParseClock(string text) =>
      DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
          out DateTimeOffset parsed) ? parsed : null;

  /// <summary>One stored usage payload as the Model Domain's neutral row, or null when it is
  ///     not a usage object. Tolerant by design, exactly as the ledger's own reader is.
  ///     </summary>
  private static UsageRow? UsageRowOf(string payloadJson)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(payloadJson);
      JsonElement root = document.RootElement;
      return root.ValueKind != JsonValueKind.Object
          ? null
          : new UsageRow(Text(root, "CallClass") ?? "main_loop", Text(root, "ModelId"),
              Text(root, "Provider"), Text(root, "WorkspaceId"), Integer(root, "InputTokens") ?? 0,
              Integer(root, "OutputTokens") ?? 0, Integer(root, "CachedInputTokens"),
              Integer(root, "ReasoningTokens"), Number(root, "PromptPricePerToken"),
              Number(root, "PromptCacheReadPricePerToken"), Number(root, "CompletionPricePerToken"),
              Text(root, "RequestedModelId"), Text(root, "ServedModelId"), Long(root, "DurationMs"),
              Long(root, "FirstTokenMs"), Text(root, "ServedProvider"), Number(root, "ReportedCost"),
              Flag(root, "ExpectsReportedCost"));
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static string? Text(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
          ? value.GetString()
          : null;

  private static int? Integer(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
      && value.TryGetInt32(out int parsed) ? parsed : null;

  private static long? Long(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
      && value.TryGetInt64(out long parsed) ? parsed : null;

  private static decimal? Number(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) ? PriceOf(value) : null;

  /// <summary>One stored price as a number, or null when it is not one. A string is accepted
  ///     for the same reason the ledger's reader accepts one: a serializer configured for
  ///     invariant decimals stores the number as text.</summary>
  private static decimal? PriceOf(JsonElement value) =>
      value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)
          ? number
          : PriceText(value);

  /// <summary>A price stored as text, or null when the text is not a number.</summary>
  private static decimal? PriceText(JsonElement value) =>
      value.ValueKind == JsonValueKind.String
      && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture,
          out decimal parsed)
          ? parsed
          : null;

  private static bool? Flag(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value)
      && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

  /// <summary>One baseline run: what the row says about it, and nothing inferred.</summary>
  private sealed record BaselineRun(string AgentId, AgentStatus Status, DateTimeOffset? StartedAt,
      DateTimeOffset? CompletedAt);
}
