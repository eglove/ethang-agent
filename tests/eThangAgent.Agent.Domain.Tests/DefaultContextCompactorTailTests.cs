using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class DefaultContextCompactorTailTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
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

  // Window 1000 tokens -> character budget 4000; the Small policy's 40% target is
  // 1600 chars and the 5% tail-compression threshold is 200 chars, so an 800-char
  // tool result is a compression candidate while small text is not.
  private static List<Message> Messages_WithOversizedTail()
  {
    List<Message> messages = [new(Role.User, "task", T)]; // protected head
    for (int i = 0; i < 3; i++)
    {
      messages.Add(new(Role.User, $"kept user {i}", T));
      messages.Add(new(Role.Assistant, "", T, [new ToolCall($"c{i}", "read", "{}")]));
      messages.Add(new(Role.Tool, new string('r', 800), T, ToolCallId: $"c{i}"));
    }

    return messages;
  }

  private static ContextEvictionPlan Plan_AllKept(List<Message> messages)
      => new(ProtectedCount: 1, EvictCount: 0, KeptCount: messages.Count);

  [Fact]
  public void CompressTail_CompressesOversizedResults_OldestFirst_UntilTarget()
  {
    List<Message> messages = Messages_WithOversizedTail();
    ContextEvictionPlan plan = Plan_AllKept(messages);

    List<Message> keptTail = DefaultContextCompactor.CompressTail(messages, plan, 1000, 40.0);

    // The two oldest oversized results compress oldest-first; the most recent
    // survives once the target is met (2409 - ~730 = ~1680 > 1600, then ~950).
    Assert.StartsWith(ToolResultMarker.Prefix, keptTail[3].Content, StringComparison.Ordinal);
    Assert.StartsWith(ToolResultMarker.Prefix, keptTail[6].Content, StringComparison.Ordinal);
    Assert.Equal(new string('r', 800), keptTail[9].Content);

    // Pairing stays intact: the compressed result still answers its call, and the
    // marker names the paired tool call found in the ORIGINAL list.
    Assert.Equal("c0", keptTail[3].ToolCallId);
    Assert.Equal(Role.Tool, keptTail[3].Role);
    Assert.Contains("read", keptTail[3].Content, StringComparison.Ordinal);
  }

  [Fact]
  public void CompressTail_RecentOversizedResultSurvives_WhenTargetAlreadyMet()
  {
    List<Message> messages = Messages_WithOversizedTail();
    ContextEvictionPlan plan = Plan_AllKept(messages);

    // A target the tail already meets: nothing compresses.
    List<Message> keptTail = DefaultContextCompactor.CompressTail(messages, plan, 1000, 100.0);

    Assert.Equal(new string('r', 800), keptTail[3].Content);
    Assert.Equal(new string('r', 800), keptTail[6].Content);
    Assert.Equal(new string('r', 800), keptTail[9].Content);
  }

  [Fact]
  public void CompressTail_CompressedResult_KeepsToolCallIdAndRole()
  {
    List<Message> messages = Messages_WithOversizedTail();
    ContextEvictionPlan plan = Plan_AllKept(messages);

    List<Message> keptTail = DefaultContextCompactor.CompressTail(messages, plan, 1000, 40.0);

    // The compressed tail passes Conversation.Compact's protocol validation - the
    // replacement is internally consistent by construction.
    Conversation conversation = new([new(Role.System, "summary", T, IsSummary: true), .. keptTail]);
    List<Message> after = [.. conversation.Messages];
    Message compressed = after.First(m => m.Content.StartsWith(ToolResultMarker.Prefix, StringComparison.Ordinal));
    Assert.Equal(Role.Tool, compressed.Role);
    Assert.Equal("c0", compressed.ToolCallId);
    Assert.Equal(11, after.Count); // summary(1) + protected head(1) + kept tail(9)
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
