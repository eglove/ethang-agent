
namespace eThangAgent.ToolDomain;

/// <summary>One OpenRouter activity row (GET /api/v1/activity): per-day usage
///     aggregated by model and endpoint. All money values are dollars.</summary>
/// <param name="Date">The UTC day the usage happened on.</param>
/// <param name="Model">The model id, or null when the row aggregates unknowns.</param>
/// <param name="ProviderName">The routing provider name, or null.</param>
/// <param name="RequestCount">Requests made that day.</param>
/// <param name="PromptTokens">Summed prompt tokens.</param>
/// <param name="CompletionTokens">Summed completion tokens.</param>
/// <param name="ReasoningTokens">Summed reasoning tokens.</param>
/// <param name="CachedTokens">Summed cached prompt tokens.</param>
/// <param name="Usage">Cost in dollars (API price).</param>
/// <param name="ByokUsage">Cost in dollars (BYOK price), or null.</param>
public sealed record OpenRouterActivityRow(
    string Date,
    string? Model,
    string? ProviderName,
    long RequestCount,
    long PromptTokens,
    long CompletionTokens,
    long ReasoningTokens,
    long CachedTokens,
    double Usage,
    double? ByokUsage);

/// <summary>One generation record (GET /api/v1/generation?id=): the per-request
///     log line a debugging session triages on - model, provider, tokens, cost,
///     latency, and finish state.</summary>
/// <param name="Id">The generation id (gen-...).</param>
/// <param name="Model">The requested model id.</param>
/// <param name="ProviderName">The provider that served the request.</param>
/// <param name="Route">The routing strategy (fallback chain), or null.</param>
/// <param name="Created">ISO-8601 creation timestamp.</param>
/// <param name="Streamed">True when the request streamed.</param>
/// <param name="Cancelled">True when the request was cancelled.</param>
/// <param name="FinishReason">The completion finish reason, or null.</param>
/// <param name="NativeFinishReason">The provider's own finish reason, or null.</param>
/// <param name="Strategy">The load-balancing strategy (priority / user), or null.</param>
/// <param name="PromptTokens">Prompt tokens (null when the request failed before usage).</param>
/// <param name="CompletionTokens">Completion tokens, or null.</param>
/// <param name="ReasoningTokens">Reasoning tokens, or null.</param>
/// <param name="CachedTokens">Cached prompt tokens, or null.</param>
/// <param name="TotalCost">Total cost in dollars, or null.</param>
/// <param name="Usage">API-price cost in dollars, or null.</param>
/// <param name="Latency">Total request latency in ms, or null.</param>
/// <param name="ModerationLatency">Moderation latency in ms, or null.</param>
/// <param name="GenerationTime">Model generation time in ms, or null.</param>
/// <param name="UpstreamError">True when the provider returned an error.</param>
/// <param name="UpstreamId">The provider's own request id, or null.</param>
/// <param name="RequestId">OpenRouter's request id, or null.</param>
/// <param name="Seed">The request's seed, or null.</param>
/// <param name="ExternalUser">The external user id, or null.</param>
/// <param name="Endpoint">The endpoint used, or null.</param>
/// <param name="UpstreamTransport">The upstream transport, or null.</param>
public sealed record OpenRouterGenerationRecord(
    string Id,
    string? Model,
    string? ProviderName,
    string? Route,
    string? Created,
    bool Streamed,
    bool Cancelled,
    string? FinishReason,
    string? NativeFinishReason,
    string? Strategy,
    long? PromptTokens,
    long? CompletionTokens,
    long? ReasoningTokens,
    long? CachedTokens,
    double? TotalCost,
    double? Usage,
    long? Latency,
    long? ModerationLatency,
    long? GenerationTime,
    bool UpstreamError,
    string? UpstreamId,
    string? RequestId,
    long? Seed,
    string? ExternalUser,
    string? Endpoint,
    long? UpstreamTransport)
{
  /// <summary>The debug triage line: identity, outcome, tokens, cost, latency.
  ///     Never includes content (that is the separate generation_content lookup).</summary>
  public string RenderLine()
  {
    string finish = FinishReason is { } fr ? $", finish_reason: {fr}" : string.Empty;
    string tokens = PromptTokens is { } p && CompletionTokens is { } c
        ? $", prompt {p}, completion {c}"
        : string.Empty;
    string cost = TotalCost is { } spent ? $", total {spent:0.####}" : string.Empty;
    string latency = Latency is { } ms ? $", latency {ms} ms" : string.Empty;
    return $"model: {Model ?? "(unknown)"} | provider: {ProviderName ?? "(unknown)"}{finish}{tokens}{cost}{latency}";
  }
}

/// <summary>One generation's stored content (GET /api/v1/generation/content?id=):
///     the prompt and completion text plus the error message. Signed URLs arrive
///     when content is not inlined.</summary>
/// <param name="Error">The provider error message, or null.</param>
/// <param name="Input">The stored prompt text or signed URL, or null.</param>
/// <param name="Output">The stored completion text or signed URL, or null.</param>
/// <param name="ErrorMessage">Top-level error field, or null.</param>
public sealed record OpenRouterGenerationContent(
    string? Error,
    string? Input,
    string? Output,
    string? ErrorMessage);

