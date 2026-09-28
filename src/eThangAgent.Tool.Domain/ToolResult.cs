namespace eThangAgent.ToolDomain;

/// <summary>The outcome of one tool execution: what enters the conversation as the
///     tool message (Content), plus an optional display title for hosts. The title
///     never enters the conversation; hosts render it in the card header beside the
///     result (the body always shows the content — the program's output for exec).
///     BypassesArchivePolicy marks a result that must never be re-archived by the
///     loop's store-and-read-back policy — the tool_output_read tool sets it, because
///     a read-back page is already a bounded read of archived content and archiving
///     it again would create a nested archive. Error results never set it.</summary>
public sealed record ToolResult(string Content, bool IsError, string? Title = null,
    IReadOnlyList<ToolResultImage>? Images = null)
{
  /// <summary>True when the loop's archive policy must pass this result through
  ///     untouched regardless of size. Only successful read-back pages set it.</summary>
  public bool BypassesArchivePolicy { get; init; }
};
