using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Server-side tool calls (OpenRouter web_search) inflate the provider-scored
///     input_tokens far beyond what the conversation itself could hold: the search pages
///     ride the provider's accounting but never enter the message history. The compaction
///     trigger must distrust a utilization report that wildly exceeds the conversation's
///     own character estimate — otherwise compaction fires on a tiny conversation and
///     fails with CompactionImpossible (observed in the field, 2026-09-26).</summary>
public class AgentCompactionInflationGuardTests
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

  private sealed class CountingCompactor : IContextCompactor
  {
    public int Calls { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new CompactionOutcome(1, 1, new TokenUsage(1, 1))));
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
  public async Task InflatedUtilization_TinyConversation_CompactorNeverRuns()
  {
    // 800 reported input tokens against a conversation of a few dozen characters:
    // nothing legitimately scores 800 tokens here — the report is inflated.
    CountingCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage("tiny");
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, compactor.Calls);
  }

  [Fact]
  public async Task BelievableUtilization_RealConversation_CompactorStillRuns()
  {
    // A conversation whose character estimate legitimately supports the reported tokens
    // (4 chars/token: ~3200 chars => ~800 tokens) must still compact at threshold.
    CountingCompactor compactor = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 3200));
    Agent agent = new(new StubProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, compactor.Calls);
  }
}
