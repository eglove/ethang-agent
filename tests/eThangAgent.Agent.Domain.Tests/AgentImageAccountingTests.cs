using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Image-bearing tool results count their base64 payload length into the
///     UI character breakdown (message chars). Provider token usage stays the only
///     decision input; this only sizes the displayed composition buckets.</summary>
public class AgentImageAccountingTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  private const string Payload = "aGVsbG8="; // 8 chars

  private sealed class RecordingMonitor : IContextMonitor
  {
    public List<ContextComposition> Reports { get; } = [];
    public ContextStatus Status { get; private set; } = new(null, 0, 0, 1000, null);
    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition) => Reports.Add(composition);
  }

  private sealed class ImagingTool : ITool
  {
    public ToolDefinition Definition { get; } = new("shot", "screenshot tool", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult("screenshot captured", false,
            Images: [new ToolResultImage("image/png", Payload)]));
  }

  [Fact]
  public async Task ImageBase64Length_CountsIntoMessageChars_BreakdownOnly()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "shot", "{}")], FinishReason.ToolCalls, new TokenUsage(400, 10))),
        Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(800, 20))));
    RecordingMonitor monitor = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([new ImagingTool()]),
        new AgentOptions { ContextMonitor = monitor });

    Result<string> result = await agent.SendMessage("take a shot", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, monitor.Reports.Count);
    long textOnlyChars = "take a shot".Length + "screenshot captured".Length
        + "shot".Length + "{}".Length; // user + tool content + assistant call name+arguments
    long withImage = monitor.Reports[1].MessageChars;
    // Second request saw the image-bearing tool message: its payload length joins the bucket.
    Assert.True(withImage >= textOnlyChars + Payload.Length,
        $"expected message chars >= {textOnlyChars + Payload.Length}, got {withImage}");
  }

  [Fact]
  public async Task TextOnlyTurn_AccountingUnchanged()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "plain", "{}")], FinishReason.ToolCalls, new TokenUsage(400, 10))),
        Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(800, 20))));
    RecordingMonitor monitor = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([new FakeTool("plain", "ok")]),
        new AgentOptions { ContextMonitor = monitor });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    // No image payload anywhere in the conversation; exact bucket, legacy formula.
    long expected = "go".Length + "ok".Length + "plain".Length + "{}".Length;
    Assert.Equal(expected, monitor.Reports[1].MessageChars);
  }
}
