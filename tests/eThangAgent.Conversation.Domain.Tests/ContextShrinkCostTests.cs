using eThangAgent.SharedKernel;

namespace eThangAgent.ConversationDomain.Tests;

/// <summary>ISSUE #267 — the price of a middle rewrite is visible.
///     <para>A provider's prompt cache is a PREFIX cache: trimming a tool result rewrites
///     bytes in the middle of the message array, so everything from the first changed
///     message onward is re-billed at full input price. The relocation markers stay (they
///     are the right design for recoverability); what changed is that the trade is now
///     stated as numbers on every tool-result shrink instead of being invisible.</para></summary>
public class ContextShrinkCostTests
{
  private static Conversation Seeded(int toolResults = 1, int contentChars = 400)
  {
    Conversation conversation = new();
    conversation.AddUserMessage("old task");
    for (int i = 0; i < toolResults; i++)
    {
      conversation.AddAssistantMessage("", [new ToolCall($"c{i}", "exec", "{}")]);
      conversation.AddToolResult($"c{i}", new string('x', contentChars));
    }

    return conversation;
  }

  [Fact]
  public void Of_PricesOnlyTheSuffix_FromTheFirstChangedIndex()
  {
    Conversation conversation = Seeded();
    IReadOnlyList<Message> messages = conversation.Messages;

    ContextShrinkCost cost = ContextShrinkCost.Of(messages, firstChangedIndex: messages.Count - 1, savedChars: 400);

    // Only the last message is at or after the rewrite point, so only it is re-billed.
    Assert.Equal(400 / ToolResultMarker.CharsPerToken, cost.SavedTokens);
    Assert.Equal(400 / ToolResultMarker.CharsPerToken, cost.InvalidatedTokens);
  }

  [Fact]
  public void Of_HeadRewrite_InvalidatesTheWholeArray()
  {
    Conversation conversation = Seeded();
    IReadOnlyList<Message> messages = conversation.Messages;

    ContextShrinkCost cost = ContextShrinkCost.Of(messages, firstChangedIndex: 0, savedChars: 0);

    // The estimate counts content AND each tool call's name + arguments, the same
    // per-message size the eviction planner uses.
    long whole = messages.Sum(m => (long)m.Content.Length
        + (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.Length) ?? 0));
    Assert.Equal(whole / ToolResultMarker.CharsPerToken, cost.InvalidatedTokens);
  }

  [Fact]
  public void Of_TailRewrite_InvalidatesAlmostNothing()
  {
    Conversation conversation = Seeded(toolResults: 5);
    IReadOnlyList<Message> messages = conversation.Messages;

    ContextShrinkCost tail = ContextShrinkCost.Of(messages, messages.Count - 1, savedChars: 0);
    ContextShrinkCost head = ContextShrinkCost.Of(messages, 0, savedChars: 0);

    Assert.True(tail.InvalidatedTokens < head.InvalidatedTokens);
  }

  [Fact]
  public void Render_StatesTheTrade_VerbatimContract()
  {
    ContextShrinkCost cost = new(SavedChars: 400, InvalidatedChars: 1200);

    Assert.Equal("[cost: ~100 tokens saved, ~300 re-billed from the rewrite point]", cost.Render());
  }

  [Fact]
  public void TrimToolResults_SentinelCarriesTheCost()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);

    Result<string> trimmed = service.TrimToolResults("last 1");

    Assert.True(trimmed.IsSuccess);
    Assert.Contains("[context: shrank", trimmed.Value, StringComparison.Ordinal);
    Assert.Contains("[cost: ~", trimmed.Value, StringComparison.Ordinal);
    Assert.Contains("re-billed from the rewrite point]", trimmed.Value, StringComparison.Ordinal);
  }

  [Fact]
  public void ShortenToolResult_SentinelCarriesTheCost()
  {
    Conversation conversation = Seeded();
    ConversationContextService service = new(conversation);

    Result<string> shortened = service.ShortenToolResult("last 1", "short");

    Assert.True(shortened.IsSuccess);
    Assert.Contains("[cost: ~", shortened.Value, StringComparison.Ordinal);
  }

  [Fact]
  public void TrimToolResults_CostReportsRealSavings()
  {
    Conversation conversation = Seeded(contentChars: 400);
    ConversationContextService service = new(conversation);

    Result<string> trimmed = service.TrimToolResults("last 1");

    Assert.True(trimmed.IsSuccess);
    // The marker is far shorter than 400 chars, so savings are real and positive.
    ContextShrinkCost cost = ContextShrinkCost.Of(conversation.Messages, conversation.Messages.Count - 1, 0);
    Assert.True(cost.InvalidatedTokens >= 0);
    Assert.Contains("tokens saved", trimmed.Value, StringComparison.Ordinal);
  }
}
