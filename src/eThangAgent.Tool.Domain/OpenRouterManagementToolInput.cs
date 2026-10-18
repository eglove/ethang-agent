using System.Globalization;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The 'openrouter_management' tool's action switch - the debugging
///     (log-review) surface of the OpenRouter Management API.</summary>
public enum OpenRouterManagementAction
{
  /// <summary>One generation's metadata by id (per-request log line).</summary>
  Generation,
  /// <summary>One generation's stored prompt/completion/error content.</summary>
  GenerationContent,
  /// <summary>The credits balance.</summary>
  Credits,
  /// <summary>The daily activity log (30 days, or one date).</summary>
  Activity,
  /// <summary>The analytics field catalog (metrics, dimensions, granularities).</summary>
  AnalyticsMeta,
  /// <summary>One aggregated analytics query.</summary>
  AnalyticsQuery,
}

/// <summary>Strictly parsed arguments for the 'openrouter_management' tool: a
///     required action switch, per-action required keys, unknown-key rejection,
///     and per-action value rules - nothing coerced, nothing defaulted.</summary>
public sealed record OpenRouterManagementToolInput(
    OpenRouterManagementAction Action,
    string? GenerationId,
    string? Date,
    string? GroupBy,
    IReadOnlyList<string>? Metrics,
    IReadOnlyList<string>? Dimensions,
    string? Granularity,
    string? TimeRangeStart,
    string? TimeRangeEnd,
    int? GroupLimit,
    int? Limit)
{
  /// <summary>Parses raw JSON arguments into validated input.</summary>
  public static Result<OpenRouterManagementToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<OpenRouterManagementToolInput>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    Result<OpenRouterManagementAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return Result.Failure<OpenRouterManagementToolInput>(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    if (unknown is not null)
    {
      return Result.Failure<OpenRouterManagementToolInput>(unknown);
    }

    ActionFields fields = new();
    DomainError? parse = ParseFields(json, action.Value, fields);
    if (parse is not null)
    {
      return Result.Failure<OpenRouterManagementToolInput>(parse);
    }

    OpenRouterManagementToolInput input = new(
        action.Value, fields.GenerationId, fields.Date, fields.GroupBy, fields.Metrics,
        fields.Dimensions, fields.Granularity, fields.TimeRangeStart, fields.TimeRangeEnd,
        fields.GroupLimit, fields.Limit);
    return Result.Success(input);
  }

  // Per-action field harvesting: each action's required and optional keys parse
  // into the carrier; actions without extra keys parse nothing.
  private static DomainError? ParseFields(
      JsonElement json, OpenRouterManagementAction action, ActionFields fields) => action switch
      {
        OpenRouterManagementAction.Generation or OpenRouterManagementAction.GenerationContent => ParseGenerationId(json, fields),
        OpenRouterManagementAction.Activity => ParseActivity(json, fields),
        OpenRouterManagementAction.Credits or OpenRouterManagementAction.AnalyticsMeta => null,
        OpenRouterManagementAction.AnalyticsQuery => ParseAnalyticsQuery(json, fields),
        _ => null,
      };

  // Mutable carrier for the per-action optional fields, populated by the
  // per-action parsers below.
  private sealed class ActionFields
  {
    public string? GenerationId { get; set; }
    public string? Date { get; set; }
    public string? GroupBy { get; set; }
    public IReadOnlyList<string>? Metrics { get; set; }
    public IReadOnlyList<string>? Dimensions { get; set; }
    public string? Granularity { get; set; }
    public string? TimeRangeStart { get; set; }
    public string? TimeRangeEnd { get; set; }
    public int? GroupLimit { get; set; }
    public int? Limit { get; set; }
  }

  private static Result<OpenRouterManagementAction> ParseAction(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<OpenRouterManagementAction>(new DomainError("MissingParameter",
          "'action' is required: exactly one of generation, generation_content, credits, activity, analytics_meta, analytics_query (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<OpenRouterManagementAction>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    return actionText switch
    {
      "generation" => Result.Success(OpenRouterManagementAction.Generation),
      "generation_content" => Result.Success(OpenRouterManagementAction.GenerationContent),
      "credits" => Result.Success(OpenRouterManagementAction.Credits),
      "activity" => Result.Success(OpenRouterManagementAction.Activity),
      "analytics_meta" => Result.Success(OpenRouterManagementAction.AnalyticsMeta),
      "analytics_query" => Result.Success(OpenRouterManagementAction.AnalyticsQuery),
      _ => Result.Failure<OpenRouterManagementAction>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'action' must be exactly one of generation, generation_content, credits, activity, analytics_meta, analytics_query (case-sensitive; got '{actionText}').")),
    };
  }

  private static DomainError? ParseGenerationId(JsonElement json, ActionFields fields)
  {
    Result<string> idText = ToolArguments.RequireString(json, "id",
        "For generation and generation_content, 'id' is the generation id (gen-...) from a response's usage metadata.");
    if (!idText.IsSuccess)
    {
      return idText.Error;
    }

    Result<string> id = GenerationIdSpecification.Validate(idText.Value);
    if (!id.IsSuccess)
    {
      return id.Error;
    }

    fields.GenerationId = id.Value;
    return null;
  }

  private static DomainError? ParseActivity(JsonElement json, ActionFields fields)
  {
    Result<string?> dateText = ToolArguments.OptionalString(json, "date");
    if (!dateText.IsSuccess)
    {
      return dateText.Error;
    }

    fields.Date = dateText.Value;
    if (fields.Date is { } d && !DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out _))
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'date' must be YYYY-MM-DD (got '{d}').");
    }

    Result<string?> groupByText = ToolArguments.OptionalString(json, "group_by");
    if (!groupByText.IsSuccess)
    {
      return groupByText.Error;
    }

    fields.GroupBy = groupByText.Value;
    return fields.GroupBy is { } g && g is not "workspace"
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'group_by' must be exactly workspace (case-sensitive; got '{g}').")
        : null;
  }

  private static DomainError? ParseAnalyticsQuery(JsonElement json, ActionFields fields)
  {
    DomainError? metrics = ParseMetrics(json, fields);
    if (metrics is not null)
    {
      return metrics;
    }

    DomainError? dimensions = ParseDimensions(json, fields);
    if (dimensions is not null)
    {
      return dimensions;
    }

    Result<string?> granularityText = ToolArguments.OptionalString(json, "granularity");
    if (!granularityText.IsSuccess)
    {
      return granularityText.Error;
    }

    fields.Granularity = granularityText.Value;
    DomainError? range = ParseTimeRange(json, fields);
    return range ?? ParseLimits(json, fields);
  }

  private static DomainError? ParseMetrics(JsonElement json, ActionFields fields)
  {
    Result<IReadOnlyList<string>?> metricsText = ToolArguments.OptionalStringArray(json, "metrics");
    if (!metricsText.IsSuccess)
    {
      return metricsText.Error;
    }

    fields.Metrics = metricsText.Value;
    return fields.Metrics is null or { Count: 0 }
        ? new DomainError(ToolErrorCodes.MissingParameter,
            "Missing required parameter 'metrics'. For analytics_query, 'metrics' is a non-empty array of field names (see analytics_meta).")
        : null;
  }

  private static DomainError? ParseDimensions(JsonElement json, ActionFields fields)
  {
    Result<IReadOnlyList<string>?> dimensionsText = ToolArguments.OptionalStringArray(json, "dimensions");
    if (!dimensionsText.IsSuccess)
    {
      return dimensionsText.Error;
    }

    fields.Dimensions = dimensionsText.Value;
    return fields.Dimensions is { Count: > 2 }
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'dimensions' accepts at most 2 entries (got {fields.Dimensions.Count}).")
        : null;
  }

  private static DomainError? ParseTimeRange(JsonElement json, ActionFields fields)
  {
    Result<string?> startText = ToolArguments.OptionalString(json, "time_range_start");
    if (!startText.IsSuccess)
    {
      return startText.Error;
    }

    fields.TimeRangeStart = startText.Value;
    Result<string?> endText = ToolArguments.OptionalString(json, "time_range_end");
    if (!endText.IsSuccess)
    {
      return endText.Error;
    }

    fields.TimeRangeEnd = endText.Value;
    return TimeRangeError(fields.TimeRangeStart, fields.TimeRangeEnd);
  }

  private static DomainError? ParseLimits(JsonElement json, ActionFields fields)
  {
    Result<int?> groupLimitText = ToolArguments.OptionalInt(json, "group_limit");
    if (!groupLimitText.IsSuccess)
    {
      return groupLimitText.Error;
    }

    fields.GroupLimit = groupLimitText.Value;
    if (fields.GroupLimit is { } gl && gl < 1)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'group_limit' must be >= 1 (got {gl}).");
    }

    Result<int?> limitText = ToolArguments.OptionalInt(json, "limit");
    if (!limitText.IsSuccess)
    {
      return limitText.Error;
    }

    fields.Limit = limitText.Value;
    return fields.Limit is { } l && l < 1
        ? new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'limit' must be >= 1 (got {l}).")
        : null;
  }

  /// <summary>Time-range rule: both bounds parse as ISO-8601 and start is not after end.</summary>
  private static DomainError? TimeRangeError(string? start, string? end)
  {
    DateTimeOffset? startAt = ParseTimestamp(start);
    DateTimeOffset? endAt = ParseTimestamp(end);

    return (startAt, endAt) switch
    {
      _ when start is { } s && startAt is null => new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'time_range_start' must be an ISO-8601 timestamp (got '{s}')."),
      _ when end is { } e && endAt is null => new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'time_range_end' must be an ISO-8601 timestamp (got '{e}')."),
      ({ } a, { } b) when a > b => new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'time_range_start' must not be after 'time_range_end'."),
      _ => null,
    };
  }

  private static DateTimeOffset? ParseTimestamp(string? text) =>
      text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
          ? parsed
          : null;

  private static string[] Allowed(OpenRouterManagementAction action) => action switch
  {
    OpenRouterManagementAction.Generation => ["action", ToolTimeout.ParameterName, "id"],
    OpenRouterManagementAction.GenerationContent => ["action", ToolTimeout.ParameterName, "id"],
    OpenRouterManagementAction.Credits => ["action", ToolTimeout.ParameterName],
    OpenRouterManagementAction.Activity => ["action", ToolTimeout.ParameterName, "date", "group_by"],
    OpenRouterManagementAction.AnalyticsMeta => ["action", ToolTimeout.ParameterName],
    OpenRouterManagementAction.AnalyticsQuery => ["action", ToolTimeout.ParameterName, "metrics", "dimensions",
        "granularity", "time_range_start", "time_range_end", "group_limit", "limit"],
    _ => [],
  };
}
