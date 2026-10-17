using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;

namespace eThangAgent.Agent.Domain.Tests;

public class ContextEvictionPolicyTargetTests
{
  private static Message User(string t) => new(Role.User, t, DateTimeOffset.UtcNow);
  private static Message Assistant(string t) => new(Role.Assistant, t, DateTimeOffset.UtcNow);

  [Fact]
  public void Plan_ProtectsHeadThroughFirstUserMessage()
  {
    // system, first user (protected), then 10 large user groups
    List<Message> messages = [new(Role.System, "notice", DateTimeOffset.UtcNow), User("task")];
    for (int i = 0; i < 10; i++)
    {
      messages.Add(User(new string('x', 40_000)));
      messages.Add(Assistant("ok"));
    }

    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 128_000, 40.0);
    Assert.Equal(2, plan.ProtectedCount);                       // system + first user
    Assert.True(plan.EvictCount > 0);
    // The protected head survives: evicted region starts AFTER it.
    Assert.Equal("task", messages[plan.ProtectedCount - 1].Content);
  }

  [Fact]
  public void Plan_NoUserMessage_EvictsNothing()
  {
    List<Message> messages = [new(Role.System, "only", DateTimeOffset.UtcNow)];
    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 128_000, 40.0);
    Assert.Equal(0, plan.EvictCount);
  }

  [Fact]
  public void Plan_SmallExcess_EvictsNothing()
  {
    List<Message> messages = [User("task"), User(new string('x', 100))]; // far below target
    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 1_000_000, 25.0);
    Assert.Equal(0, plan.EvictCount);
  }

  [Fact]
  public void Plan_NeverSplitsToolCallFromResult()
  {
    Message call = new(Role.Assistant, "", DateTimeOffset.UtcNow,
        [new ToolCall("c1", "read", "{}")]);
    Message result = new(Role.Tool, new string('y', 40_000), DateTimeOffset.UtcNow, ToolCallId: "c1");
    List<Message> messages = [User("task"), User(new string('a', 40_000)), Assistant("done"), call, result, User(new string('b', 40_000))];
    ContextEvictionPlan plan = ContextEvictionPolicy.Plan(messages, 128_000, 40.0);
    // Whatever is evicted, the call/result pair stays together: simulate the split.
    List<Message> kept = [.. messages.Take(plan.ProtectedCount), .. messages.Skip(plan.ProtectedCount + plan.EvictCount)];

    // Strengthened (mirror Conversation.Compact's invariants): no unanswered tool
    // call and no dangling tool result in the kept list.
    HashSet<string> keptCallIds = [.. kept.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id)];
    HashSet<string> keptResultIds = [.. kept.Where(m => m.Role is Role.Tool).Select(m => m.ToolCallId!)];
    Assert.Empty(keptCallIds.Except(keptResultIds)); // every kept call has its result kept
    Assert.Empty(keptResultIds.Except(keptCallIds)); // every kept result has its call kept
  }
}
