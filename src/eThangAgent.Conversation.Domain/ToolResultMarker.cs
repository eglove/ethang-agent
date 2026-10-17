namespace eThangAgent.ConversationDomain;

/// <summary>Renders the relocation marker that replaces an evicted or trimmed tool
///     result: it names the tool call that produced the result and how to re-fetch it —
///     nothing vanishes without a trace of how to get it back (design decision).</summary>
public static class ToolResultMarker
{
  public const string Prefix = "[trimmed:";

  /// <summary>Characters per token, the same documented estimate the eviction
  ///     planner uses; the next provider usage report is the truth.</summary>
  public const int CharsPerToken = 4;

  /// <summary>Renders the marker for the tool result at <paramref name="toolResultIndex"/>:
  ///     the paired tool call's name and (truncated) arguments when one exists earlier
  ///     in the list, the generic form otherwise. Deterministic — no LLM call.</summary>
  public static string For(IReadOnlyList<Message> messages, int toolResultIndex)
  {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentOutOfRangeException.ThrowIfNegative(toolResultIndex);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toolResultIndex, messages.Count);
    Message result = messages[toolResultIndex];
    int estTokens = result.Content.Length / CharsPerToken;
    ToolCall? call = null;
    for (int i = toolResultIndex - 1; i >= 0 && call is null; i--)
    {
      call = messages[i].ToolCalls?.FirstOrDefault(c => c.Id == result.ToolCallId);
    }

    string marker = call is { } c
        ? $"{Prefix} {c.Name}({Truncate(c.Arguments, 80)}) — original result ~{estTokens} tokens evicted; re-run the tool if needed]"
        : $"{Prefix} tool result ~{estTokens} tokens evicted; re-run the tool if needed]";
    return marker;
  }

  private static string Truncate(string text, int max)
      => text.Length <= max ? text : text[..max] + "…";
}
