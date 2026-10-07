using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The 'openrouter_management' tool: the model's door to the OpenRouter
///     Management API's DEBUGGING surface - per-request generation logs, their
///     stored content, the credits balance, and the activity/analytics usage logs.
///     Input is parsed strictly by <see cref="OpenRouterManagementToolInput"/>,
///     dispatched to the <see cref="IOpenRouterManagementAccess"/> seam, and
///     rendered on the fixed output contract. The seam never throws domain
///     errors - failures arrive as <see cref="OpenRouterManagementOutcome.Failure"/>
///     values and are rendered, not caught (the McpTool pattern).</summary>
public sealed class OpenRouterManagementTool(IOpenRouterManagementAccess access) : ITool
{
  private readonly IOpenRouterManagementAccess _access =
      access ?? throw new ArgumentNullException(nameof(access));

  /// <summary>The wire name of every admitted action, in advertisement order.</summary>
  private static readonly string[] ActionNames =
      ["generation", "generation_content", "credits", "activity", "analytics_meta", "analytics_query"];

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "openrouter_management",
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
              ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("action", ToolParameterType.Text,
              "Exactly one of generation, generation_content, credits, activity, analytics_meta, analytics_query (case-sensitive)."),
          new ToolParameter("id", ToolParameterType.Text,
              "Required for generation and generation_content: the generation id (gen-...) from a response's usage metadata."),
          new ToolParameter("date", ToolParameterType.Text,
              "Optional for activity: a YYYY-MM-DD UTC day (default: the last 30 completed days)."),
          new ToolParameter("group_by", ToolParameterType.Text,
              "Optional for activity: exactly workspace to group rows by workspace."),
          new ToolParameter("metrics", ToolParameterType.TextArray,
              "Required for analytics_query: a non-empty array of metric names (see analytics_meta)."),
          new ToolParameter("dimensions", ToolParameterType.TextArray,
              "Optional for analytics_query: at most 2 dimension names to group by."),
          new ToolParameter("granularity", ToolParameterType.Text,
              "Optional for analytics_query: a granularity name (see analytics_meta, e.g. day)."),
          new ToolParameter("time_range_start", ToolParameterType.Text,
              "Optional for analytics_query: ISO-8601 start of the time range."),
          new ToolParameter("time_range_end", ToolParameterType.Text,
              "Optional for analytics_query: ISO-8601 end of the time range."),
          new ToolParameter("group_limit", ToolParameterType.WholeNumber,
              "Optional for analytics_query: max groups per dimension (default 50)."),
          new ToolParameter("limit", ToolParameterType.WholeNumber,
              "Optional for analytics_query: max rows returned (default 100)."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  /// <summary>The description IS the contract: every action, key, output annotation,
  ///     and error code appears verbatim so the model never guesses what it is
  ///     looking at.</summary>
  private static string BuildDescription()
  {
    return "Review OpenRouter usage logs for debugging through the OpenRouter Management API"
        + " (requires the optional management key; model API keys cannot serve these routes)."
        + " timeoutSeconds is mandatory. action is exactly one of " + string.Join(", ", ActionNames)
        + " (case-sensitive)."
        + " generation takes id (gen-...) and returns one request's log line: model, provider, finish reason,"
        + " token counts, cost, and latency. generation_content takes id and returns the stored prompt and"
        + " completion text (or a signed URL) plus the error message. credits returns the balance"
        + " (total, used, remaining). activity returns per-day usage rows (date YYYY-MM-DD optional, default"
        + " the last 30 completed days; group_by workspace optional). analytics_meta lists the available"
        + " metrics, dimensions, and granularities. analytics_query runs one aggregation: metrics (required,"
        + " non-empty), up to 2 dimensions, granularity, time_range_start/time_range_end (ISO-8601),"
        + " group_limit, and limit."
        + " Output: generation renders '[openrouter-management] generation <id>' plus a field line;"
        + " generation_content renders the error/input/output blocks; credits renders total/used/remaining;"
        + " activity renders '[openrouter-management] N activity row(s)' plus one line per row;"
        + " analytics_meta renders the field catalogs; analytics_query renders 'N row(s), truncated: <bool>'"
        + " plus key=value pairs per row."
        + " Failures render 'Error [Code]: <message>'."
        + " Error codes: ManagementUnavailable (no management key configured - add one in Settings, API Keys),"
        + " ManagementError (the API refused - HTTP status and its message included), ManagementParseError"
        + " (a response the API contract does not describe). Errors are safe to retry with corrected input.";
  }

  /// <inheritdoc />
  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<OpenRouterManagementToolInput> parsed = OpenRouterManagementToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => RunAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> RunAsync(OpenRouterManagementToolInput args, CancellationToken ct)
  {
    OpenRouterManagementCommand command = args.Action switch
    {
      OpenRouterManagementAction.Generation => new OpenRouterManagementCommand.GetGeneration(args.GenerationId!),
      OpenRouterManagementAction.GenerationContent => new OpenRouterManagementCommand.GetGenerationContent(args.GenerationId!),
      OpenRouterManagementAction.Credits => new OpenRouterManagementCommand.GetCredits(),
      OpenRouterManagementAction.Activity => new OpenRouterManagementCommand.GetActivity(args.Date, args.GroupBy),
      OpenRouterManagementAction.AnalyticsMeta => new OpenRouterManagementCommand.GetAnalyticsMeta(),
      OpenRouterManagementAction.AnalyticsQuery => new OpenRouterManagementCommand.QueryAnalytics(
          args.Metrics!, args.Dimensions, args.Granularity, args.TimeRangeStart, args.TimeRangeEnd,
          args.GroupLimit, args.Limit),
      _ => throw new InvalidOperationException("unreachable: action is a validated enum"),
    };

    OpenRouterManagementOutcome outcome = await _access.ExecuteAsync(command, ct).ConfigureAwait(false);
    return outcome switch
    {
      OpenRouterManagementOutcome.Generation generation => new ToolResult(
          $"[openrouter-management] generation {generation.Item.Id}\n{generation.Item.RenderLine()}",
          false),
      OpenRouterManagementOutcome.GenerationContent content => RenderContent(content.Item),
      OpenRouterManagementOutcome.Credits credits => RenderCredits(credits.Item),
      OpenRouterManagementOutcome.Activity activity => RenderActivity(activity.Rows),
      OpenRouterManagementOutcome.AnalyticsMeta meta => RenderMeta(meta),
      OpenRouterManagementOutcome.Analytics analytics => RenderAnalytics(analytics.Result),
      OpenRouterManagementOutcome.Failure failure => new ToolResult(
          $"Error [{failure.Code}]: {failure.Message}", true),
      _ => throw new InvalidOperationException("unreachable: outcome is a validated record family"),
    };
  }

  private static ToolResult RenderContent(OpenRouterGenerationContent content)
  {
    string error = content.Error is { } e ? $"error: {e}" : "error: none";
    string input = content.Input is { } i ? $"input: {i}" : "input: (none)";
    string output = content.Output is { } o ? $"output: {o}" : "output: (none)";
    string top = content.ErrorMessage is { } m ? $"\nmessage: {m}" : string.Empty;
    return new ToolResult(
        "[openrouter-management] generation content\n" + error + "\n" + input + "\n" + output + top,
        false);
  }

  private static ToolResult RenderCredits(OpenRouterCreditsItem credits)
  {
    return new ToolResult(
        string.Create(CultureInfo.InvariantCulture,
            $"[openrouter-management] credits: {credits.TotalCredits:0.##} total, {credits.TotalUsage:0.##} used, {credits.TotalCredits - credits.TotalUsage:0.##} remaining"),
        false);
  }

  private static ToolResult RenderActivity(IReadOnlyList<OpenRouterActivityRow> rows)
  {
    if (rows.Count == 0)
    {
      return new ToolResult("[openrouter-management] 0 activity row(s)", false);
    }

    string[] lines = new string[rows.Count + 1];
    lines[0] = string.Create(CultureInfo.InvariantCulture, $"[openrouter-management] {rows.Count} activity row(s)");
    for (int i = 0; i < rows.Count; i++)
    {
      OpenRouterActivityRow row = rows[i];
      lines[i + 1] = $"{row.Date} | {row.Model ?? "(unknown)"} | {row.ProviderName ?? "(unknown)"} | {row.RequestCount} req | " +
          $"prompt {row.PromptTokens}, completion {row.CompletionTokens}, reasoning {row.ReasoningTokens}, cached {row.CachedTokens} | " +
          $"usage {row.Usage:0.####}" + (row.ByokUsage is { } byok ? $", byok {byok:0.####}" : string.Empty);
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  private static ToolResult RenderMeta(OpenRouterManagementOutcome.AnalyticsMeta meta)
  {
    List<string> lines =
    [
        "[openrouter-management] analytics fields",
        "metrics: " + RenderFields(meta.Metrics),
        "dimensions: " + RenderFields(meta.Dimensions),
        "granularities: " + RenderFields(meta.Granularities),
    ];
    return new ToolResult(string.Join("\n", lines), false);
  }

  private static string RenderFields(IReadOnlyList<OpenRouterAnalyticsField> fields) =>
      string.Join(", ", fields.Select(RenderField));

  private static string RenderField(OpenRouterAnalyticsField f)
  {
    return (f.Label, f.Type) switch
    {
      (null, _) => f.Name,
      ({ } label, null) => $"{f.Name} ({label})",
      ({ } label, { } type) => $"{f.Name} ({label}, {type})",
    };
  }

  private static ToolResult RenderAnalytics(OpenRouterAnalyticsResult result)
  {
    string head = $"[openrouter-management] {result.RowCount} row(s), truncated: {(result.Truncated ? "true" : "false")}"
        + (result.NextOffset is { } next ? $", next_offset {next}" : string.Empty);
    List<string> lines = [head];
    foreach (IReadOnlyDictionary<string, string> row in result.Rows)
    {
      lines.Add(string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
