using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentContextAccountingTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  private sealed class RecordingMonitor : IContextMonitor
  {
    public List<(TokenUsage Usage, ContextComposition Composition)> Reports { get; } = [];
    public ContextStatus Status { get; private set; } = new(null, 0, 0, 1000, null);
    public ContextBreakdown? Breakdown { get; private set; }

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
      Reports.Add((usage, composition));
      Status = new ContextStatus(usage.InputTokens, Reports.Sum(r => r.Usage.InputTokens),
          Reports.Sum(r => r.Usage.OutputTokens), 1000, usage.InputTokens * 100.0 / 1000);
      Breakdown = new ContextBreakdown(composition.SystemPromptChars, (int)composition.MessageChars, (int)composition.ToolDefinitionChars);
    }
  }

  [Fact]
  public async Task UsageFromEachIteration_ForwardedToMonitor_AndOnContextUpdateFires()
  {
    // Iteration 1 requests a tool call; iteration 2 answers. Each call scores distinct usage.
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "tool", "{}")], FinishReason.ToolCalls, new TokenUsage(400, 10))),
        Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(800, 20))));
    ToolRegistry tools = new([new FakeTool("tool", "ok")]);
    RecordingMonitor monitor = new();
    List<ContextSnapshot> updates = [];
    Agent agent = new(provider, new Conversation(), Config, tools,
        new AgentOptions { ContextMonitor = monitor });

    Result<string> result = await agent.SendMessage("start",
        new TurnCallbacks(OnContextUpdate: updates.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, monitor.Reports.Count);
    Assert.Equal(400, monitor.Reports[0].Usage.InputTokens);
    Assert.Equal(800, monitor.Reports[1].Usage.InputTokens);
    Assert.Equal(2, updates.Count);
    Assert.Equal(80.0, updates[^1].Status.UtilizationPercent);
    // Composition: message chars grow between iterations (user + assistant + tool result).
    Assert.True(monitor.Reports[1].Composition.MessageChars > monitor.Reports[0].Composition.MessageChars);
  }

  [Fact]
  public async Task NoMonitor_TurnUnchanged()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("done", result.Value);
  }
}
