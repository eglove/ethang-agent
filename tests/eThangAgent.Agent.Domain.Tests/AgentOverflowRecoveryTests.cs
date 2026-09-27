using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>In-loop context-window overflow recovery: when the provider call fails with
///     ContextWindowExceeded and no overflow recovery has run this turn, the loop runs a
///     FORCED compaction (bypassing the utilization threshold) and re-sends the same
///     request ONCE. A second overflow, or a compaction failure, fails the turn.
///     Everything here uses fakes only — the domain never knows HTTP or OpenRouter exist.</summary>
public class AgentOverflowRecoveryTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  // Opaque to the domain: the message text is provider-wire detail the ACL attached.
  private static readonly DomainError Overflow = new("ContextWindowExceeded",
      "This model's maximum context length is 131072 tokens. However, you requested 150000 tokens.");

  /// <summary>Fails with ContextWindowExceeded on the 1-based call window
  ///     [failStart, failStart + failCount), then answers successfully; records every
  ///     request's message count. failStart null never fails.</summary>
  private sealed class OverflowThenOkProvider(int? failStart = null, int failCount = 1) : IModelProvider
  {
    private int _calls;
    public System.Collections.ObjectModel.Collection<int> RequestMessageCounts { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      _calls++;
      RequestMessageCounts.Add(request.Messages.Count);
      return Task.FromResult(failStart is { } start && _calls >= start && _calls < start + failCount
          ? Result.Failure<ModelResponse>(Overflow)
          : Result.Success(new ModelResponse("done", [])));
    }
  }

  /// <summary>Overflow on every call — drives the retry-also-overflows path.</summary>
  private sealed class AlwaysOverflowProvider : IModelProvider
  {
    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Failure<ModelResponse>(Overflow));
    }
  }

  /// <summary>Recording fake compactor: succeeds (performing the aggregate replacement
  ///     the real one does) or fails with a chosen error; counts calls.</summary>
  private sealed class RecordingCompactor(DomainError? failure = null) : IContextCompactor
  {
    public int Calls { get; private set; }
    public ModelConfig? LastServingModel { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      LastServingModel = servingModel;
      if (failure is not null)
      {
        return Task.FromResult(Result.Failure<CompactionOutcome>(failure));
      }

      Result<bool> replaced = conversation.Compact(
      [
        new Message(Role.System, "[Conversation summary — earlier messages were compacted.]\nhandoff",
            DateTimeOffset.UtcNow, IsSummary: true),
        ..conversation.Messages,
      ]);
      return Task.FromResult(replaced.IsSuccess
          ? Result.Success(new CompactionOutcome(1, conversation.Messages.Count - 1, new TokenUsage(50, 100)))
          : Result.Failure<CompactionOutcome>(replaced.Error));
    }
  }

  /// <summary>Monitor that always reports the given utilization — the 80% backstop's
  ///     decision input. High utilization must NOT be what triggers the forced path's
  ///     assertions; the fake providers here never report usage, so the monitor is the
  ///     only compaction trigger candidate and the tests prove the forced path runs
  ///     without it firing.</summary>
  private sealed class FixedMonitor(double utilization) : IContextMonitor
  {
    public ContextStatus Status { get; } = new(null, 0, 0, 1000, utilization);
    public ContextBreakdown? Breakdown => null;
    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  [Fact]
  public async Task Overflow_CompactsOnce_AndRetriesOnce_Succeeds()
  {
    OverflowThenOkProvider provider = new(failStart: 1);
    RecordingCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000));
    conversation.AddAssistantMessage(new string('y', 8000));
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions { ContextCompactor = compactor });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, provider.RequestMessageCounts.Count); // exactly one retry
    Assert.Equal(1, compactor.Calls);
    Assert.Equal(Config, compactor.LastServingModel);
    Assert.DoesNotContain(conversation.Messages,
        m => m.Content.Contains(Agent.TurnFailedPrefix, StringComparison.Ordinal));
  }

  [Fact]
  public async Task OverflowRetry_AlsoOverflows_TurnFailsWithOriginalError()
  {
    AlwaysOverflowProvider provider = new();
    RecordingCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000));
    conversation.AddAssistantMessage(new string('y', 8000));
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions { ContextCompactor = compactor });
    List<string> surfaced = [];

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ContextWindowExceeded", result.Error.Code);
    Assert.Equal(2, provider.Calls); // one retry, no more
    Assert.Equal(1, compactor.Calls); // compaction ran exactly once
    Message failure = agent.Conversation.Messages[^1];
    Assert.Equal(Role.System, failure.Role);
    Assert.StartsWith(Agent.TurnFailedPrefix, failure.Content, StringComparison.Ordinal);
    Assert.Contains("Error [ContextWindowExceeded]:", failure.Content, StringComparison.Ordinal);
    // Surfaced live: first the forced compaction's summary, then the turn-failure line.
    Assert.Equal(2, surfaced.Count);
    Assert.Contains("Conversation summary", surfaced[0], StringComparison.Ordinal);
    Assert.StartsWith(Agent.TurnFailedPrefix, surfaced[1], StringComparison.Ordinal);
    Assert.Contains("Error [ContextWindowExceeded]:", surfaced[1], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Overflow_CompactionFails_TurnFailsWithoutRetry()
  {
    OverflowThenOkProvider provider = new(failStart: 1);
    RecordingCompactor compactor = new(new DomainError("CompactionImpossible", "Nothing outside the kept tail to evict."));
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000));
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions { ContextCompactor = compactor });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    // The compaction failure is the actionable cause: both the returned error and the
    // transcript line carry ITS code, never the overflow that triggered recovery.
    Assert.Equal("CompactionImpossible", result.Error.Code);
    _ = Assert.Single(provider.RequestMessageCounts); // no retry
    Assert.Equal(1, compactor.Calls);
    Message failure = Assert.Single(agent.Conversation.Messages, m => m.Role is Role.System);
    Assert.StartsWith(Agent.TurnFailedPrefix, failure.Content, StringComparison.Ordinal);
    Assert.Contains("Error [CompactionImpossible]:", failure.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoOverflow_BehaviorUnchanged_NoForcedCompaction()
  {
    OverflowThenOkProvider provider = new(); // never fails
    RecordingCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000));
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions { ContextCompactor = compactor });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(provider.RequestMessageCounts);
    Assert.Equal(0, compactor.Calls); // no monitor wired: the threshold trigger cannot fire
  }

  [Fact]
  public async Task ForcedRecovery_DoesNotConsumeTheThresholdBackstop()
  {
    // The forced path must bypass the utilization check, not disable it: a turn that
    // overflow-recovers still compacts through the threshold later in the same turn.
    OverflowThenOkProvider provider = new(failStart: 1);
    RecordingCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000));
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new FixedMonitor(95.0),
          ContextCompactor = compactor,
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // iteration 1: threshold compaction; iteration 2: forced recovery compaction.
    Assert.Equal(2, compactor.Calls);
    Assert.Equal(2, provider.RequestMessageCounts.Count);
  }

  [Fact]
  public async Task Overflow_WithNoCompactorWired_TurnFailsWithoutRetry()
  {
    AlwaysOverflowProvider provider = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000));
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    // Recovery cannot even be attempted without a compactor: the distinct
    // CompactionUnavailable code says so; the underlying overflow rides the message.
    Assert.Equal("CompactionUnavailable", result.Error.Code);
    Assert.Contains("maximum context length", result.Error.Message, StringComparison.Ordinal);
    Assert.Equal(1, provider.Calls); // nothing to compact with: no retry
  }
}
