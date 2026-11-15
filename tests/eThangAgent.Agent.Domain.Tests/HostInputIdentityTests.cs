using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A resent turn delivers its user input EXACTLY ONCE.
///     <para>THE DEFECT THIS PINS: a host that dies after persisting the user's message is
///     reconnected and the turn is resent, so the replacement host hydrates a conversation
///     that already holds that input and appends it again - the model reads the request
///     twice and the transcript shows it twice.</para>
///     <para>THE KEY IS IDENTITY, NEVER TEXT (the rule the transcript's retraction path
///     already states): the same sentence typed in two consecutive turns is two messages and
///     must reach the model twice. A sender that names no identity keeps today's behaviour
///     exactly - one append, no deduplication - which is what keeps an older app working.</para>
///     </summary>
public class HostInputIdentityTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  private static Agent NewAgent(IModelProvider provider, Conversation conversation) =>
      new(provider, conversation, DefaultConfig, new ToolRegistry([]),
          new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });

  /// <summary>A resent turn appends nothing when the conversation already holds its input, so
  ///     the model reads the request once.</summary>
  [Fact]
  public async Task AResentTurn_AppendsTheInputOnce()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("carry on", userMessageId: "submit-7");
    ScriptedModelProvider provider = new(Result.Success(new ModelResponse("the answer", [])));
    Agent agent = NewAgent(provider, conversation);

    Result<string> result = await agent.SendMessage("carry on", userMessageId: "submit-7",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(conversation.Messages, m => m.Role is Role.User);
  }

  /// <summary>A turn sent once, with no prior copy, appends exactly once.</summary>
  [Fact]
  public async Task AnOrdinaryTurn_AppendsOnce()
  {
    Conversation conversation = new();
    ScriptedModelProvider provider = new(Result.Success(new ModelResponse("the answer", [])));
    Agent agent = NewAgent(provider, conversation);

    Result<string> result = await agent.SendMessage("hello", userMessageId: "submit-9",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(conversation.Messages, m => m.Role is Role.User);
  }

  /// <summary>A sender that names no identity keeps today's behaviour: a second turn with the
  ///     same text is a second message, because there is no key to compare.</summary>
  [Fact]
  public async Task ARequestWithNoIdentity_AppendsEveryTime()
  {
    Conversation conversation = new();
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse("one", [])),
        Result.Success(new ModelResponse("two", [])));
    Agent agent = NewAgent(provider, conversation);

    _ = await agent.SendMessage("hello", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    _ = await agent.SendMessage("hello", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal(2, conversation.Messages.Count(m => m.Role is Role.User));
  }

  /// <summary>THE RULE THAT MAKES TEXT THE WRONG KEY: the same sentence typed twice in one
  ///     session is two messages, because each submit mints its own identity.</summary>
  [Fact]
  public async Task TheSameSentenceTwice_ReachesTheModelTwice()
  {
    Conversation conversation = new();
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse("one", [])),
        Result.Success(new ModelResponse("two", [])));
    Agent agent = NewAgent(provider, conversation);

    _ = await agent.SendMessage("again", userMessageId: "submit-1",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await agent.SendMessage("again", userMessageId: "submit-2",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(2, conversation.Messages.Count(m => m.Role is Role.User));
  }

  /// <summary>The conversation treats the identity as its business key, and an absent or empty
  ///     one as no key at all.</summary>
  [Fact]
  public void TheConversation_HoldsAnIdentity_AndNeverHoldsAnEmptyOne()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("carry on", userMessageId: "submit-7");

    Assert.True(conversation.HoldsUserMessage("submit-7"));
    Assert.False(conversation.HoldsUserMessage("submit-8"));
    Assert.False(conversation.HoldsUserMessage(null));
    Assert.False(conversation.HoldsUserMessage(string.Empty));
  }

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Result<ModelResponse>[] _responses = responses;
    private int _next;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      int index = Math.Min(_next++, _responses.Length - 1);
      return Task.FromResult(_responses[index]);
    }
  }
}
