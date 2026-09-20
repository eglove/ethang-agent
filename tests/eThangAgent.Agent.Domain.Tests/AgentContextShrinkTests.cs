using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The loop's shrink-trigger contract: a tool result carrying the verbatim
///     sentinel <c>[context: shrank</c> tells the loop the conversation shrank mid-turn,
///     so auto-compaction rests for the turn and persistence must replace the
///     transcript. The shrink itself is performed by the tool through the shared
///     ConversationContextService; the loop only observes the sentinel.
///     Grand-plan: agent-triggered compaction at milestones.</summary>
public class AgentContextShrinkTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 1000).Value!;

  private sealed class ScriptedProvider(IReadOnlyList<ModelResponse> responses) : IModelProvider
  {
    private int _calls;

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      int index = Math.Min(_calls, responses.Count - 1);
      _calls++;
      return Task.FromResult(Result.Success(responses[index]));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private sealed class ShrinkingTool : ITool
  {
    public const string SentinelResult = "[context: shrank 2 message(s): removed last 2.]";

    public ToolDefinition Definition { get; } = new("context_edit", "test", [], ["timeoutSeconds"]);

    public bool Called { get; private set; }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      Called = true;
      return Task.FromResult(new ToolResult(SentinelResult, false));
    }
  }

  /// <summary>Monitor whose utilization is scriptable mid-turn: OnRequestUsage lifts
  ///     it to the threshold, so the loop's suppression decision is observable.</summary>
  private sealed class SteppedMonitor : IContextMonitor
  {
    public double Utilization { get; set; }

    public ContextStatus Status => new(10, 10, 0, 1000, Utilization);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition) => Utilization = 80.0;
  }

  private sealed class RecordingCompactor : IContextCompactor
  {
    public int Calls { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new CompactionOutcome(0, conversation.Messages.Count, null)));
    }
  }

  [Fact]
  public async Task ShrinkSentinel_SuppressesAutoCompaction_RestOfTurn()
  {
    Conversation conversation = Seeded();
    ShrinkingTool tool = new();
    SteppedMonitor monitor = new();
    RecordingCompactor compactor = new();
    ScriptedProvider provider = new(
    [
      CallResponse("c1", "context_edit"),
      new ModelResponse("final", []),
    ]);
    Agent agent = new(provider, conversation, Config, new ToolRegistry([tool]),
        new AgentOptions { ContextMonitor = monitor, ContextCompactor = compactor });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(tool.Called);
    // Usage reported after the shrink call lifted utilization to the threshold,
    // but the sentinel keeps the auto-compactor silent for the rest of the turn.
    Assert.Equal(80.0, monitor.Utilization);
    Assert.Equal(0, compactor.Calls);
  }

  [Fact]
  public async Task ShrinkSentinel_Fires_OnContextShrunk_Callback_Once()
  {
    Conversation conversation = Seeded();
    ShrinkingTool tool = new();
    int fired = 0;
    ScriptedProvider provider = new(
    [
      CallResponse("c1", "context_edit"),
      new ModelResponse("final", []),
    ]);
    Agent agent = new(provider, conversation, Config, new ToolRegistry([tool]), new AgentOptions());

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnContextShrunk: () => fired++), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(1, fired);
  }

  private static ModelResponse CallResponse(string id, string name) => new(null, [new ToolCallRequest(id, name, "{}")], Usage: new TokenUsage(50, 100));

  private static Conversation Seeded()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("seed task");
    return conversation;
  }
}
