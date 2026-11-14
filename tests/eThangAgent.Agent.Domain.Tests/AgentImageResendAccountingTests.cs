using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The UI character breakdown sizes the REQUEST, so an image the request no longer carries
///     stops being counted. That is the one place a withheld image's payload could have kept
///     costing something: a bucket that still added megabytes of base64 for a picture the model is
///     no longer shown would report a context the turn does not have.
///     <para>WHAT IT COUNTS NOW: an inline image's base64 length when the current turn produced it,
///     nothing for an image from an earlier turn, and nothing for a stored reference. The
///     transcript still holds every payload - this number was never a memory reading.</para>
///     <para>Every expectation below is computed from the request the provider actually received,
///     not from an arithmetic guess about how many messages the loop built.</para></summary>
public class AgentImageResendAccountingTests
{
  private static ModelConfig Config => ModelConfig.Create("test-model", 100, 0.5f, 1000).Value!;

  private const string Payload = "aGVsbG8="; // 'hello': 8 base64 chars, 5 decoded bytes

  private sealed class RecordingMonitor : IContextMonitor
  {
    public List<ContextComposition> Reports { get; } = [];
    public ContextStatus Status { get; private set; } = new(null, 0, 0, 1000, null);
    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition) => Reports.Add(composition);
  }

  private sealed class ImagingTool : ITool
  {
    public ToolDefinition Definition { get; } = new("shot", "screenshot tool", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult("screenshot captured", false,
            Images: [new ToolResultImage("image/png", Payload)]));
  }

  /// <summary>Text and tool-call characters of a request: everything the bucket counts regardless of
  ///     images.</summary>
  private static long TextChars(IReadOnlyList<Message> messages)
  {
    long total = 0;
    foreach (Message message in messages)
    {
      total += message.Content.Length;
      if (message.ToolCalls is { Count: > 0 } calls)
      {
        foreach (ToolCall call in calls)
        {
          total += call.Arguments.Length + call.Name.Length;
        }
      }
    }

    return total;
  }

  /// <summary>Every inline image payload in a request, current turn or not.</summary>
  private static long AllImageChars(IReadOnlyList<Message> messages)
  {
    long total = 0;
    foreach (Message message in messages)
    {
      if (message.Parts is { Count: > 0 } parts)
      {
        foreach (MessagePart part in parts)
        {
          total += part is MessagePart.ImagePart image ? image.Base64Data.Length : 0;
        }
      }
    }

    return total;
  }

  /// <summary>The payloads the resend rule sends: the inline images of the current turn.</summary>
  private static long SentImageChars(IReadOnlyList<Message> messages)
  {
    ImageResendPlan plan = ImageResendPlan.For(messages);
    long total = 0;
    for (int i = 0; i < messages.Count; i++)
    {
      if (messages[i].Parts is not { Count: > 0 } parts)
      {
        continue;
      }

      foreach (MessagePart part in parts)
      {
        total += part is MessagePart.ImagePart image && plan.Sends(image, i)
            ? image.Base64Data.Length
            : 0;
      }
    }

    return total;
  }

  /// <summary>The producing turn's request carries the picture, so its payload is counted - the
  ///     guarantee the marginal-cost step made, checked through the bucket rather than the wire
  ///     alone.</summary>
  [Fact]
  public async Task TheProducingTurnsRequest_StillCountsTheImage()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "shot", "{}")],
            FinishReason.ToolCalls, new TokenUsage(400, 10))),
        Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(800, 20))));
    RecordingMonitor monitor = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([new ImagingTool()]),
        new AgentOptions { ContextMonitor = monitor });

    Result<string> result = await agent.SendMessage("take a shot", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess, result.Error?.Message);
    IReadOnlyList<Message> second = provider.RequestsSeen[1].Messages;
    long sent = SentImageChars(second);
    Assert.Equal(Payload.Length, sent);
    Assert.Equal(TextChars(second) + sent, monitor.Reports[1].MessageChars);
  }

  /// <summary>A SECOND turn's request does not carry the first turn's screenshot, so its payload
  ///     leaves the bucket: the same transcript, measured once the boundary moved past the picture.
  ///     <para>This is the trap the step's brief named: leaving the accounting unaware of the
  ///     resend rule would keep reporting megabytes the request does not send.</para></summary>
  [Fact]
  public async Task ASecondTurnsRequest_StopsCountingTheFirstTurnsImage()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "shot", "{}")],
            FinishReason.ToolCalls, new TokenUsage(400, 10))),
        Result.Success(new ModelResponse("done", [], FinishReason.Stop, new TokenUsage(800, 20))),
        Result.Success(new ModelResponse("second turn done", [], FinishReason.Stop, new TokenUsage(900, 20))));
    RecordingMonitor monitor = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([new ImagingTool()]),
        new AgentOptions { ContextMonitor = monitor });

    Result<string> first = await agent.SendMessage("take a shot", ct: TestContext.Current.CancellationToken);
    Assert.True(first.IsSuccess, first.Error?.Message);

    Result<string> second = await agent.SendMessage("now just talk", ct: TestContext.Current.CancellationToken);
    Assert.True(second.IsSuccess, second.Error?.Message);

    // The transcript still holds the picture; the request built from it no longer carries it.
    IReadOnlyList<Message> latest = provider.RequestsSeen[^1].Messages;
    Assert.True(AllImageChars(latest) > 0, "the transcript still holds the screenshot's payload");
    Assert.Equal(0, SentImageChars(latest));
    Assert.Equal(TextChars(latest), monitor.Reports[^1].MessageChars);
  }
}
