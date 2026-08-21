namespace eThangAgent.ModelDomain;

public sealed record ModelResponse(string? Content, IReadOnlyList<ToolCallRequest> ToolCalls);
