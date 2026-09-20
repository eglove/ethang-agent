using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain.Tests;

public class ConversationContextServiceTests
{
  /// <summary>Seeds one complete exchange pair: user task, assistant tool call,
  ///     tool result, assistant answer.</summary>
  private static Conversation SeededConversation()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("old task");
    conversation.AddAssistantMessage("", [new ToolCall("c1", "exec", "{}")]);
    conversation.AddToolResult("c1", "result text");
    conversation.AddAssistantMessage("done");
    return conversation;
  }

  // ---- List ----

  [Fact]
  public void List_RenderContract_IndexedRoleLines()
  {
    ConversationContextService service = new(SeededConversation());
    string rendered = service.List();
    string[] lines = rendered.Split('\n');
    Assert.Equal(4, lines.Length);
    Assert.Equal("[1] [User] old task", lines[0]);
    Assert.Equal("[2] [Assistant] tool_call(c1): exec({})", lines[1]);
    Assert.Equal("[3] [Tool] result text [answers c1]", lines[2]);
    Assert.Equal("[4] [Assistant] done", lines[3]);
  }

  [Fact]
  public void List_MultiLineContent_ContinuationLinesIndented()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("line one\nline two");
    ConversationContextService service = new(conversation);
    string[] lines = service.List().Split('\n');
    Assert.Equal(2, lines.Length);
    Assert.Equal("[1] [User] line one", lines[0]);
    Assert.Equal("  line two", lines[1]);
  }

  [Fact]
  public void List_AfterRemove_IndexesRenumbered()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    Result<string> removed = service.Remove("2..3");
    Assert.True(removed.IsSuccess);
    string[] lines = service.List().Split('\n');
    Assert.Equal(2, lines.Length);
    Assert.Equal("[1] [User] old task", lines[0]);
    Assert.Equal("[2] [Assistant] done", lines[1]);
  }

  // ---- Remove ----

  [Fact]
  public void Remove_MiddleRange_PairTogether_Succeeds()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    Result<string> removed = service.Remove("2..3");
    Assert.True(removed.IsSuccess);
    Assert.Contains("[context: shrank 2 message(s)", removed.Value, StringComparison.Ordinal);
    Assert.Equal(2, conversation.Messages.Count);
  }

  [Fact]
  public void Remove_ToolResultOnly_Fails_UnansweredToolCall_ConversationUntouched()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    Result<string> removed = service.Remove("3");
    Assert.False(removed.IsSuccess);
    Assert.Equal("UnansweredToolCall", removed.Error.Code);
    Assert.Equal(4, conversation.Messages.Count);
  }

  [Fact]
  public void Remove_LeavesAssistantHead_Fails_OrphanHead()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    Result<string> removed = service.Remove("1");
    Assert.False(removed.IsSuccess);
    Assert.Equal("OrphanHead", removed.Error.Code);
    Assert.Equal(4, conversation.Messages.Count);
  }

  [Fact]
  public void Remove_EmptySelection_Fails_NothingSelected()
  {
    ConversationContextService service = new(SeededConversation());
    Result<string> removed = service.Remove("last 0");
    Assert.False(removed.IsSuccess);
    Assert.Equal("NothingSelected", removed.Error.Code);
  }

  [Fact]
  public void Remove_RangePastEnd_Fails_PositionOutOfRange()
  {
    ConversationContextService service = new(SeededConversation());
    Result<string> removed = service.Remove("3..9");
    Assert.False(removed.IsSuccess);
    Assert.Equal("PositionOutOfRange", removed.Error.Code);
  }

  [Fact]
  public void Remove_UnparseableSelection_Fails_InvalidSelection()
  {
    ConversationContextService service = new(SeededConversation());
    Result<string> removed = service.Remove("everything");
    Assert.False(removed.IsSuccess);
    Assert.Equal("InvalidSelection", removed.Error.Code);
  }

  // ---- Shorten ----

  [Fact]
  public void Shorten_SingleMessage_ReplacesContent_PreservesProtocolMetadata()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    Result<string> shortened = service.Shorten("4", "done, briefly");
    Assert.True(shortened.IsSuccess);
    Assert.Contains("[context: shrank 1 message(s)", shortened.Value, StringComparison.Ordinal);
    Message edited = conversation.Messages[3];
    Assert.Equal("done, briefly", edited.Content);
    Assert.Equal(Role.Assistant, edited.Role);
    Assert.Equal(conversation.Messages[3].Timestamp, edited.Timestamp);
  }

  [Fact]
  public void Shorten_ToolResult_PreservesToolCallId_PairStaysValid()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    Result<string> shortened = service.Shorten("3", "result (condensed)");
    Assert.True(shortened.IsSuccess);
    Assert.Equal("c1", conversation.Messages[2].ToolCallId);
    Assert.Equal(Role.Tool, conversation.Messages[2].Role);
  }

  [Fact]
  public void Shorten_MultiMessageSelection_Fails_AmbiguousSelection()
  {
    ConversationContextService service = new(SeededConversation());
    Result<string> shortened = service.Shorten("1..2", "x");
    Assert.False(shortened.IsSuccess);
    Assert.Equal("AmbiguousSelection", shortened.Error.Code);
  }

  [Fact]
  public void Shorten_EmptyText_Fails_InvalidText()
  {
    ConversationContextService service = new(SeededConversation());
    Result<string> shortened = service.Shorten("4", "   ");
    Assert.False(shortened.IsSuccess);
    Assert.Equal("InvalidText", shortened.Error.Code);
  }

  [Fact]
  public void Shorten_DoesNotChangeCount()
  {
    Conversation conversation = SeededConversation();
    ConversationContextService service = new(conversation);
    _ = service.Shorten("1", "old task (unchanged meaning)");
    Assert.Equal(4, conversation.Messages.Count);
  }
}
