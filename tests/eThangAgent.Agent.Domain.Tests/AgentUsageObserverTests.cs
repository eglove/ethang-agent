using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentUsageObserverTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  private sealed class RecordingMonitor : IContextMonitor
  {
    public List<TokenUsage> Reports { get; } = [];
    public ContextStatus Status { get; private set; } = new(null, 0, 0, 1000, null);
    public ContextBreakdown? Breakdown { get; private set; }

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
      Reports.Add(usage);
      Status = new ContextStatus(usage.InputTokens, usage.InputTokens, usage.OutputTokens, 1000, usage.InputTokens * 100.0 / 1000);
      Breakdown = new ContextBreakdown(composition.SystemPromptChars, (int)composition.MessageChars, (int)composition.ToolDefinitionChars);
    }
  }

  [Fact]
  public async Task UsageObserver_ReceivesSameUsageAsMonitor()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(400, 10))));
    RecordingMonitor monitor = new();
    List<TokenUsage> observed = [];
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions { ContextMonitor = monitor, UsageObserver = observed.Add });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    TokenUsage single = Assert.Single(observed);
    Assert.Equal(new TokenUsage(400, 10), single);
    Assert.Equal(monitor.Reports[0], single);
  }

  [Fact]
  public async Task NoUsageObserver_BehaviorUnchanged()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(400, 10))));
    RecordingMonitor monitor = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions { ContextMonitor = monitor });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(new TokenUsage(400, 10), Assert.Single(monitor.Reports));
  }
}
