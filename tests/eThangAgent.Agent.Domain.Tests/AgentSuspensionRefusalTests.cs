using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Loop enforcement of the repeat guard's suspension: once a tool is
///     suspended (13 consecutive failures across any arguments), further calls to it
///     are REFUSED without executing — the model gets the refusal as its tool result
///     and must take a different approach.</summary>
public class AgentSuspensionRefusalTests
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
  public async Task SuspendedTool_FurtherCallsRefusedWithoutExecuting()
  {
    CountingTool tool = new();
    // 13 failed exec calls (varied args) to trip suspension, then the model calls
    // exec twice more; the provider then finishes.
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 13; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }
    responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c14", "exec", Args(14))])));
    responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c15", "exec", Args(15))])));
    responses.Add(Result.Success(new ModelResponse("gave up", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(13, tool._executed);
    // The two refused calls still produce tool results (protocol stays valid).
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Message refusal14 = messages.Single(m => m.Role is Role.Tool && m.ToolCallId == "c14");
    Assert.Contains("suspended", refusal14.Content, StringComparison.Ordinal);
    Message refusal15 = messages.Single(m => m.Role is Role.Tool && m.ToolCallId == "c15");
    Assert.Contains("suspended", refusal15.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Refusal_DoesNotExtendSuspensionOrResetStreak()
  {
    CountingTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 13; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c" + i, "exec", Args(i))])));
    }
    responses.Add(Result.Success(new ModelResponse(null, [new ToolCallRequest("c14", "exec", Args(14))])));
    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(13, tool._executed);
  }
}