/// <summary>The credits balance (GET /api/v1/credits): total purchased and used,
///     both in dollars. Remaining is derived.</summary>
/// <param name="TotalCredits">Lifetime purchased credits.</param>
/// <param name="TotalUsage">Lifetime used credits.</param>
public sealed record OpenRouterCreditsItem(double TotalCredits, double TotalUsage);

/// <summary>One analytics field description (GET /api/v1/analytics/meta):
///     a metric, dimension, or granularity name with its human label.</summary>
/// <param name="Name">The field name to use in analytics_query.</param>
/// <param name="Label">The human-readable label.</param>
/// <param name="Type">The value type (number, string, ...), or null.</param>
public sealed record OpenRouterAnalyticsField(string Name, string Label, string? Type);

/// <summary>The analytics query result (POST /api/v1/analytics/query): rows keyed
///     by field name (values serialized as strings), plus pagination metadata.</summary>
/// <param name="Rows">One dictionary per row.</param>
/// <param name="RowCount">Rows in this page.</param>
/// <param name="Truncated">True when the result set was truncated.</param>
/// <param name="NextOffset">The offset for the next page, or null when done.</param>
public sealed record OpenRouterAnalyticsResult(
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    long RowCount,
    bool Truncated,
    long? NextOffset);

/// <summary>Commands the tool sends the management seam (the closed command set).
///     Validation happens in the tool's parser; these records are already valid.</summary>
// CA1034: the nested command cases ARE the seam's contract; public nested cases
// keep the case names as declared (the McpCommand named decision).
#pragma warning disable CA1034
public abstract record OpenRouterManagementCommand
{
  /// <summary>Reads one generation's metadata by id (the per-request log line).</summary>
  public sealed record GetGeneration(string Id) : OpenRouterManagementCommand;

  /// <summary>Reads one generation's stored prompt/completion/error content.</summary>
  public sealed record GetGenerationContent(string Id) : OpenRouterManagementCommand;

  /// <summary>Reads the credits balance.</summary>
  public sealed record GetCredits() : OpenRouterManagementCommand;

  /// <summary>Reads the daily activity log. Date is YYYY-MM-DD or null (last 30 days);
  ///     GroupBy is 'workspace' or null.</summary>
  public sealed record GetActivity(string? Date, string? GroupBy) : OpenRouterManagementCommand;

  /// <summary>Lists the analytics fields available to analytics_query.</summary>
  public sealed record GetAnalyticsMeta() : OpenRouterManagementCommand;

  /// <summary>Runs one analytics query. Every member is already validated.</summary>
  public sealed record QueryAnalytics(
      IReadOnlyList<string> Metrics,
      IReadOnlyList<string>? Dimensions,
      string? Granularity,
      string? TimeRangeStart,
      string? TimeRangeEnd,
      int? GroupLimit,
      int? Limit) : OpenRouterManagementCommand;
}
#pragma warning restore CA1034

/// <summary>Outcomes the seam returns (the closed outcome set). Failures carry
///     typed error codes; the tool renders them verbatim.</summary>
// CA1034: the nested outcome cases ARE the seam's contract (the McpOutcome decision).
#pragma warning disable CA1034
public abstract record OpenRouterManagementOutcome
{
  /// <summary>One generation's metadata.</summary>
  public sealed record Generation(OpenRouterGenerationRecord Item) : OpenRouterManagementOutcome;

  /// <summary>One generation's stored content.</summary>
  public sealed record GenerationContent(OpenRouterGenerationContent Item) : OpenRouterManagementOutcome;

  /// <summary>The credits balance.</summary>
  public sealed record Credits(OpenRouterCreditsItem Item) : OpenRouterManagementOutcome;

  /// <summary>The activity rows (one page).</summary>
  public sealed record Activity(IReadOnlyList<OpenRouterActivityRow> Rows) : OpenRouterManagementOutcome;

  /// <summary>The analytics field catalog.</summary>
  public sealed record AnalyticsMeta(
      IReadOnlyList<OpenRouterAnalyticsField> Metrics,
      IReadOnlyList<OpenRouterAnalyticsField> Dimensions,
      IReadOnlyList<OpenRouterAnalyticsField> Granularities) : OpenRouterManagementOutcome;

  /// <summary>One analytics query result.</summary>
  public sealed record Analytics(OpenRouterAnalyticsResult Result) : OpenRouterManagementOutcome;

  /// <summary>A typed failure: ManagementUnavailable (no management key configured),
  ///     ManagementError (HTTP failure - the capped body is embedded), or
  ///     ManagementParseError (a body the API contract does not describe).</summary>
  public sealed record Failure(string Code, string Message) : OpenRouterManagementOutcome;
}
#pragma warning restore CA1034

/// <summary>Access to the OpenRouter Management API (openrouter.ai/docs/guides/
///     overview/auth/management-api-keys) for DEBUGGING - the log-review surface:
///     generation lookups, credits, activity, and analytics. Implementations
///     translate the closed command set onto the wire; every failure is a typed
///     outcome, never an exception - the same contract as IMcpServerAccess.</summary>
public interface IOpenRouterManagementAccess
{
  /// <summary>Executes one management command. Every failure is an outcome.</summary>
  Task<OpenRouterManagementOutcome> ExecuteAsync(OpenRouterManagementCommand command, CancellationToken ct = default);
}
