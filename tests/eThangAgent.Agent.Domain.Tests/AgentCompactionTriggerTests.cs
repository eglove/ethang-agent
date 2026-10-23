using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The compaction trigger is derived from the context window through
///     <see cref="CompactionPolicy.For"/> — never from configuration. Window 1_000
///     resolves the Small policy (ceiling 80); window 1_000_000 resolves Large
///     (ceiling 60), where the former fixed 80% trigger waited far too long.</summary>
public class AgentCompactionTriggerTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  private sealed class ThresholdMonitor(double utilization, int window = 1000) : IContextMonitor
  {
    public ContextStatus Status { get; set; } =
        new((int)(utilization * window / 100.0), (long)(utilization * window / 100.0), 0, window, utilization);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  private sealed class ScriptedCompactor : IContextCompactor
  {
    public bool HandoffRequested => false;
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

  /// <summary>A compactor that actually performs the aggregate replacement the real
  ///     one does: prepends the IsSummary handoff message to the conversation.</summary>
  private sealed class ScriptedCompactingCompactor : IContextCompactor
  {
    public bool HandoffRequested => false;
    public const string SummaryText = "[Conversation summary - earlier messages were compacted.]\nhandoff summary text";

    public int Calls { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      Result<bool> replaced = conversation.Compact(
      [
        new Message(Role.System, SummaryText, DateTimeOffset.UtcNow, IsSummary: true),
            ..conversation.Messages,
      ]);
      return Task.FromResult(replaced.IsSuccess
          ? Result.Success(new CompactionOutcome(1, conversation.Messages.Count - 1, new TokenUsage(50, 100)))
          : Result.Failure<CompactionOutcome>(replaced.Error));
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
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 800)); // believable report: 800 tokens at 4 chars/token
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
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
  public async Task SuccessfulCompaction_Fires_SystemMessage_With_Summary_Text()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 800)); // believable report: 800 tokens at 4 chars/token
    ScriptedCompactingCompactor compactor = new();
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });
    List<string> systemMessages = [];

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnSystemMessage: systemMessages.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string summary = Assert.Single(systemMessages);
    Assert.Contains("Conversation summary", summary, StringComparison.Ordinal);
    Assert.Contains("handoff summary text", summary, StringComparison.Ordinal);
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
    conversation.AddUserMessage(new string('x', 1000)); // believable report: >= 950 tokens at 4 chars/token
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
        m.Role is Role.System && m.Content.Contains("compaction failed", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task LargeWindow_CompactsAtDerivedCeiling()
  {
    // Window 1_000_000 -> Large policy ceiling 60: a compaction at 60% where the
    // former fixed threshold waited for 80%. The conversation must legitimately
    // support the reported tokens (60% of 1M = 600k tokens at 4 chars/token).
    ScriptedCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 600_000));
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(60.0, 1_000_000),
          ContextCompactor = compactor,
        });

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, compactor.Calls);
  }

  [Fact]
  public async Task LargeWindow_BelowDerivedCeiling_CompactorNeverRuns()
  {
    ScriptedCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 600_000));
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(59.9, 1_000_000),
          ContextCompactor = compactor,
        });

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, compactor.Calls);
  }
}
