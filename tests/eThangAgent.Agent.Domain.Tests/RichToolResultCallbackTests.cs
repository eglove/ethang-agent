using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>OnToolResult carries the executed tool's rich result (title, display
///     body) as a fifth nullable argument, so hosts can render the card header and
///     body from metadata that never enters the conversation.</summary>
public class RichToolResultCallbackTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task OnToolResult_CarriesTitle_AndDisplayBody_FromTheToolResult()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "exec", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    RichTool rich = new("parse names", "```csharp\nreturn 42;\n```");
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([rich]));

    List<(string Name, ToolResult? Rich)> results = [];
    TurnCallbacks callbacks = new(OnToolResult: (name, summary, full, err, richResult) => results.Add((name, richResult)));

    Result<string> result = await agent.SendMessage("go", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    (string Name, ToolResult? Rich) = Assert.Single(results);
    Assert.Equal("exec", Name);
    Assert.NotNull(Rich);
    Assert.Equal("parse names", Rich.Title);
    Assert.Equal("```csharp\nreturn 42;\n```", Rich.DisplayBody);
  }

  [Fact]
  public async Task OnToolResult_PlainTool_CarriesNullRichMetadata()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "plain", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("plain", "file body")]));

    List<ToolResult?> rich = [];
    TurnCallbacks callbacks = new(OnToolResult: (name, summary, full, err, richResult) => rich.Add(richResult));

    _ = await agent.SendMessage("go", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    ToolResult? carried = Assert.Single(rich);
    Assert.Null(carried);
  }

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class RichTool(string title, string displayBody) : ITool
  {
    public ToolDefinition Definition { get; } = new("exec", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult("42", false, title, displayBody));
  }

  private sealed class FakeTool(string name, string content) : ITool
  {
    public ToolDefinition Definition { get; } = new(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult(content, false));
  }
}
