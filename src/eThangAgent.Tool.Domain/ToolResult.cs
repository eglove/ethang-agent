namespace eThangAgent.ToolDomain;

/// <summary>The outcome of one tool execution: what enters the conversation as the
///     tool message (Content), plus optional display metadata for hosts. The metadata
///     never enters the conversation; hosts render it beside the result.</summary>
public sealed record ToolResult(string Content, bool IsError, string? Title = null, string? DisplayBody = null);
