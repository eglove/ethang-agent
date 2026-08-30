using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain.Tests;

public class ContextEvictionPolicyTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static Message User(string text) => new(Role.User, text, T);
  private static Message Assistant(string text) => new(Role.Assistant, text, T);
  private static Message AssistantToolCall(string id) => new(Role.Assistant, "", T, [new ToolCall(id, "tool", "{}")]);
  private static Message ToolResult(string id, string text) => new(Role.Tool, text, T, ToolCallId: id);

  [Fact]
  public void SmallConversation_EvictsNothing()
  {
    List<Message> messages = [User("a"), Assistant("b"), User("c"), Assistant("d")];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, contextWindow: 1_000_000);

    Assert.Equal(0, plan.EvictCount);
    Assert.Equal(messages.Count, plan.KeptCount);
  }

  [Fact]
  public void LargePrefix_EvictsWholeGroups_ReachingMinimum()
  {
    // Window 1000 tokens → budget 4000 chars, minimum eviction 600 chars.
    // Each old exchange group is ~1000 chars: evicting one group frees >= 600.
    string big = new('x', 1000);
    List<Message> messages =
    [
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User("recent"), Assistant("recent answer"),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, contextWindow: 1000);

    Assert.Equal(2, plan.EvictCount); // exactly one whole group (user + assistant)
    Assert.Equal(messages.Count - 2, plan.KeptCount);
  }

  [Fact]
  public void Eviction_NeverSplits_ToolCallGroup()
  {
    // One old exchange containing a tool batch; the batch must move together.
    string big = new('x', 1000);
    List<Message> messages =
    [
      User(big),
      AssistantToolCall("c1"), ToolResult("c1", big),
      Assistant(big),
      User("recent"), Assistant("recent"),
      User("recent2"), Assistant("recent2"),
      User("recent3"), Assistant("recent3"),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, contextWindow: 1000);

    if (plan.EvictCount > 0)
    {
      List<Message> evicted = [.. messages.Take(plan.EvictCount)];
      bool hasCall = evicted.Any(m => m.ToolCalls?.Count > 0);
      bool hasResult = evicted.Any(m => m.Role is Role.Tool);
      Assert.Equal(hasCall, hasResult); // both or neither: the batch never splits
    }
  }

  [Fact]
  public void KeptTail_FloorsAtThreeGroups_EvenWhenTiny()
  {
    string big = new('x', 2000);
    List<Message> messages =
    [
      User(big), Assistant(big),
      User(big), Assistant(big),
      User("a"), Assistant("b"),
      User("c"), Assistant("d"),
      User("e"), Assistant("f"),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, contextWindow: 1000);

    // The last 3 groups (6 messages) survive: keptGroups floors at 3 even though the
    // big older groups would fit no tail budget. Eviction stops at the 15% minimum,
    // so exactly one 4000-char group is evicted.
    Assert.Equal(2, plan.EvictCount);
    Assert.Equal(messages.Count - 2, plan.KeptCount);
  }
}
