using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain;

/// <summary>The deterministic compaction plan: how many of the oldest messages to evict
///     and how many remain.</summary>
public sealed record ContextEvictionPlan(int EvictCount, int KeptCount);

/// <summary>Deterministic eviction planner (no LLM). Character budgets use the documented
///     4-characters-per-token estimate; the next provider usage report is the truth.
///     Rules: a user message starts an exchange group that runs to just before the next
///     user message, so assistant tool-call batches never split from their results; the
///     kept tail is the longest group-suffix whose size fits 25% of the window's character
///     budget and is never fewer than the last 3 groups; eviction frees at least 15% of
///     the budget or evicts nothing.</summary>
public static class ContextEvictionPolicy
{
  public const int CharsPerTokenEstimate = 4;
  public const int TailBudgetPercent = 25;
  public const int MinimumEvictionPercent = 15;
  public const int MinimumKeptGroups = 3;

  public static ContextEvictionPlan Plan(IReadOnlyList<Message> messages, int contextWindow)
  {
    ArgumentNullException.ThrowIfNull(messages);
    long budgetChars = (long)contextWindow * CharsPerTokenEstimate;
    long tailBudget = budgetChars * TailBudgetPercent / 100;
    long minimumEviction = budgetChars * MinimumEvictionPercent / 100;

    List<List<Message>> groups = SplitIntoGroups(messages);

    int keptGroups = Math.Min(MinimumKeptGroups, groups.Count);
    while (keptGroups < groups.Count
        && groups.GetRange(groups.Count - keptGroups - 1, keptGroups + 1).Sum(GroupSize) <= tailBudget)
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
      if (freed >= minimumEviction)
      {
        return new ContextEvictionPlan(evictMessages, messages.Count - evictMessages);
      }
    }

    return new ContextEvictionPlan(0, messages.Count);
  }

  private static List<List<Message>> SplitIntoGroups(IReadOnlyList<Message> messages)
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

  private static long GroupSize(List<Message> group) => Size(group);

  private static long Size(IEnumerable<Message> messages)
  {
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
