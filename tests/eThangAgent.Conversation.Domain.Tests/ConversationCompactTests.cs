using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain.Tests;

public class ConversationCompactTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Fact]
  public void Compact_ValidSummaryPlusTail_ReplacesMessages()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("old task");
    conversation.AddAssistantMessage("old answer");
    ToolCall call = new("c1", "search", "{}");
    ToolCall[] calls = [call];
    IReadOnlyList<Message> tail =
    [
      new(Role.System, "summary of the evicted prefix", T, IsSummary: true),
      new(Role.User, "new task", T),
      new(Role.Assistant, "", T, calls),
      new(Role.Tool, "result", T, ToolCallId: "c1"),
    ];

    Result<bool> result = conversation.Compact(tail);

    Assert.True(result.IsSuccess);
    Assert.Equal(4, conversation.Messages.Count);
    Assert.Equal("summary of the evicted prefix", conversation.Messages[0].Content);
    Assert.True(conversation.Messages[0].IsSummary);
  }

  [Fact]
  public void Compact_EmptyList_Rejected_ConversationUntouched()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("keep");

    Result<bool> result = conversation.Compact([]);

    Assert.False(result.IsSuccess);
    Assert.Equal("EmptyConversation", result.Error.Code);
    _ = Assert.Single(conversation.Messages);
  }

  [Fact]
  public void Compact_DanglingToolResult_RejectedWithId()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    IReadOnlyList<Message> replacement =
    [
      new(Role.Tool, "orphan", T, ToolCallId: "ghost"),
    ];

    Result<bool> result = conversation.Compact(replacement);

    Assert.False(result.IsSuccess);
    Assert.Equal("DanglingToolResult", result.Error.Code);
    Assert.Contains("ghost", result.Error.Message, StringComparison.Ordinal);
    _ = Assert.Single(conversation.Messages);
  }

  [Fact]
  public void Compact_UnansweredToolCall_Rejected()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    IReadOnlyList<Message> replacement =
    [
      new(Role.Assistant, "", T, [new ToolCall("c9", "exec", "{}")]),
    ];

    Result<bool> result = conversation.Compact(replacement);

    Assert.False(result.IsSuccess);
    Assert.Equal("UnansweredToolCall", result.Error.Code);
    _ = Assert.Single(conversation.Messages);
  }

  [Fact]
  public void AddSystemMessage_NeverSetsSummaryFlag()
  {
    Conversation conversation = new();
    conversation.AddSystemMessage("nudge");

    Assert.False(conversation.Messages[0].IsSummary);
  }
}
