using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Loop enforcement of the success-repetition breaker (2026-10-08 incident):
///     a model that re-issues one byte-identical call and gets a SUCCESSFUL result every
///     time is suspended after 13 identical successes — further calls are refused without
///     executing. Distinct-argument successes are never suspended (legitimate polling).</summary>
public class AgentSuccessSuspensionTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private const string Payload = "the same payload";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class CountingSuccessTool : ITool
  {
    public int _executed;

    public ToolDefinition Definition { get; } = new("exec", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      _executed++;
      return Task.FromResult(new ToolResult(Payload, false));
    }
  }

  [Fact]
  public async Task ThirteenIdenticalSuccesses_SuspendTool_FurtherCallsRefused()
  {
    CountingSuccessTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 13; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", /*lang=json,strict*/ "{\"program\":\"same\"}")])));
    }
    responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c14", "exec", /*lang=json,strict*/ "{\"program\":\"same\"}")])));
    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(13, tool._executed);
    Message refusal = agent.Conversation.Messages.Single(m => m.Role is Role.Tool && m.ToolCallId == "c14");
    Assert.Contains("suspended", refusal.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DistinctArgumentSuccesses_NotSuspended()
  {
    CountingSuccessTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 20; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }
    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(20, tool._executed);
  }
}
