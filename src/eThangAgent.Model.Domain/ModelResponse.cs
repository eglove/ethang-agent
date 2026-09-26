namespace eThangAgent.ModelDomain;

/// <summary>A completed model response: assistant text, any tool calls, and why the
///     response ended. FinishReason defaults to <see cref="FinishReason.Stop"/> — the
///     named-leniency default so simple fakes and legacy callers mean "completed
///     normally"; real provider ACLs set it explicitly from the wire format.</summary>
/// <param name="Content">Assistant text, or null when the model returned only tool calls.</param>
/// <param name="ToolCalls">Tool calls requested by the model; empty for a plain answer.</param>
/// <param name="FinishReason">Why the response ended.</param>
/// <param name="Usage">Provider-reported token usage, or null when the provider reported none.</param>
/// <param name="ServerToolCalls">Server-side tool calls the provider executed inside this
/// response (web_search etc.); empty when none. They never enter the message history —
/// hosts surface them from here.</param>
public sealed record ModelResponse(
    string? Content,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    FinishReason FinishReason = FinishReason.Stop,
    TokenUsage? Usage = null,
    IReadOnlyList<ServerToolCall>? ServerToolCalls = null)
{
  /// <summary>Never null: an absent list reads as empty.</summary>
  public IReadOnlyList<ServerToolCall> ServerToolCalls { get; init; } = ServerToolCalls ?? [];
}
