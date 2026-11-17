namespace eThangAgent.ConversationDomain.Tests;

public class ConversationContextServiceListTests
{
  private static Conversation Seeded(int results)
  {
    List<Message> messages = [new(Role.User, "go", DateTimeOffset.UtcNow)];
    for (int i = 1; i <= results; i++)
    {
      messages.Add(new Message(Role.Assistant, "", DateTimeOffset.UtcNow,
          [new ToolCall($"c{i}", "read", /*lang=json,strict*/$"{{\"path\":\"src/f{i}.cs\"}}")]));
      messages.Add(new Message(Role.Tool, new string('x', i * 1_000), DateTimeOffset.UtcNow, ToolCallId: $"c{i}"));
    }

    return new(messages);
  }

  [Fact]
  public void ListTrimmable_ShowsOrdinalPositionToolAndSize()
  {
    Conversation conversation = Seeded(3);
    ConversationContextService service = new(conversation);
    string listing = service.ListTrimmable();
    Assert.Contains("r1", listing, StringComparison.Ordinal);
    Assert.Contains("msg 3", listing, StringComparison.Ordinal);
    Assert.Contains("read", listing, StringComparison.Ordinal);
    Assert.Contains("src/f1.cs", listing, StringComparison.Ordinal);
  }

  [Fact]
  public void ListTrimmable_IsBiggestFirst()
  {
    Conversation conversation = Seeded(3);
    ConversationContextService service = new(conversation);
    string listing = service.ListTrimmable();
    int biggest = listing.IndexOf("r3", StringComparison.Ordinal);
    int smallest = listing.IndexOf("r1", StringComparison.Ordinal);
    Assert.True(biggest < smallest, $"expected r3 before r1, got:\n{listing}");
  }

  [Fact]
  public void ListTrimmable_OmitsAlreadyTrimmedResults()
  {
    Conversation conversation = Seeded(3);
    ConversationContextService service = new(conversation);
    Assert.True(service.TrimToolResults("r2").IsSuccess);
    string listing = service.ListTrimmable();
    Assert.DoesNotContain("r2\t", listing, StringComparison.Ordinal);
    Assert.Contains("r3", listing, StringComparison.Ordinal);
  }

  [Fact]
  public void ListTrimmable_OrdinalsStayStable_AfterATrim()
  {
    // r3 must still be the third tool result after r1 is trimmed, or a model acting
    // on a stale listing would trim the wrong result.
    Conversation conversation = Seeded(3);
    ConversationContextService service = new(conversation);
    Assert.True(service.TrimToolResults("r1").IsSuccess);
    string listing = service.ListTrimmable();
    Assert.Contains("r3", listing, StringComparison.Ordinal);
    Assert.Contains("msg 7", listing, StringComparison.Ordinal);
  }

  [Fact]
  public void ListTrimmable_NothingToTrim_SaysSo()
  {
    Conversation conversation = new([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]);
    ConversationContextService service = new(conversation);
    Assert.Equal("[context: no trimmable tool results]", service.ListTrimmable());
  }

  [Fact]
  public void ListTrimmable_CapsTheRows_AndSaysHowManyWereLeftOut()
  {
    Conversation conversation = Seeded(ConversationContextService.ListLimit + 5);
    ConversationContextService service = new(conversation);
    string listing = service.ListTrimmable();
    Assert.Contains("5 smaller one(s) omitted", listing, StringComparison.Ordinal);
  }
}
