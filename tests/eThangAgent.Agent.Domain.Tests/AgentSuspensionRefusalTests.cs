using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Loop wiring of the failure breaker's escalation: after 13 consecutive
///     failures across any arguments the critical line lands in the conversation; the
///     NEXT breaker-counted call ends the turn with RepeatGuardStop (suspension is
///     deleted — every call still executes until the turn ends).</summary>
public class AgentSuspensionRefusalTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

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
  public async Task ThirteenthFailure_CriticalLineInjected_NoRefusalEver()
  {
    CountingTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 13; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c14", "exec", Args(14))])));
    responses.Add(Result.Success(new ModelResponse("gave up", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The 14th call still EXECUTED (suspension is deleted) and its failure drove the
    // turn end; the model read the critical line after the 13th.
    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.RepeatGuardStopCode, result.Error.Code);
    Assert.Equal(14, tool._executed);
    Assert.Contains(agent.Conversation.Messages,
        m => m.Role is Role.System && m.Content.StartsWith("[repeat guard] CRITICAL:", StringComparison.Ordinal));
    Assert.DoesNotContain(agent.Conversation.Messages,
        m => m.Content.Contains("ToolSuspended", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DistinctArgumentFailures_ThroughTwelve_NeverEndTurn()
  {
    CountingTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 12; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }

    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(12, tool._executed);
  }
}
