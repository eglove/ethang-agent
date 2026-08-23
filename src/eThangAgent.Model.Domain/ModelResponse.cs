namespace eThangAgent.ModelDomain;

/// <param name="FinishReason">Why the response ended. Defaults to <see cref="FinishReason.Stop"/>
///     — the named-leniency default so simple fakes and legacy callers mean "completed
///     normally"; real provider ACLs set it explicitly from the wire format.</param>
public sealed record ModelResponse(
    string? Content,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    FinishReason FinishReason = FinishReason.Stop);
