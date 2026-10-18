using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Loop wiring of the repeat guard's critical escalation: the 13th
///     breaker-counted call injects a critical System line; the NEXT breaker-counted
///     call ENDS the turn — remaining calls in the batch get the synthetic
///     InterruptedToolResult, a final system line names why, and SendMessage returns
///     Failure(RepeatGuardStop). The synthetic results never extend the streaks.</summary>
public class AgentRepeatGuardStopTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private const string Error = "Error [ExecParseError]: program failed validation.";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class CountingTool : ITool
  {
    public int _executed;

    public ToolDefinition Definition { get; } = new("exec", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      _executed++;
      return Task.FromResult(new ToolResult(Error, true));
    }
  }

  [Fact]
  public async Task ThirteenthFailure_CriticalLineInjected_AsSystemMessage()
  {
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 13; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([new CountingTool()]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message critical = Assert.Single(agent.Conversation.Messages,
        m => m.Role is Role.System && m.Content.Contains("critical error", StringComparison.Ordinal));
    Assert.StartsWith("[repeat guard] CRITICAL:", critical.Content, StringComparison.Ordinal);
    Assert.Contains("13 consecutive", critical.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FourteenthFailure_TurnEndsWithRepeatGuardStop()
  {
    CountingTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 14; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.RepeatGuardStopCode, result.Error.Code);
    Assert.Equal(14, tool._executed); // every call in the ending batch still executed
  }

  [Fact]
  public async Task FourteenthFailure_RemainingBatchCalls_GetSyntheticResult()
  {
    CountingTool tool = new();
    // The 14th call arrives in a batch WITH a trailing call: the batch stops there.
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 13; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse(null,
        [new ToolCallRequest("c14", "exec", Args(14)), new ToolCallRequest("c15", "exec", Args(15))])));
    responses.Add(Result.Success(new ModelResponse("never reached", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.RepeatGuardStopCode, result.Error.Code);
    Assert.Equal(14, tool._executed);
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Message synthetic = messages.Single(m => m.Role is Role.Tool && m.ToolCallId == "c15");
    Assert.Equal(Agent.InterruptedToolResult, synthetic.Content);
    // The 14th call itself ran and carries its real (failing) result.
    Message real = messages.Single(m => m.Role is Role.Tool && m.ToolCallId == "c14");
    Assert.Equal(Error, real.Content);
  }

  [Fact]
  public async Task FourteenthFailure_ConversationEndsOnFinalSystemLine()
  {
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 14; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse("never reached", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([new CountingTool()]));

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Message last = agent.Conversation.Messages[^1];
    Assert.Equal(Role.System, last.Role);
    Assert.Equal("[repeat guard] turn ended: the same call repeated past the critical error", last.Content);
  }

  [Fact]
  public async Task SyntheticResults_DoNotExtendStreaks()
  {
    // After the turn ends on the 14th call, a NEW turn starts fresh: the guard was
    // reset at turn start, and the synthetic results of the prior turn never fed the
    // guard. The new turn's first identical failure produces no nudge.
    CountingTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 14; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    // Turn 2: one identical failure — fresh streak, no nudge, then finish.
    responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("d1", "exec", Args(100))])));
    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> first = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);
    Assert.False(first.IsSuccess);
    Assert.Equal(Agent.RepeatGuardStopCode, first.Error.Code);

    Result<string> second = await agent.SendMessage("go again", ct: TestContext.Current.CancellationToken);
    Assert.True(second.IsSuccess);
    // The turn-1 critical line and stop line predate turn 2: slice AFTER the stop line
    // and assert the turn-2 identical failure produced no nudge (the streak was reset
    // at turn start and the synthetic results never fed the guard).
    IReadOnlyList<Message> allMessages = agent.Conversation.Messages;
    int stopIndex = allMessages.Select((m, i) => (m, i))
        .First(p => p.m.Role is Role.System && p.m.Content == Agent.RepeatGuardStopLine).i;
    Assert.True(stopIndex >= 0);
    Assert.DoesNotContain(agent.Conversation.Messages.Skip(stopIndex + 1),
        m => m.Role is Role.System && m.Content.StartsWith("[repeat guard]", StringComparison.Ordinal));
  }

  [Fact]
  public async Task CriticalSurfacedThroughOnSystemMessage_AndStopThroughFailure()
  {
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 14; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse("never reached", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([new CountingTool()]));
    List<string> surfaced = [];
    TurnCallbacks callbacks = new(OnSystemMessage: line => surfaced.Add(line));

    Result<string> result = await agent.SendMessage("go", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.RepeatGuardStopCode, result.Error.Code);
    Assert.Contains(surfaced, l => l.StartsWith("[repeat guard] CRITICAL:", StringComparison.Ordinal));
    Assert.Contains(surfaced, l => l == "[repeat guard] turn ended: the same call repeated past the critical error");
  }
}
