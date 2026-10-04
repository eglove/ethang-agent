namespace eThangAgent.ConversationDomain.Tests;

public class ConversationMutationEventsTests
{
  [Fact]
  public void Subscribe_AddUserMessage_FiresEventWithExactMessage()
  {
    Conversation c = new();
    Message? observed = null;
    using IDisposable sub = c.Subscribe(onAdded: m => observed = m);
    c.AddUserMessage("Hello");
    Assert.NotNull(observed);
    Assert.Same(c.Messages[0], observed);
  }

  [Fact]
  public void Subscribe_EveryMutationKind_FiresMessageAdded()
  {
    Conversation c = new();
    List<Message> observed = [];
    using IDisposable sub = c.Subscribe(onAdded: observed.Add);
    c.AddUserMessage("u");
    c.AddAssistantMessage("a");
    c.AddAssistantMessage("calls", [new ToolCall("c1", "exec", "{}")]);
    c.AddToolResult("c1", "result");
    c.AddSystemMessage("sys");
    Assert.Equal(5, observed.Count);
    Assert.Equal(Role.User, observed[0].Role);
    Assert.Equal(Role.Assistant, observed[1].Role);
    Assert.Equal(Role.Assistant, observed[2].Role);
    Assert.Equal(Role.Tool, observed[3].Role);
    Assert.Equal("c1", observed[3].ToolCallId);
    Assert.Equal(Role.System, observed[4].Role);
  }

  [Fact]
  public void Subscribe_Compact_FiresReplacedNotAdded()
  {
    Conversation c = new();
    c.AddUserMessage("u");
    int added = 0;
    int replaced = 0;
    using IDisposable sub = c.Subscribe(onAdded: _ => added++, onReplaced: () => replaced++);
    _ = c.Compact([new Message(Role.System, "summary", DateTimeOffset.UtcNow, IsSummary: true)]);
    Assert.Equal(0, added);
    Assert.Equal(1, replaced);
  }

  [Fact]
  public void Dispose_StopsEvents()
  {
    Conversation c = new();
    int count = 0;
    using IDisposable sub = c.Subscribe(onAdded: _ => count++);
    c.AddUserMessage("first");
    sub.Dispose();
    c.AddUserMessage("second");
    Assert.Equal(1, count);
  }
}
