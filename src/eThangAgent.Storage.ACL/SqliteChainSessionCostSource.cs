using System.Globalization;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>The usage ledger's totals for the sessions a chain started (issue #309 D1).
///     <para>IT READS THE SAME <c>agent_events</c> USAGE ROWS THE STATUS BAR DOES, and sums them
///     with the Model Domain's ONE cost formula — never a second arithmetic. That is the whole of
///     D1: a cost number computed twice would eventually disagree with the ledger, and the ledger
///     is the one that is reconciled.</para>
///     <para>ONE QUERY FOR THE WHOLE SET, not one per session: a chain with twenty steps would
///     otherwise run twenty recursive CTEs, and the report is a batch question. The subtree walk
///     is kept per session id (a session's children's spend counts toward it, the #24 B11 rule),
///     so this is a recursive CTE seeded with a list rather than a single id.</para>
///     <para>READ-ONLY: it opens the read-only connection, so a report can never mutate the
///     ledger it measures.</para></summary>
public sealed class SqliteChainSessionCostSource(AppDatabase database) : IChainSessionCostSource
{
  private const string MainLoopClass = "main_loop";

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  /// <summary>The subtree walk seeded with a LIST of session ids, so a chain's whole session set
  ///     is priced in one pass.
  ///     <para><c>UNION</c> (not <c>UNION ALL</c>) keeps a corrupt parent cycle from looping
  ///     forever — the <c>SqliteUsageQueries</c> rule, for the same reason.</para></summary>
  private const string Sql =
      @"WITH RECURSIVE subtree(session_id, id) AS (
            SELECT value, value FROM json_each(@sessions)
            UNION
            SELECT s.session_id, a.id FROM agents a JOIN subtree s ON a.parent_id = s.id
        )
        SELECT s.session_id, e.payload_json
        FROM agent_events e
        JOIN subtree s ON e.agent_id = s.id
        WHERE e.type = 'usage' AND json_valid(e.payload_json)
        ORDER BY e.id;";

  public async Task<Result<IReadOnlyList<ChainTelemetrySessionCost>>> CostsForAsync(
      IReadOnlyList<string> sessionIds, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(sessionIds);
    if (sessionIds.Count == 0)
    {
      return Result.Success<IReadOnlyList<ChainTelemetrySessionCost>>([]);
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.OpenReadOnly();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = Sql;
      _ = command.Parameters.AddWithValue("@sessions",
          JsonSerializer.Serialize(sessionIds.Distinct(StringComparer.Ordinal)));

      Dictionary<string, UsageAccumulator> totals = new(StringComparer.Ordinal);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        string sessionId = reader.GetString(0);
        if (!totals.TryGetValue(sessionId, out UsageAccumulator? total))
        {
          total = new UsageAccumulator(sessionId);
          totals[sessionId] = total;
        }

        total.Add(reader.GetString(1));
      }

      // A session with no rows is ABSENT rather than present with a zero: 'never ran a billable
      // call' and 'ran free' are different facts and a zero would assert the second.
      return Result.Success<IReadOnlyList<ChainTelemetrySessionCost>>(
          [.. totals.Values.Where(total => total.Calls > 0).Select(total => total.Build())]);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<ChainTelemetrySessionCost>>(new DomainError(
          "StorageUnavailable", $"the chain cost read could not read the ledger: {ex.Message}"));
    }
  }

  /// <summary>One session's running total, accumulated with the Model Domain's cost formula.
  ///     <para>THE COST ARITHMETIC IS DELEGATED to <c>UsageCost</c> rather than reimplemented: a
  ///     second implementation is the disagreement D1 forbids. It is reached through the same
  ///     neutral <c>UsageRow</c> the status bar builds, so this reader and that one cannot price
  ///     the same payload differently.</para></summary>
  private sealed class UsageAccumulator(string sessionId)
  {
    private decimal? _cost;
    private int _priced;
    private int _unpriced;

    public int Calls => _priced + _unpriced;

    /// <summary>Adds one stored usage payload. A payload that is not a usage object is
    ///     SKIPPED rather than counted as zero spend: a corrupt row must not be reported as a
    ///     free call.</summary>
    public void Add(string payloadJson)
    {
      if (Payload(payloadJson) is not { } row)
      {
        return;
      }

      if (UsageCost.Of(row) is { } cost)
      {
        _cost = (_cost ?? 0m) + cost;
        _priced++;
      }
      else
      {
        _unpriced++;
      }
    }

    public ChainTelemetrySessionCost Build() => new(sessionId, _cost, _priced, _unpriced);

    /// <summary>The payload as the Model Domain's neutral row, or null when it is not a usage
    ///     object.
    ///     <para>TOLERANT BY DESIGN, exactly as <c>SqliteUsageQueries</c> is: a payload written
    ///     before a member existed reads with it absent, and a null call class means
    ///     <c>main_loop</c> because every row written before the class existed came from the
    ///     loop.</para></summary>
    private static UsageRow? Payload(string payloadJson)
    {
      try
      {
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        JsonElement root = document.RootElement;
        return root.ValueKind != JsonValueKind.Object
            ? null
            : new UsageRow(
                Text(root, "CallClass") ?? MainLoopClass,
                Text(root, "ModelId"),
                Text(root, "Provider"),
                Text(root, "WorkspaceId"),
                Integer(root, "InputTokens") ?? 0,
                Integer(root, "OutputTokens") ?? 0,
                Integer(root, "CachedInputTokens"),
                Integer(root, "ReasoningTokens"),
                Number(root, "PromptPricePerToken"),
                Number(root, "PromptCacheReadPricePerToken"),
                Number(root, "CompletionPricePerToken"),
                Text(root, "RequestedModelId"),
                Text(root, "ServedModelId"),
                Long(root, "DurationMs"),
                Long(root, "FirstTokenMs"),
                Text(root, "ServedProvider"),
                Number(root, "ReportedCost"),
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

    /// <summary>A price as stored, accepting a numeric string for the same reason the status
    ///     bar's reader does: a serializer configured for invariant decimals stores it as
    ///     text.</summary>
    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? ValueOf(value) : null;

    /// <summary>One stored price value as a number, or null when it is not one.
    ///     <para>A STRING IS ACCEPTED because a serializer configured for invariant decimals
    ///     stores the number as text, and refusing it would silently unpriced every such row.
    ///     </para></summary>
    private static decimal? ValueOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)
            ? number
            : TextValue(value);

    /// <summary>A price stored as text, or null when the text is not a number.</summary>
    private static decimal? TextValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture,
            out decimal parsed)
            ? parsed
            : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
  }
}
