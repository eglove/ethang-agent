namespace eThangAgent.ToolDomain;

/// <summary>The outcome of one tool execution: what enters the conversation as the
///     tool message (Content), plus an optional display title for hosts. The title
///     never enters the conversation; hosts render it in the card header beside the
///     result (the body always shows the content — the program's output for exec).</summary>
public sealed record ToolResult(string Content, bool IsError, string? Title = null);
