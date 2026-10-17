using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class DefaultContextCompactorTailTests
{
  private static readonly ModelConfig Serving = ModelConfig.Create("serving", null, 1000, 0.5f, 1000).Value!;

  private sealed class RecordingFactory : IModelProviderFactory
  {
    public ScriptedProvider Provider { get; } = new ScriptedProvider();

    public IModelProvider Create(ModelConfig config) => Provider;
  }

  private sealed class ScriptedProvider : IModelProvider
  {
    public ModelRequest? LastRequest { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      LastRequest = request;
      return Task.FromResult(Result.Success(new ModelResponse("summary text", [])));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  [Fact]
  public async Task CompactAsync_OversizedTailOnly_CompressesInsteadOfFailing()
  {
    // Tail-dominated bloat: the kept tail's 3-group floor alone exceeds the target
    // (window 1000 -> budget 4000 chars, Small policy 40% target = 1600, 5%
    // threshold = 200), so the plan evicts nothing; the deep clean compresses the
    // oversized kept tool results instead of failing CompactionImpossible.
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    for (int i = 0; i < 3; i++)
    {
      conversation.AddUserMessage($"kept user {i}");
      conversation.AddAssistantMessage("", [new ToolCall($"c{i}", "read", "{}")]);
      conversation.AddToolResult($"c{i}", new string('r', 800));
    }

    RecordingFactory factory = new();
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.Equal(0, outcome.Value.MessagesEvicted);
    Assert.Equal(10, outcome.Value.MessagesKept);
    Assert.Null(outcome.Value.SummarizerUsage);

    IReadOnlyList<Message> after = conversation.Messages;
    Assert.Equal(10, after.Count); // nothing was evicted: no summary message added
    Assert.DoesNotContain(after, m => m.IsSummary);

    // The two oldest oversized results compress oldest-first; the most recent
    // survives once the target is met. Roles and pairing stay intact.
    Assert.StartsWith(ToolResultMarker.Prefix, after[3].Content, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, after[6].Content, StringComparison.Ordinal);
    Assert.Equal(new string('r', 800), after[9].Content);
    Assert.Equal("c0", after[3].ToolCallId);
    Assert.Equal(Role.Tool, after[3].Role);
    Assert.Contains("read", after[3].Content, StringComparison.Ordinal);

    // Nothing was evicted: the summarizer model is never called.
    Assert.Null(factory.Provider.LastRequest);
  }

  [Fact]
  public async Task CompactAsync_NoCompressibleCandidates_StillFailsCompactionImpossible()
  {
    // An oversized kept tail with NO tool results: compression has no candidates,
    // so the deep clean still fails with the unchanged CompactionImpossible code
    // and the conversation is untouched.
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    for (int i = 0; i < 3; i++)
    {
      conversation.AddUserMessage(new string('u', 1000));
      conversation.AddAssistantMessage(new string('a', 1000));
    }

    RecordingFactory factory = new();
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("CompactionImpossible", outcome.Error.Code);
    Assert.Equal(7, conversation.Messages.Count); // untouched
  }

  [Fact]
  public async Task CompactAsync_KeptTailUnderTarget_ResultsSurviveUnchanged()
  {
    // Through the public surface: a conversation whose plan evicts and whose kept
    // tail is under the target leaves every tool result's content unchanged.
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    conversation.AddAssistantMessage(new string('x', 2000));
    conversation.AddUserMessage(new string('y', 2000));
    conversation.AddAssistantMessage(new string('z', 2000));
    conversation.AddUserMessage("kept user 0");
    conversation.AddAssistantMessage("", [new ToolCall("c0", "read", "{}")]);
    conversation.AddToolResult("c0", new string('r', 800));
    for (int i = 1; i < 3; i++)
    {
      conversation.AddUserMessage($"kept user {i}");
      conversation.AddAssistantMessage("short");
    }

    RecordingFactory factory = new();
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Message toolResult = conversation.Messages.First(m => m.Role is Role.Tool);
    Assert.Equal(new string('r', 800), toolResult.Content);
  }
}
