using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentCompactionTriggerTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  private sealed class ThresholdMonitor(double utilization) : IContextMonitor
  {
    public ContextStatus Status { get; private set; } = new((int)(utilization * 10), (long)(utilization * 10), 0, 1000, utilization);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  private sealed class ScriptedCompactor : IContextCompactor
  {
    public int Calls { get; private set; }
    public bool Fail { get; init; }
    public CompactionOutcome Outcome { get; } = new(4, 2, new TokenUsage(50, 100));

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Fail
          ? Result.Failure<CompactionOutcome>(new DomainError("SummaryUnavailable", "model down"))
          : Result.Success(Outcome));
    }
  }

  private sealed class StubProvider : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ModelResponse("final", [])));

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  [Fact]
  public async Task AtThreshold_CompactorRuns_OutcomeSurfaced()
  {
    ScriptedCompactor compactor = new();
    List<CompactionOutcome> compacted = [];
    Agent agent = new(new StubProvider(), new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnCompacted: compacted.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(1, compactor.Calls);
    _ = Assert.Single(compacted);
  }

  [Fact]
  public async Task BelowThreshold_CompactorNeverRuns()
  {
    ScriptedCompactor compactor = new();
    Agent agent = new(new StubProvider(), new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(79.9),
          ContextCompactor = compactor,
        });

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, compactor.Calls);
  }

  [Fact]
  public async Task CompactionFailure_DegradesToSystemNotice_TurnStillCompletes()
  {
    ScriptedCompactor compactor = new() { Fail = true };
    Conversation conversation = new();
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(95.0),
          ContextCompactor = compactor,
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(1, compactor.Calls); // one attempt, no spam
    Assert.Contains(conversation.Messages, m =>
        m.Role is Role.System && m.Content.Contains("[Context compaction failed:", StringComparison.Ordinal));
  }
}
