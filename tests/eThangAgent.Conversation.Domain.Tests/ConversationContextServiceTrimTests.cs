using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain.Tests;

public class ConversationContextServiceTrimTests
{
  private static Conversation Seeded()
  {
    Message call = new(Role.Assistant, "", DateTimeOffset.UtcNow,
        [new ToolCall("c1", "read", "{\"path\":\"src/big.cs\"}")]);
    Message result = new(Role.Tool, new string('x', 40_000), DateTimeOffset.UtcNow, ToolCallId: "c1");
    Message user = new(Role.User, "next step?", DateTimeOffset.UtcNow);
    return new([call, result, user]);
  }

  [Fact]
  public void TrimToolResults_ReplacesContentWithMarker_KeepsPairing()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("2");
    Assert.True(result.IsSuccess);
    Assert.StartsWith(ConversationContextService.ShrankSentinel, result.Value, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, conversation.Messages[1].Content, StringComparison.Ordinal);
    Assert.Equal("c1", conversation.Messages[1].ToolCallId);   // pairing intact
    Assert.Equal(3, conversation.Messages.Count);              // in place, not deleted
  }

  [Fact]
  public void TrimToolResults_NonToolSelection_FailsTyped()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("3");      // the user message
    Assert.False(result.IsSuccess);
    Assert.Equal("ToolOnlySelection", result.Error.Code);
    Assert.Equal(3, conversation.Messages.Count);              // untouched
  }

  [Fact]
  public void ShortenToolResult_ReplacesWithExcerpt()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.ShortenToolResult("2", "the relevant section: line 42");
    Assert.True(result.IsSuccess);
    Assert.Equal("the relevant section: line 42", conversation.Messages[1].Content);
  }

  [Fact]
  public void ShortenToolResult_EmptyText_Fails()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.ShortenToolResult("2", "  ");
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidText", result.Error.Code);
  }
}
