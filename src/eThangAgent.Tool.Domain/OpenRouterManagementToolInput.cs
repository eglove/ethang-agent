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
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<OpenRouterManagementToolInput>(new DomainError("MissingParameter",
          "'action' is required: exactly one of generation, generation_content, credits, activity, analytics_meta, analytics_query (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    Result<OpenRouterManagementAction> action = actionText switch
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
    if (!action.IsSuccess)
    {
      return Result.Failure<OpenRouterManagementToolInput>(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    if (unknown is not null)
    {
      return Result.Failure<OpenRouterManagementToolInput>(unknown);
    }

    string? generationId = null;
    string? date = null;
    string? groupBy = null;
    IReadOnlyList<string>? metrics = null;
    IReadOnlyList<string>? dimensions = null;
    string? granularity = null;
    string? timeRangeStart = null;
    string? timeRangeEnd = null;
    int? groupLimit = null;
    int? limit = null;

    if (action.Value is OpenRouterManagementAction.Generation or OpenRouterManagementAction.GenerationContent)
    {
      Result<string> idText = ToolArguments.RequireString(json, "id",
          "For generation and generation_content, 'id' is the generation id (gen-...) from a response's usage metadata.");
      if (!idText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(idText.Error);
      }

      Result<string> id = GenerationIdSpecification.Validate(idText.Value);
      if (!id.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(id.Error);
      }
      generationId = id.Value;
    }

    if (action.Value is OpenRouterManagementAction.Activity)
    {
      Result<string?> dateText = ToolArguments.OptionalString(json, "date");
      if (!dateText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(dateText.Error);
      }
      date = dateText.Value;
      if (date is { } d && !DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
          DateTimeStyles.None, out _))
      {
        return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'date' must be YYYY-MM-DD (got '{d}')."));
      }

      Result<string?> groupByText = ToolArguments.OptionalString(json, "group_by");
      if (!groupByText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(groupByText.Error);
      }
      groupBy = groupByText.Value;
      if (groupBy is { } g && g is not "workspace")
      {
        return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'group_by' must be exactly workspace (case-sensitive; got '{g}')."));
      }
    }

    if (action.Value is OpenRouterManagementAction.AnalyticsQuery)
    {
      Result<IReadOnlyList<string>?> metricsText = ToolArguments.OptionalStringArray(json, "metrics");
      if (!metricsText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(metricsText.Error);
      }
      metrics = metricsText.Value;
      if (metrics is null or { Count: 0 })
      {
        return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.MissingParameter,
            "Missing required parameter 'metrics'. For analytics_query, 'metrics' is a non-empty array of field names (see analytics_meta)."));
      }

      Result<IReadOnlyList<string>?> dimensionsText = ToolArguments.OptionalStringArray(json, "dimensions");
      if (!dimensionsText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(dimensionsText.Error);
      }
      dimensions = dimensionsText.Value;
      if (dimensions is { Count: > 2 })
      {
        return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'dimensions' accepts at most 2 entries (got {dimensions.Count})."));
      }

      Result<string?> granularityText = ToolArguments.OptionalString(json, "granularity");
      if (!granularityText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(granularityText.Error);
      }
      granularity = granularityText.Value;

      Result<string?> startText = ToolArguments.OptionalString(json, "time_range_start");
      if (!startText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(startText.Error);
      }
      timeRangeStart = startText.Value;
      Result<string?> endText = ToolArguments.OptionalString(json, "time_range_end");
      if (!endText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(endText.Error);
      }
      timeRangeEnd = endText.Value;
      DomainError? range = TimeRangeError(timeRangeStart, timeRangeEnd);
      if (range is not null)
      {
        return Result.Failure<OpenRouterManagementToolInput>(range);
      }

      Result<int?> groupLimitText = ToolArguments.OptionalInt(json, "group_limit");
      if (!groupLimitText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(groupLimitText.Error);
      }
      groupLimit = groupLimitText.Value;
      if (groupLimit is { } gl && gl < 1)
      {
        return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'group_limit' must be >= 1 (got {gl})."));
      }

      Result<int?> limitText = ToolArguments.OptionalInt(json, "limit");
      if (!limitText.IsSuccess)
      {
        return Result.Failure<OpenRouterManagementToolInput>(limitText.Error);
      }
      limit = limitText.Value;
      if (limit is { } l && l < 1)
      {
        return Result.Failure<OpenRouterManagementToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'limit' must be >= 1 (got {l})."));
      }
    }

    return Result.Success(new OpenRouterManagementToolInput(
        action.Value, generationId, date, groupBy, metrics, dimensions, granularity,
        timeRangeStart, timeRangeEnd, groupLimit, limit));
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
