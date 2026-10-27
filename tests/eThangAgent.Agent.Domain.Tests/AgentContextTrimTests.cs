using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #222 end-to-end: the real context_trim tool, backed by the real
///     ConversationContextService, driven through the real agent loop. While the tool
///     runs, its own call is the conversation's trailing unanswered message — the exact
///     shape that used to make every trim fail with UnansweredToolCall.</summary>
public class AgentContextTrimTests
{
  private static readonly ModelConfig Config = ModelConfig.Create("test-model", 100, 0.5f, 1000).Value!;

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

  /// <summary>The real tool over the real service — no fakes between them.</summary>
  private sealed class RealContextTrimTool(Conversation conversation) : ITool
  {
    private readonly ContextTrimTool _inner = new(new ToolDomainAdapter(conversation));

    public ToolDefinition Definition => _inner.Definition;

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => _inner.ExecuteAsync(input, ct);
  }

  private sealed class ToolDomainAdapter(Conversation conversation) : IConversationContextService
  {
    private readonly ConversationContextService _service = new(conversation);

    public string List() => _service.List();

    public Result<string> Remove(string selection) => _service.Remove(selection);

    public Result<string> Shorten(string selection, string text) => _service.Shorten(selection, text);

    public Result<string> TrimToolResults(string selection) => _service.TrimToolResults(selection);

    public Result<string> ShortenToolResult(string selection, string text) => _service.ShortenToolResult(selection, text);
  }

  private static ModelResponse CallResponse(string id, string name, string arguments)
      => new(null, [new ToolCallRequest(id, name, arguments)], Usage: new TokenUsage(50, 100));

  [Fact]
  public async Task ContextTrim_ThroughTheLoop_Succeeds_WhileItsOwnCallIsInFlight()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("seed task");
    conversation.AddAssistantMessage("", [new ToolCall("c1", "read", "{}")]);
    conversation.AddToolResult("c1", new string('x', 40_000));
    RealContextTrimTool tool = new(conversation);
    ScriptedProvider provider = new(
    [
      CallResponse("c2", "context_trim",
          /*lang=json,strict*/ "{\"timeoutSeconds\":5,\"action\":\"Trim\",\"selection\":\"3\"}"),
      new ModelResponse("done", []),
    ]);
    Agent agent = new(provider, conversation, Config, new ToolRegistry([tool]), new AgentOptions());
    List<(string Name, string Content, bool IsError)> results = [];

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnToolResult: (name, _, content, isError, _) => results.Add((name, content, isError))),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    (string _, string content, bool isError) = Assert.Single(results);
    Assert.False(isError);
    Assert.StartsWith(ConversationContextService.ShrankSentinel, content, StringComparison.Ordinal);
    Message trimmed = Assert.Single(conversation.Messages, m => m.Role is Role.Tool && m.ToolCallId == "c1");
    Assert.StartsWith(ToolResultMarker.Prefix, trimmed.Content, StringComparison.Ordinal);
  }
}
