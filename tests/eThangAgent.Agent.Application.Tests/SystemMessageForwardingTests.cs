using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>SendMessageCommandHandler forwards the nudge line it appends as a System
///     message through TurnCallbacks.OnSystemMessage, so hosts surface it live. Silent
///     policy: no callback fire. Handler-level complement of the Agent-domain tests.</summary>
public class SystemMessageForwardingTests
{
  [Fact]
  public async Task Handle_NudgeAppended_FiresOnSystemMessage()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("ok", [])));
    (Ag agent, Conversation conversation) = BuildAgent(provider);
    CountingPolicy policy = new("[nudge] remember to curate");
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 0);

    List<string> reported = [];
    Result<string> result = await handler.Handle(new SendMessageCommand("hello"),
        new TurnCallbacks(OnSystemMessage: reported.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string line = Assert.Single(reported);
    Assert.Equal("[nudge] remember to curate", line);
  }

  [Fact]
  public async Task Handle_SilentPolicy_NeverFiresOnSystemMessage()
  {
    ScriptedProvider provider = new(Result.Success(new ModelResponse("ok", [])));
    (Ag agent, Conversation conversation) = BuildAgent(provider);
    CountingPolicy policy = new(null);
    SendMessageCommandHandler handler = new(agent, conversation, policy, () => 0);

    List<string> reported = [];
    _ = await handler.Handle(new SendMessageCommand("hello"),
        new TurnCallbacks(OnSystemMessage: reported.Add), ct: TestContext.Current.CancellationToken);

    Assert.Empty(reported);
    Assert.Equal(2, conversation.Messages.Count);
  }

  private static (Ag Agent, Conversation Conversation) BuildAgent(IModelProvider provider)
  {
    Conversation conversation = new();
    Ag agent = new(provider, conversation,
        ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!, new ToolRegistry([]));
    return (agent, conversation);
  }

  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _queue = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_queue.Count > 0 ? _queue.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class CountingPolicy(string? line) : INudgePolicy
  {
    public string? Evaluate(NudgeContext context) => line;
  }
}
