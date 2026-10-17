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

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 1_000_000, 40.0);

    Assert.Equal(0, plan.EvictCount);
    Assert.Equal(messages.Count, plan.KeptCount);
  }

  [Fact]
  public void LargePrefix_EvictsWholeGroups()
  {
    // Window 1000 tokens -> budget 4000 chars, target 40% -> 1600 chars.
    // Ten 600-char exchange groups plus a small recent group: the plan frees the
    // target-derived excess (4419 chars) by evicting whole groups from the front,
    // stopping at the first group that reaches it (15 messages: g1..g8).
    string big = new('x', 300);
    List<Message> messages =
    [
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User("recent"), Assistant("recent answer"),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 1000, 40.0);

    Assert.Equal(15, plan.EvictCount); // whole groups only: g1 (head assistant) + g2..g8
    Assert.Equal(messages.Count - 15, plan.KeptCount);
    Assert.True(plan.EvictCount + plan.ProtectedCount < messages.Count); // kept tail survives
    // The eviction boundary is a group boundary: the first kept message after the
    // evicted region starts a new exchange group.
    Assert.True(messages[plan.ProtectedCount + plan.EvictCount].Role is Role.User);
  }

  [Fact]
  public void Eviction_NeverSplits_ToolCallGroup()
  {
    // The old exchange holding the tool batch is evicted wholesale: the call and
    // its result move together, and the kept tail holds neither half.
    string big = new('x', 1000);
    List<Message> messages =
    [
      User("task"),
      AssistantToolCall("c1"), ToolResult("c1", big),
      User(big), Assistant(big),
      User("recent"), Assistant("recent"),
      User("recent"), Assistant("recent"),
      User("recent"), Assistant("recent"),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 1000, 40.0);

    Assert.Equal(4, plan.EvictCount); // the tool batch group + the big exchange group
    List<Message> evicted = [.. messages.Skip(plan.ProtectedCount).Take(plan.EvictCount)];
    List<Message> kept = [.. messages.Take(plan.ProtectedCount), .. messages.Skip(plan.ProtectedCount + plan.EvictCount)];
    HashSet<string> evictedCalls = [.. evicted.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id)];
    HashSet<string> evictedResults = [.. evicted.Where(m => m.Role is Role.Tool).Select(m => m.ToolCallId!)];
    Assert.Equal(evictedResults, evictedCalls); // both or neither: the batch never splits
    Assert.DoesNotContain(kept, m => m.ToolCalls?.Count > 0);
    Assert.DoesNotContain(kept, m => m.Role is Role.Tool);
  }

  [Fact]
  public void KeptTail_GrowsToLongestSuffixFittingTarget()
  {
    // Window 1000 tokens -> budget 4000 chars, target 40% -> 1600 chars. The kept
    // tail is the LONGEST group-suffix fitting the target: the four small recent
    // groups (16 chars total) fit, adding the big exchange group (4000 chars) does not,
    // so the suffix grows from the 3-group floor to 4 groups.
    string big = new('x', 2000);
    List<Message> messages =
    [
      User("task"), Assistant(big),
      User(big), Assistant(big),
      User("s1"), Assistant("s1"),
      User("s2"), Assistant("s2"),
      User("s3"), Assistant("s3"),
      User("s4"), Assistant("s4"),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 1000, 40.0);

    // Evictable prefix: the head assistant group + the big exchange group (3 messages,
    // 6000 chars) reaches the target-derived toFree (4420 chars: total 6020 - target 1600).
    Assert.Equal(3, plan.EvictCount);
    Assert.Equal(messages.Count - 3, plan.KeptCount);
    // The kept tail after the protected head is exactly the 4 small groups.
    Assert.True(messages[plan.ProtectedCount + plan.EvictCount].Role is Role.User);
    Assert.Equal("s1", messages[plan.ProtectedCount + plan.EvictCount].Content);
  }

  [Fact]
  public void KeptTail_FloorsAtThreeGroups_NoEvictionIntoOversizedTail()
  {
    // The last 3 groups alone (3 x 4000 chars) exceed the target share (1600 chars):
    // the floor keeps them intact, and no evictable prefix can free the target-derived
    // toFree — the plan evicts nothing rather than evicting into the tail.
    string big = new('x', 2000);
    List<Message> messages =
    [
      User("s1"), Assistant("s1"),
      User("s2"), Assistant("s2"),
      User(big), Assistant(big),
      User(big), Assistant(big),
      User(big), Assistant(big),
    ];

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 1000, 40.0);

    Assert.Equal(0, plan.EvictCount);
    Assert.Equal(messages.Count, plan.KeptCount);
  }
}
