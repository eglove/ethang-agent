using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The model serving a turn is ambient for the tools that turn executes, so a
///     tool that attributes its work names the model that actually ran. The provider's
///     SERVED id wins over the requested one, and the scope is restored after the call.</summary>
public class AgentServingModelScopeTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("anthropic/claude-opus-4-8", 100, 0.5f, 1000).Value!;

  private sealed class ProbeTool : ITool
  {
    public ToolDefinition Definition { get; } = new("probe", "captures the ambient model", [], ["timeoutSeconds"]);

    public string? Observed { get; private set; }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      Observed = AmbientServingModel.Current;
      return Task.FromResult(new ToolResult("probed", false));
    }
  }

  private sealed class OneShotProvider(ModelResponse first) : IModelProvider
  {
    private int _calls;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      _calls++;
      return Task.FromResult(Result.Success(_calls == 1 ? first : new ModelResponse("done", [])));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default) => SendAsync(config, request, ct);
  }

  private static async Task<ProbeTool> RunProbeAsync(ModelResponse first)
  {
    ProbeTool probe = new();
    Conversation conversation = new();
    conversation.AddUserMessage("go");
    Agent agent = new(new OneShotProvider(first), conversation, Config, new ToolRegistry([probe]));
    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return probe;
  }

  [Fact]
  public async Task ToolSeesTheServedModelTheProviderNamed()
  {
    ProbeTool probe = await RunProbeAsync(new ModelResponse(null,
        [new ToolCallRequest("c1", "probe", "{}")])
    { ServedModelId = "openai/gpt-5-2" }).ConfigureAwait(true);

    Assert.Equal("openai/gpt-5-2", probe.Observed);
  }

  [Fact]
  public async Task ToolSeesTheConfiguredModelWhenTheProviderNamesNone()
  {
    ProbeTool probe = await RunProbeAsync(new ModelResponse(null,
        [new ToolCallRequest("c1", "probe", "{}")])).ConfigureAwait(true);

    Assert.Equal("anthropic/claude-opus-4-8", probe.Observed);
  }

  [Fact]
  public async Task AmbientIsRestoredAfterTheToolCall()
  {
    _ = await RunProbeAsync(new ModelResponse(null,
        [new ToolCallRequest("c1", "probe", "{}")])).ConfigureAwait(true);

    Assert.Null(AmbientServingModel.Current);
  }
}
