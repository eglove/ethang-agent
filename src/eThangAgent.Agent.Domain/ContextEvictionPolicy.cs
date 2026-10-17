using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain;

/// <summary>The deterministic compaction plan: how many head messages are protected,
/// how many of the oldest unprotected messages to evict, and how many remain.</summary>
public sealed record ContextEvictionPlan(int ProtectedCount, int EvictCount, int KeptCount);

/// <summary>Deterministic eviction planner (no LLM). Character budgets use the documented
/// 4-characters-per-token estimate; the next provider usage report is the truth.
/// Rules: everything through and including the first user message is a protected head
/// (system messages, prior compaction summaries, and the original task statement are
/// never evicted; no user message at all means nothing is evictable); a user message
/// starts an exchange group that runs to just before the next user message, so
/// assistant tool-call batches never split from their results; the kept tail is the
/// longest group-suffix (never fewer than the last 3 groups) fitting the policy's
/// target share of the window's character budget; eviction frees at least 15% of the
/// budget or evicts nothing.</summary>
public static class ContextEvictionPolicy
{
  public const int CharsPerTokenEstimate = 4;
  public const int MinimumEvictionPercent = 15;
  public const int MinimumKeptGroups = 3;

  public static ContextEvictionPlan Plan(IReadOnlyList<Message> messages, int contextWindow, double targetPercent)
  {
    ArgumentNullException.ThrowIfNull(messages);
    long budgetChars = (long)contextWindow * CharsPerTokenEstimate;
    long targetChars = (long)(budgetChars * targetPercent / 100);
    long minimumEviction = budgetChars * MinimumEvictionPercent / 100;

    int protectedCount = ProtectedHeadCount(messages);
    List<List<Message>> groups = SplitIntoGroups([.. messages.Skip(protectedCount)]);
    long excess = SizeOf(messages) - targetChars;
    long toFree = Math.Max(minimumEviction, excess);
    if (toFree <= 0 || groups.Count == 0)
    {
      return new ContextEvictionPlan(protectedCount, 0, messages.Count);
    }

    // Kept tail: the longest group-suffix (min 3) fitting the target budget.
    int keptGroups = Math.Min(MinimumKeptGroups, groups.Count);
    while (keptGroups < groups.Count
        && groups.GetRange(groups.Count - keptGroups - 1, keptGroups + 1).Sum(GroupSize) <= targetChars)
    {
      keptGroups++;
    }

    int evictable = groups.Count - keptGroups;
    long freed = 0;
    int evictMessages = 0;
    for (int g = 0; g < evictable; g++)
    {
      freed += GroupSize(groups[g]);
      evictMessages += groups[g].Count;
      if (freed >= toFree)
      {
        return new ContextEvictionPlan(protectedCount, evictMessages, messages.Count - evictMessages);
      }
    }

    return new ContextEvictionPlan(protectedCount, 0, messages.Count);
  }

  /// <summary>Everything through and including the first user message: system
  /// messages, compaction summaries, and the original task statement are never
  /// evicted. No user message at all means nothing is evictable.</summary>
  private static int ProtectedHeadCount(IReadOnlyList<Message> messages)
  {
    for (int i = 0; i < messages.Count; i++)
    {
      if (messages[i].Role is Role.User)
      {
        return i + 1;
      }
    }

    return messages.Count;
  }

  private static List<List<Message>> SplitIntoGroups(List<Message> messages)
  {
    List<List<Message>> groups = [];
    for (int i = 0; i < messages.Count;)
    {
      List<Message> group = [messages[i]];
      i++;
      while (i < messages.Count && messages[i].Role is not Role.User)
      {
        group.Add(messages[i]);
        i++;
      }

      groups.Add(group);
    }

    return groups;
  }

  private static long GroupSize(List<Message> group) => SizeOf(group);

  /// <summary>The per-message size estimate shared with the compactor (one size shape
  ///     across the eviction plan and the tail compression): content length plus each
  ///     tool call's name and arguments lengths.</summary>
  public static long SizeOf(IEnumerable<Message> messages)
  {
    ArgumentNullException.ThrowIfNull(messages);
    long size = 0;
    foreach (Message message in messages)
    {
      size += message.Content.Length;
      if (message.ToolCalls is { Count: > 0 } calls)
      {
        foreach (ToolCall call in calls)
        {
          size += call.Arguments.Length + call.Name.Length;
        }
      }
    }

    return size;
  }
}
