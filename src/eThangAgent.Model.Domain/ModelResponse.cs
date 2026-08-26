namespace eThangAgent.ModelDomain;

/// <summary>A completed model response: assistant text, any tool calls, and why the
///     response ended. FinishReason defaults to <see cref="FinishReason.Stop"/> — the
///     named-leniency default so simple fakes and legacy callers mean "completed
///     normally"; real provider ACLs set it explicitly from the wire format.</summary>
/// <param name="Content">Assistant text, or null when the model returned only tool calls.</param>
/// <param name="ToolCalls">Tool calls requested by the model; empty for a plain answer.</param>
/// <param name="FinishReason">Why the response ended.</param>
public sealed record ModelResponse(
    string? Content,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    FinishReason FinishReason = FinishReason.Stop);
