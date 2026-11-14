using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The size an image lands in a row at is decided at PERSISTENCE, and the turn that
///     produced it is not a place that decision is allowed to reach. These tests pin the two
///     halves of that: the conversation holds the bytes while the turn runs - whatever a sink, a
///     renderer or a later request builder does with them - and the sink is handed the message
///     it was handed before, unchanged.
///     <para>What this protects is the model's ability to look at what it just did: the same
///     turn's next request is built from the live conversation, so a part rewritten into a
///     pointer here would make a screenshot invisible to the only reader that was looking at
///     it.</para></summary>
public class AgentImageTurnPassThroughTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  /// <summary>A tool that returns one screenshot-sized image, base64-encoded.</summary>
  private sealed class BigShotTool(string base64) : ITool
  {
    public ToolDefinition Definition { get; } = new("computer", "screenshot tool", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult("computer: observe", false,
            Images: [new ToolResultImage("image/png", base64)]));
  }

  /// <summary>Records every message the loop hands it, in order.</summary>
  private sealed class RecordingSink : IAgentTranscriptStore
  {
    public List<Message> Appended { get; } = [];

    public Task AppendAsync(Message message)
    {
      Appended.Add(message);
      return Task.CompletedTask;
    }

    public Task FlushAsync() => Task.CompletedTask;

    public Task<int> FlushedCount() => Task.FromResult(Appended.Count);

    public Task ReplaceAsync(IReadOnlyList<Message> messages)
    {
      Appended.Clear();
      Appended.AddRange(messages);
      return Task.CompletedTask;
    }
  }

  /// <summary>A PNG-shaped payload of the given decoded size: over the inline threshold, so it
  ///     is exactly the case the persistence rule converts.</summary>
  private static string Png(int size)
  {
    byte[] bytes = new byte[size];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
    return Convert.ToBase64String(bytes);
  }

  // --- Behaviour 3: the model still receives what it received before, on that turn ---

  [Fact]
  public async Task AToolResultImage_StaysInlineOnTheConversation_SoTheTurnsNextRequestCarriesIt()
  {
    string screenshot = Png(400 * 1024);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "computer", "{}")],
            FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    RecordingSink sink = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new BigShotTool(screenshot)]), new AgentOptions { TranscriptSink = sink });

    Result<string> result = await agent
        .SendMessage("take a screenshot", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);

    // The SECOND request is the one built after the tool ran: it carries the picture's bytes,
    // so the model can still see what it just captured.
    ModelRequest second = provider.RequestsSeen[1];
    Message toolMessage = second.Messages.Single(m => m.Role is Role.Tool);
    MessagePart.ImagePart inline = Assert.IsType<MessagePart.ImagePart>(Assert.Single(toolMessage.Parts!));
    Assert.Equal(screenshot, inline.Base64Data);
    Assert.Equal("image/png", inline.MediaType);
  }

  // --- Behaviour 4: the tool's own output is unchanged ---

  [Fact]
  public async Task TheSinkReceivesTheMessageTheLoopBuilt_WithThePayloadTheToolReturned()
  {
    string screenshot = Png(400 * 1024);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "computer", "{}")],
            FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    RecordingSink sink = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new BigShotTool(screenshot)]), new AgentOptions { TranscriptSink = sink });

    Result<string> result = await agent
        .SendMessage("take a screenshot", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Message persisted = sink.Appended.Single(m => m.Role is Role.Tool);
    // The tool's contract to its caller: the same content and the same images it returned.
    Assert.Equal("computer: observe", persisted.Content);
    Assert.Equal(screenshot,
        Assert.IsType<MessagePart.ImagePart>(Assert.Single(persisted.Parts!)).Base64Data);
  }

  // --- Behaviour 1: a small image is untouched by the rule at every layer ---

  [Fact]
  public async Task ASmallImage_ReachesTheProviderInTheSameTurn_AndTheSmallFormIsUnchanged()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "computer", "{}")],
            FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    RecordingSink sink = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new BigShotTool("aGVsbG8=")]), new AgentOptions { TranscriptSink = sink });

    Result<string> result = await agent
        .SendMessage("take a screenshot", ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Message toolMessage = provider.RequestsSeen[1].Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal("aGVsbG8=",
        Assert.IsType<MessagePart.ImagePart>(Assert.Single(toolMessage.Parts!)).Base64Data);
    Assert.Equal("aGVsbG8=",
        Assert.IsType<MessagePart.ImagePart>(Assert.Single(sink.Appended.Single(m => m.Role is Role.Tool).Parts!)).Base64Data);
  }
}
