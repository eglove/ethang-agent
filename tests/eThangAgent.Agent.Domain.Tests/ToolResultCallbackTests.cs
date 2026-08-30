using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class ToolResultCallbackTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task OnToolResult_CarriesFullContent_AndErrorFlag_ForFailedTool()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "faily", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FailingTool("Error [Validation]: input was bad")]));

    List<(string Name, string Summary, string FullContent, bool IsError)> results = [];
    TurnCallbacks callbacks = new(OnToolResult: (name, summary, full, err) => results.Add((name, summary, full, err)));

    Result<string> result = await agent.SendMessage("go", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    (string Name, string Summary, string FullContent, bool IsError) = Assert.Single(results);
    Assert.Equal("faily", Name);
    Assert.Contains("Error [Validation]", Summary, StringComparison.Ordinal);
    Assert.Equal("Error [Validation]: input was bad", FullContent);
    Assert.True(IsError);
  }

  [Fact]
  public async Task OnToolResult_CarriesFullContent_AndSuccessFlag_ForSuccessfulTool()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "read", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "file body line\nsecond line")]));

    List<(string Name, string Summary, string FullContent, bool IsError)> results = [];
    TurnCallbacks callbacks = new(OnToolResult: (name, summary, full, err) => results.Add((name, summary, full, err)));

    Result<string> result = await agent.SendMessage("go", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    (_, string Summary, string FullContent, bool IsError) = Assert.Single(results);
    Assert.Equal("ok", Summary);
    Assert.Equal("file body line\nsecond line", FullContent);
    Assert.False(IsError);
  }

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class FailingTool(string content) : ITool
  {
    public ToolDefinition Definition { get; } = new("faily", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult(content, true));
  }
}
