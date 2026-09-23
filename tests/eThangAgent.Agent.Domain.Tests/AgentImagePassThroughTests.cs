using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The loop carries tool-produced images into the conversation: a ToolResult
///     with Images lands as MessagePart.ImagePart entries on the tool message. Text-only
///     results (the overwhelmingly common case) keep the legacy shape with Parts null,
///     and the interrupted-repair path stays text-only.</summary>
public class AgentImagePassThroughTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  /// <summary>A tool whose result carries images, counting how often it ran.</summary>
  private sealed class ImagingTool : ITool
  {
    public ToolDefinition Definition { get; } = new("shot", "screenshot tool", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult("screenshot captured", false,
            Images: [new ToolResultImage("image/png", "aGVsbG8=")]));
  }

  [Fact]
  public async Task ToolResultImages_BecomeImageParts_OnTheToolMessage()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "shot", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([new ImagingTool()]));

    Result<string> result = await agent.SendMessage("take a shot", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal("screenshot captured", toolMessage.Content);
    Assert.NotNull(toolMessage.Parts);
    MessagePart.ImagePart image = Assert.IsType<MessagePart.ImagePart>(toolMessage.Parts[0]);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal("aGVsbG8=", image.Base64Data);
  }

  [Fact]
  public async Task TextOnlyToolResult_KeepsLegacyShape_PartsNull()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "plain", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([new FakeTool("plain", "ok")]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Null(toolMessage.Parts);
  }

  /// <summary>The imaging tool cancels mid-run like real work observing ct.</summary>
  private sealed class CancellingImagingTool(CancellationTokenSource cts) : ITool
  {
    public ToolDefinition Definition { get; } = new("shot", "screenshot tool", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
#pragma warning disable CA1849 // sync-over-sync inside a tool fake: the cancel must land before ct observes; mirrors AgentSteeringTests' ActionTool
      cts.Cancel();
#pragma warning restore CA1849
      ct.ThrowIfCancellationRequested();
      return Task.FromResult<ToolResult>(null!); // never reached
    }
  }

  [Fact]
  public async Task RepairInterruptedToolCalls_StaysTextOnly()
  {
    using CancellationTokenSource cts = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "shot", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new CancellingImagingTool(cts)]));

    Result<string> result = await agent.SendMessage("go", ct: cts.Token);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.TurnCancelledCode, result.Error.Code);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal(Agent.InterruptedToolResult, toolMessage.Content);
    Assert.Null(toolMessage.Parts);
  }
}
