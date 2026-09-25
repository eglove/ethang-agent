using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Loop wiring: when the same tool call fails identically twice in one turn,
///     a "[repeat guard]" System message lands in the conversation (once per threshold),
///     and the streak never carries across turns.</summary>
public class AgentRepeatGuardTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private const string Error = "Error [MissingParameter]: Missing required parameter 'timeoutSeconds'.";

  [Fact]
  public async Task SameCallFailsTwice_InOneTurn_SystemNudgeInjectedOnce()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c2", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FailingTool("read")]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    List<Message> systemMessages = [.. agent.Conversation.Messages.Where(m => m.Role is Role.System)];
    Message nudge = Assert.Single(systemMessages);
    Assert.StartsWith("[repeat guard]", nudge.Content, StringComparison.Ordinal);
    Assert.Contains("failed 2 consecutive times", nudge.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SameCallFailsOnce_NoNudge()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FailingTool("read")]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.DoesNotContain(agent.Conversation.Messages, m => m.Role is Role.System);
  }

  [Fact]
  public async Task StreakDoesNotCarryAcrossTurns()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse("done", [])),
        // Turn 2: the same call fails once more — a fresh streak, no nudge.
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c3", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FailingTool("read")]));

    Result<string> first = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);
    Result<string> second = await agent.SendMessage("hi again", ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    Assert.DoesNotContain(agent.Conversation.Messages, m => m.Role is Role.System);
  }

  [Fact]
  public async Task NudgeSurfacedThroughOnSystemMessage()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c2", "read", /*lang=json,strict*/ "{\"p\":1}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FailingTool("read")]));
    List<string> surfaced = [];
    TurnCallbacks callbacks = new(OnSystemMessage: line => surfaced.Add(line));

    _ = await agent.SendMessage("hi", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    string? nudge = Assert.Single(surfaced);
    Assert.StartsWith("[repeat guard]", nudge, StringComparison.Ordinal);
  }

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class FailingTool(string name) : ITool
  {
    public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult(Error, true));
  }
}
