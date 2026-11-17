using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain.Tests;

public class ConversationContextServiceTrimTests
{
  private static Conversation Seeded()
  {
    Message call = new(Role.Assistant, "", DateTimeOffset.UtcNow,
        [new ToolCall("c1", "read", /*lang=json,strict*/"{\"path\":\"src/big.cs\"}")]);
    Message result = new(Role.Tool, new string('x', 40_000), DateTimeOffset.UtcNow, ToolCallId: "c1");
    Message user = new(Role.User, "next step?", DateTimeOffset.UtcNow);
    return new([call, result, user]);
  }

  [Fact]
  public void TrimToolResults_ReplacesContentWithMarker_KeepsPairing()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("r1");
    Assert.True(result.IsSuccess);
    Assert.StartsWith(ConversationContextService.ShrankSentinel, result.Value, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, conversation.Messages[1].Content, StringComparison.Ordinal);
    Assert.Equal("c1", conversation.Messages[1].ToolCallId);   // pairing intact
    Assert.Equal(3, conversation.Messages.Count);              // in place, not deleted
  }

  [Fact]
  public void TrimToolResults_PositionSyntax_IsRejected_NotSilentlyRetargeted()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("2");      // a message position, not an ordinal
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSelection", result.Error.Code);
    Assert.Equal(3, conversation.Messages.Count);              // untouched
  }

  [Fact]
  public void TrimToolResults_OrdinalPastTheEnd_IsSkipped_AndTheRestTrims()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("r1,r9");
    Assert.True(result.IsSuccess);
    Assert.Contains("skipped: r9", result.Value, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, conversation.Messages[1].Content, StringComparison.Ordinal);
  }

  [Fact]
  public void TrimToolResults_AlreadyTrimmed_IsSkipped_NotDoubleCounted()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Assert.True(service.TrimToolResults("r1").IsSuccess);
    Result<string> second = service.TrimToolResults("r1");
    Assert.False(second.IsSuccess);
    Assert.Equal("NothingToTrim", second.Error.Code);
  }

  [Fact]
  public void TrimToolResults_OmittedSelection_KeepsTheRecentTail()
  {
    // Seven tool results: the default trims the first two and leaves the last five.
    List<Message> messages = [new(Role.User, "go", DateTimeOffset.UtcNow)];
    for (int i = 1; i <= 7; i++)
    {
      messages.Add(new Message(Role.Assistant, "", DateTimeOffset.UtcNow,
          [new ToolCall($"c{i}", "read", "{}")]));
      messages.Add(new Message(Role.Tool, new string('x', 5_000), DateTimeOffset.UtcNow, ToolCallId: $"c{i}"));
    }

    Conversation conversation = new(messages);
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("");
    Assert.True(result.IsSuccess);
    Assert.Contains("2 message(s)", result.Value, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, conversation.Messages[2].Content, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, conversation.Messages[4].Content, StringComparison.Ordinal);
    Assert.False(conversation.Messages[6].Content.StartsWith(ToolResultMarker.Prefix, StringComparison.Ordinal));
  }

  [Fact]
  public void TrimToolResults_DefaultWithNothingOutsideTheTail_SaysHowToProceed()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("");
    Assert.False(result.IsSuccess);
    Assert.Equal("NothingToTrim", result.Error.Code);
    Assert.Contains("most recent 5", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("selection", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Shorten_ReplacesWithExcerpt()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.Shorten("2", "the relevant section: line 42");
    Assert.True(result.IsSuccess);
    Assert.Equal("the relevant section: line 42", conversation.Messages[1].Content);
  }

  [Fact]
  public void Shorten_EmptyText_Fails()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);
    Result<string> result = service.Shorten("2", "  ");
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidText", result.Error.Code);
  }

  // Issue #222: the trimming tool's OWN call is the conversation's last message and
  // has no result yet while it runs. The aggregate must tolerate exactly that one
  // trailing unanswered call, or context_trim can never succeed.
  private static Conversation WithInFlightCall()
  {
    Message call = new(Role.Assistant, "", DateTimeOffset.UtcNow,
        [new ToolCall("c1", "read", /*lang=json,strict*/"{\"path\":\"src/big.cs\"}")]);
    Message result = new(Role.Tool, new string('x', 40_000), DateTimeOffset.UtcNow, ToolCallId: "c1");
    Message inFlight = new(Role.Assistant, "", DateTimeOffset.UtcNow,
        [new ToolCall("c2", "context_trim", /*lang=json,strict*/"{\"action\":\"Trim\",\"selection\":\"2\"}")]);
    return new([call, result, inFlight]);
  }

  [Fact]
  public void TrimToolResults_WhileItsOwnCallIsInFlight_Succeeds()
  {
    Conversation conversation = WithInFlightCall();
    ConversationContextService service = new(conversation);
    Result<string> result = service.TrimToolResults("r1");
    Assert.True(result.IsSuccess);
    Assert.StartsWith(ConversationContextService.ShrankSentinel, result.Value, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, conversation.Messages[1].Content, StringComparison.Ordinal);
    Assert.Equal(3, conversation.Messages.Count);                          // in place, not deleted
    Assert.Equal("c2", conversation.Messages[2].ToolCalls![0].Id);        // in-flight call kept
  }

  [Fact]
  public void Shorten_WhileItsOwnCallIsInFlight_Succeeds()
  {
    Conversation conversation = WithInFlightCall();
    ConversationContextService service = new(conversation);
    Result<string> result = service.Shorten("2", "the relevant section");
    Assert.True(result.IsSuccess);
    Assert.Equal("the relevant section", conversation.Messages[1].Content);
  }

  [Fact]
  public void TrimToolResults_UnansweredCallBeforeTheTrailingCall_StillFails()
  {
    // Only the TRAILING unanswered call is the in-flight one; an earlier unanswered
    // call is still a protocol violation and must be refused.
    Message orphan = new(Role.Assistant, "", DateTimeOffset.UtcNow, [new ToolCall("c0", "exec", "{}")]);
    Message call = new(Role.Assistant, "", DateTimeOffset.UtcNow, [new ToolCall("c1", "read", "{}")]);
    Message result = new(Role.Tool, new string('x', 40_000), DateTimeOffset.UtcNow, ToolCallId: "c1");
    Message inFlight = new(Role.Assistant, "", DateTimeOffset.UtcNow, [new ToolCall("c2", "context_trim", "{}")]);
    Conversation conversation = new([orphan, call, result, inFlight]);
    ConversationContextService service = new(conversation);

    Result<string> trimmed = service.TrimToolResults("r1");

    Assert.False(trimmed.IsSuccess);
    Assert.Equal("UnansweredToolCall", trimmed.Error.Code);
    Assert.Equal(4, conversation.Messages.Count);                          // untouched
  }
}
