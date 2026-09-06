using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>TurnCallbacks.OnSystemMessage: every System message the agent loop appends
///     mid-turn (length-truncation continuation prompt, compaction-failure notice) is
///     reported verbatim, so hosts can surface loop-voice decisions live. Legacy callers
///     without the callback see byte-identical behavior.</summary>
public class SystemMessageCallbackTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  [Fact]
  public async Task ContinuationPrompt_FiresOnSystemMessage_AndStillAppendsToConversation()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse("half an", [], FinishReason.Length)),
        Result.Success(new ModelResponse("half an answer", [])));
    Conversation conversation = new();
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]));

    List<string> reported = [];
    TurnCallbacks callbacks = new(OnSystemMessage: reported.Add);

    Result<string> result = await agent.SendMessage("go", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string line = Assert.Single(reported);
    Assert.Equal(Agent.ContinuationPrompt, line);
    Assert.Contains(conversation.Messages, m => m.Role is Role.System && m.Content == Agent.ContinuationPrompt);
  }

  [Fact]
  public async Task CompactionFailureNotice_FiresOnSystemMessage()
  {
    Conversation conversation = new();
    Agent agent = new(new ScriptedModelProvider(), conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(95.0),
          ContextCompactor = new FailingCompactor(),
        });

    List<string> reported = [];
    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnSystemMessage: reported.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string line = Assert.Single(reported);
    Assert.StartsWith("[Context compaction failed:", line, StringComparison.Ordinal);
  }

  private sealed class ThresholdMonitor(double utilization) : IContextMonitor
  {
    public ContextStatus Status { get; private set; } = new((int)(utilization * 10), (long)(utilization * 10), 0, 1000, utilization);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  private sealed class FailingCompactor : IContextCompactor
  {
    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<CompactionOutcome>(new DomainError("SummaryUnavailable", "model down")));
  }
}
