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

/// <summary>THE PRICE OF A MIDDLE REWRITE (issue #267). Trimming or shortening a tool
///     result rewrites bytes in the MIDDLE of the message array, and a provider's prompt
///     cache is a PREFIX cache: everything from the first changed byte onward is
///     re-billed at full input price on the next request. The relocation markers are the
///     right design for recoverability — nothing vanishes without a way to re-fetch it —
///     but until this type existed the harness could not say what a trim COST, so the
///     trade against its savings was invisible.
///     <para>Both numbers are deterministic character estimates at the documented
///     <see cref="ToolResultMarker.CharsPerToken"/> rate; the provider's next usage
///     report is the truth, and the <c>agent_events</c> usage rows are where that truth
///     is measured (see the audit record on issue #267).</para>
///     <para>This is a REPORT, never a gate: the trim proceeds regardless. It exists so
///     the model and the transcript can see the trade it just made, which is the whole
///     point of pricing it.</para></summary>
public sealed record ContextShrinkCost(long SavedChars, long InvalidatedChars)
{
  /// <summary>The estimated tokens this rewrite frees (the removed characters).</summary>
  public long SavedTokens => SavedChars / ToolResultMarker.CharsPerToken;

  /// <summary>The estimated tokens re-billed on the next request: every character from
  ///     the FIRST rewritten position through the end of the array. A trim near the tail
  ///     invalidates almost nothing; a trim near the head invalidates nearly everything.</summary>
  public long InvalidatedTokens => InvalidatedChars / ToolResultMarker.CharsPerToken;

  /// <summary>Prices one rewrite of <paramref name="messages"/>. <paramref name="firstChangedIndex"/>
  ///     is the 0-based position of the earliest message whose bytes changed; everything
  ///     before it is a stable prefix and stays cached. <paramref name="savedChars"/> is
  ///     the total character reduction across the whole rewrite.</summary>
  public static ContextShrinkCost Of(IReadOnlyList<Message> messages, int firstChangedIndex, long savedChars)
  {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentOutOfRangeException.ThrowIfNegative(firstChangedIndex);
    long invalidated = 0;
    for (int i = Math.Min(firstChangedIndex, messages.Count); i < messages.Count; i++)
    {
      invalidated += MessageChars(messages[i]);
    }

    return new ContextShrinkCost(savedChars, invalidated);
  }

  /// <summary>The per-message size estimate shared with the eviction planner: content
  ///     length plus each tool call's name and arguments lengths.</summary>
  private static long MessageChars(Message message)
  {
    long size = message.Content.Length;
    if (message.ToolCalls is { Count: > 0 } calls)
    {
      foreach (ToolCall call in calls)
      {
        size += call.Arguments.Length + call.Name.Length;
      }
    }

    return size;
  }

  /// <summary>The one-line render appended to a shrink's sentinel: the trade, stated as
  ///     numbers. Verbatim contract, pinned by tests:
  ///     <c>[cost: ~N tokens saved, ~M re-billed from the rewrite point]</c>.</summary>
  public string Render() =>
      $"[cost: ~{SavedTokens} tokens saved, ~{InvalidatedTokens} re-billed from the rewrite point]";
}
