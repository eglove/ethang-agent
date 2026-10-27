using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The one-time soft reminder: when utilization first reaches the derived
///     policy's soft level (below the ceiling), the loop appends exactly one System
///     reminder naming the pressure, the context_trim tool, and the ceiling. It never
///     repeats until the pressure cycle resets — a successful compaction or an observed
///     context shrink starts a new cycle; a FAILED compaction does not.</summary>
public class AgentSoftReminderTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", 100, 0.5f, 1000).Value!;

  /// <summary>Monitor whose pressure report the test drives directly: window 1_000_000
  ///     resolves the Large policy (soft 50, ceiling 60).</summary>
  private sealed class PressureMonitor(int window, double utilization, int reportedTokens) : IContextMonitor
  {
    public ContextStatus Status { get; set; } =
        new(reportedTokens, reportedTokens, 0, window, utilization);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  private sealed class CountingCompactor : IContextCompactor
  {
    public int Calls { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new CompactionOutcome(1, 1, new TokenUsage(1, 1))));
    }

    public Task<Result<CompactionOutcome>> CompactInPlaceAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
        => CompactAsync(conversation, servingModel, ct);
  }

  private sealed class FailingCompactor : IContextCompactor
  {
    public int Calls { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Failure<CompactionOutcome>(new DomainError("SummaryUnavailable", "model down")));
    }

    public Task<Result<CompactionOutcome>> CompactInPlaceAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
        => CompactAsync(conversation, servingModel, ct);
  }

  private sealed class StubProvider : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ModelResponse("final", [])));

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  /// <summary>One iteration per queued response; a tool-call response forces another
  ///     iteration against the same turn.</summary>
  private sealed class ScriptedProvider(params ModelResponse[] responses) : IModelProvider
  {
    private readonly Queue<ModelResponse> _responses = new(responses);

    public System.Collections.ObjectModel.Collection<string> Prompts { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      Prompts.Add(request.Messages.Last(m => m.Role is Role.User).Content);
      return Task.FromResult(Result.Success(_responses.Dequeue()));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private static Agent NewAgent(IContextMonitor monitor, IContextCompactor? compactor, IModelProvider provider,
      Conversation? conversation = null)
  {
    conversation ??= new Conversation();
    conversation.AddUserMessage(new string('x', 4000)); // believable: ~1000 estimated tokens at 4 chars/token
    return new Agent(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = monitor,
          ContextCompactor = compactor ?? new CountingCompactor(),
        });
  }

  private static int ReminderCount(Conversation conversation)
      => conversation.Messages.Count(m => m.Role is Role.System
          && m.Content.StartsWith(Agent.SoftReminderPrefix, StringComparison.Ordinal)
          && m.Content.Contains("full", StringComparison.Ordinal));

  [Fact]
  public async Task Reminder_FiresOnce_AtSoftLevel_BelowCeiling()
  {
    // Window 1_000_000 -> Large policy: soft 50, ceiling 60. 55% sits between them.
    PressureMonitor monitor = new(1_000_000, 55.0, 550);
    CountingCompactor compactor = new();
    List<string> systemMessages = [];
    Agent agent = NewAgent(monitor, compactor, new StubProvider());
    Conversation conversation = agent.Conversation;

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnSystemMessage: systemMessages.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(1, ReminderCount(conversation));
    string reminder = Assert.Single(systemMessages, m => m.StartsWith(Agent.SoftReminderPrefix, StringComparison.Ordinal));
    Assert.Contains("55% full", reminder, StringComparison.Ordinal);
    Assert.Contains("context_trim", reminder, StringComparison.Ordinal);
    Assert.Contains("60%", reminder, StringComparison.Ordinal);
    Assert.Equal(0, compactor.Calls); // below the ceiling: no compaction ran
  }

  [Fact]
  public async Task Reminder_DoesNotRepeat_OnNextIteration()
  {
    // Two iterations at the same 55% pressure: still exactly one reminder.
    PressureMonitor monitor = new(1_000_000, 55.0, 550);
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 4000));
    Agent agent = new(new ScriptedProvider(
            new ModelResponse(null, [new ToolCallRequest("call_1", "echo", "{}")]),
            new ModelResponse("done", [])),
        conversation, Config, new ToolRegistry([new FakeTool("echo", "ok")]),
        new AgentOptions { ContextMonitor = monitor, ContextCompactor = new CountingCompactor() });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(1, ReminderCount(conversation));
  }

  [Fact]
  public async Task Reminder_Resets_AfterCompaction()
  {
    // 55% (reminder fires) -> 65% (compaction runs) -> back to 55%: a SECOND reminder
    // appears, because the successful compaction started a new pressure cycle.
    PressureMonitor monitor = new(1_000_000, 55.0, 550);
    CountingCompactor compactor = new();
    Agent agent = NewAgent(monitor, compactor, new StubProvider());
    Conversation conversation = agent.Conversation;

    _ = await agent.SendMessage("first", ct: TestContext.Current.CancellationToken);
    Assert.Equal(1, ReminderCount(conversation));

    monitor.Status = new ContextStatus(650, 650, 0, 1_000_000, 65.0);
    _ = await agent.SendMessage("second", ct: TestContext.Current.CancellationToken);
    Assert.Equal(1, compactor.Calls); // 65% is at the ceiling: compaction ran
    Assert.Equal(1, ReminderCount(conversation)); // at the ceiling no reminder fires

    monitor.Status = new ContextStatus(550, 550, 0, 1_000_000, 55.0);
    _ = await agent.SendMessage("third", ct: TestContext.Current.CancellationToken);
    Assert.Equal(2, ReminderCount(conversation)); // the cycle restarted
  }

  [Fact]
  public async Task Reminder_DoesNotReset_OnFailedCompaction()
  {
    // A FAILED compaction is not a pressure-cycle reset: back at 55% the reminder
    // must NOT fire again.
    PressureMonitor monitor = new(1_000_000, 55.0, 550);
    FailingCompactor compactor = new();
    Agent agent = NewAgent(monitor, compactor, new StubProvider());
    Conversation conversation = agent.Conversation;

    _ = await agent.SendMessage("first", ct: TestContext.Current.CancellationToken);
    Assert.Equal(1, ReminderCount(conversation));

    monitor.Status = new ContextStatus(650, 650, 0, 1_000_000, 65.0);
    _ = await agent.SendMessage("second", ct: TestContext.Current.CancellationToken);
    Assert.Equal(1, compactor.Calls);

    monitor.Status = new ContextStatus(550, 550, 0, 1_000_000, 55.0);
    _ = await agent.SendMessage("third", ct: TestContext.Current.CancellationToken);
    Assert.Equal(1, ReminderCount(conversation)); // no second reminder
  }

  [Fact]
  public async Task Reminder_DoesNotFire_WhenInflationGuardTrips()
  {
    // Reported input tokens far beyond the conversation's own estimate: the report
    // is server-tool accounting, not context — neither reminder nor compaction fires.
    PressureMonitor monitor = new(1_000_000, 55.0, 500_000);
    CountingCompactor compactor = new();
    Agent agent = NewAgent(monitor, compactor, new StubProvider());
    Conversation conversation = agent.Conversation;

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, compactor.Calls);
    Assert.Equal(0, ReminderCount(conversation));
  }
}
