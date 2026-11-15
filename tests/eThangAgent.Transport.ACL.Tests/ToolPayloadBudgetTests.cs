using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The window-facing tool payload carries a size budget (R3): a payload that fits
///     must be exactly the bytes the wire has always carried, and one that does not must be
///     reduced without breaking either reader or the call shape the app parses.</summary>
public class ToolPayloadBudgetTests
{
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  /// <summary>What the wire carries for these arguments: the record's own shape, pinned
  ///     verbatim so the fitting case cannot drift. Every member is present, the computed
  ///     <c>IsError</c> included.</summary>
  private const string FittingPayload =
      /*lang=json,strict*/
      """{"Name":"read","Status":"ok","Title":null,"Summary":"ok","Content":"hello","Images":[{"MediaType":"image/png","Base64Data":"iVBORw0KGgoAAQI="}],"IsError":false}""";

  [Fact]
  public void Serialize_FittingPayload_IsTheUnbudgetedWireShape()
  {
    string json = RootTurnToolResultPayload.Serialize("read", "ok", null, "ok", "hello",
        [new RootTurnImage("image/png", PngBase64)]);

    Assert.Equal(FittingPayload, json);
  }

  [Fact]
  public void Serialize_FittingPayloadWithoutImages_IsTheUnbudgetedWireShape()
  {
    string json = RootTurnToolResultPayload.Serialize("read", "ok", null, "ok", "hello");

    Assert.Equal(
        /*lang=json,strict*/
        """{"Name":"read","Status":"ok","Title":null,"Summary":"ok","Content":"hello","Images":[],"IsError":false}""",
        json);
  }

  [Theory]
  [InlineData(0, 0, 0, 0)]
  [InlineData(100, 0, 0, 0)]
  [InlineData(1_000, 20, 20, 8)]
  [InlineData(262_144, 0, 0, 1_000)]
  [InlineData(262_143, 0, 0, 1_000)]
  [InlineData(262_144, 100, 40, 1_000)]
  [InlineData(300_000, 100, 40, 1_000)]
  public void Serialize_IsByteIdenticalExactlyWhenTheTextFits(
      int contentChars, int summaryChars, int titleChars, int imageChars)
  {
    // The serializer WITHOUT the budget: the record the wire carried before the budget
    // existed, built the way Serialize builds it and serialized the same way.
    string content = new('c', contentChars);
    string summary = new('s', summaryChars);
    string? title = titleChars == 0 ? null : new string('t', titleChars);
    List<RootTurnImage> images = imageChars == 0
        ? []
        : [new RootTurnImage("image/png", new string('A', imageChars))];
    string unbudgeted = JsonSerializer.Serialize(
        new RootTurnToolResultPayload("read", "ok", title, summary, content) { Images = images });

    string json = RootTurnToolResultPayload.Serialize("read", "ok", title, summary, content, images);

    int textBytes = Encoding.UTF8.GetByteCount(content) + Encoding.UTF8.GetByteCount(summary)
        + Encoding.UTF8.GetByteCount(title ?? string.Empty);
    if (textBytes <= FramedPayloadBudget.MaxTextBytes)
    {
      Assert.Equal(unbudgeted, json);
      return;
    }

    // Over the text budget the payload is reduced, and the reduction is all that changes:
    // it still parses, still exceeds nothing, and still carries the same name and status.
    Assert.NotEqual(unbudgeted, json);
    Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, FramedPayloadBudget.MaxPayloadBytes);
    RootTurnToolResultPayload parsed =
        Assert.IsType<RootTurnToolResultPayload>(RootTurnToolResultPayload.Parse(json));
    Assert.Equal("read", parsed.Name);
    Assert.Equal("ok", parsed.Status);
  }

  [Fact]
  public void Serialize_OversizeContent_FramesTheTextAndKeepsTheShape()
  {
    // 48,301,402 bytes handed straight to the serializer: the unit case for the cap, not
    // a production path (an over-2MiB result is refused before it ever reaches an emitter).
    string content = new('x', 48_301_402);

    string json = RootTurnToolResultPayload.Serialize("read", "ok", null, "ok", content);

    Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, FramedPayloadBudget.MaxPayloadBytes);
    RootTurnToolResultPayload parsed = Assert.IsType<RootTurnToolResultPayload>(
        RootTurnToolResultPayload.Parse(json));
    Assert.Equal("read", parsed.Name);
    Assert.Equal("ok", parsed.Status);
    Assert.Empty(parsed.Images);
    Assert.True(Encoding.UTF8.GetByteCount(parsed.Content) <= FramedPayloadBudget.MaxTextBytes);
    Assert.Contains("truncated", parsed.Content, StringComparison.Ordinal);
    Assert.Contains("48301402", parsed.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_OversizeImage_DropsItWholeAndSaysSo()
  {
    string image = new('A', 5 * 1024 * 1024);

    string json = RootTurnToolResultPayload.Serialize("image_generation", "ok", null,
        "ok", "ten bytes!", [new RootTurnImage("image/png", image)]);

    Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, FramedPayloadBudget.MaxPayloadBytes);
    RootTurnToolResultPayload parsed = Assert.IsType<RootTurnToolResultPayload>(
        RootTurnToolResultPayload.Parse(json));
    Assert.Empty(parsed.Images);
    Assert.Equal("ten bytes!", parsed.Content);
    Assert.Contains("1 image not shown: too large to preview here", parsed.Summary,
        StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_ImagesAndMaximalText_NeverExceedsThePayloadBudget()
  {
    string content = new('x', 400_000);
    List<RootTurnImage> images = [
        new("image/png", new string('A', 300_000)),
        new("image/png", new string('B', 300_000)),
        new("image/png", new string('C', 300_000)),
    ];

    string json = RootTurnToolResultPayload.Serialize("shot", "ok", new string('T', 9_000),
        new string('s', 9_000), content, images);

    Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, FramedPayloadBudget.MaxPayloadBytes);
    RootTurnToolResultPayload parsed = Assert.IsType<RootTurnToolResultPayload>(
        RootTurnToolResultPayload.Parse(json));
    int dropped = images.Count - parsed.Images.Count;
    Assert.InRange(dropped, 1, images.Count);
    Assert.Contains(FramedPayloadBudget.ImageNotice(dropped), parsed.Summary,
        StringComparison.Ordinal);
  }

  [Fact]
  public void TruncateText_CutStraddlingAMultiByteSequence_BacksUpToTheBoundary()
  {
    // 'e' + U+00E9 + U+20AC + U+1F600, whose UTF-8 lengths are 1, 2, 3 and 4 bytes.
    const string text = "e\u00E9\u20AC\uD83D\uDE00";

    // Six bytes reaches into the four-byte pair, so the cut keeps only the first three.
    Assert.Equal("e\u00E9\u20AC", FramedPayloadBudget.TruncateText(text, 6));
    Assert.Equal("e\u00E9", FramedPayloadBudget.TruncateText(text, 3));
    Assert.Equal("", FramedPayloadBudget.TruncateText(text, 0));
    Assert.Equal(text, FramedPayloadBudget.TruncateText(text, 10));
    Assert.True(Encoding.UTF8.GetByteCount(FramedPayloadBudget.TruncateText(text, 5)) <= 5);
    Assert.DoesNotContain('\uFFFD', FramedPayloadBudget.TruncateText(text, 5));
  }

  [Fact]
  public void TruncatedMarker_NamesTheOriginalAndKeptByteCounts()
  {
    string marker = FramedPayloadBudget.TruncatedMarker(48_301_402, 262_000);

    Assert.Contains("262000", marker, StringComparison.Ordinal);
    Assert.Contains("48301402", marker, StringComparison.Ordinal);
    Assert.Contains("UTF-8 bytes", marker, StringComparison.Ordinal);
  }

  [Fact]
  public void FrameText_WithinTheTextBudget_ReturnsTheSameValues()
  {
    (string content, string summary, string? title) =
        FramedPayloadBudget.FrameText("content", "ok", "Title");

    Assert.Equal("content", content);
    Assert.Equal("ok", summary);
    Assert.Equal("Title", title);
  }

  [Fact]
  public void FrameText_OverTheTextBudget_FramesContentFirstThenSummaryThenTitle()
  {
    string source = new('x', 200_000);

    (string content, string summary, string? title) =
        FramedPayloadBudget.FrameText(source, source, source);

    Assert.InRange(
        Encoding.UTF8.GetByteCount(content) + Encoding.UTF8.GetByteCount(summary)
            + Encoding.UTF8.GetByteCount(title!),
        1, FramedPayloadBudget.MaxTextBytes);
    Assert.Contains("truncated", content, StringComparison.Ordinal);
    Assert.Contains("truncated", summary, StringComparison.Ordinal);
    Assert.Contains("truncated", title, StringComparison.Ordinal);
  }

  [Fact]
  public void TruncateArguments_OverMaxChars_KeepsABudgetAndNeverSplitsASurrogatePair()
  {
    string arguments = new('a', 5_000);

    string kept = FramedPayloadBudget.TruncateArguments(arguments, FramedPayloadBudget.MaxArgumentChars);

    Assert.Equal(FramedPayloadBudget.MaxArgumentChars, kept.Length);
    Assert.Equal(arguments[..FramedPayloadBudget.MaxArgumentChars], kept);

    string pairs = string.Concat(Enumerable.Repeat("\uD83D\uDE00", 10));
    string pairKept = FramedPayloadBudget.TruncateArguments(pairs, 5);
    Assert.Equal(4, pairKept.Length);
    Assert.Equal("\uD83D\uDE00\uD83D\uDE00", pairKept);
  }

  [Fact]
  public void ArgumentBudget_LeavesTheCallShapeParseable()
  {
    string arguments = new('a', 5_000);
    string kept = FramedPayloadBudget.TruncateArguments(arguments, FramedPayloadBudget.MaxArgumentChars)
        + FramedPayloadBudget.TruncatedArgumentMarker(arguments.Length, FramedPayloadBudget.MaxArgumentChars);
    string payload = "call probe 1/1 " + kept;

    // The app's reader takes everything after the position token as the arguments, so a
    // shortened string still yields a call with a name and its head.
    string body = payload["call ".Length..];
    int nameEnd = body.IndexOf(' ', StringComparison.Ordinal);
    int positionEnd = body.IndexOf(' ', nameEnd + 1, StringComparison.Ordinal);
    Assert.True(nameEnd > 0 && positionEnd > nameEnd);
    Assert.Equal("probe", body[..nameEnd]);
    Assert.Equal(1, body[(nameEnd + 1)..positionEnd].Count(c => c == '/'));
    Assert.StartsWith(kept, body[(positionEnd + 1)..], StringComparison.Ordinal);
  }
}

/// <summary>The host's own emitter is where a tool result becomes a wire payload, so the
///     budget has to hold there rather than at one call site (R3). These run the REAL
///     <see cref="ChildHost.RootRunHost"/> over the composed container whose only
///     fake is the model provider, and read what it emitted.</summary>
[Trait("Category", "Process")]
public sealed class ToolPayloadBudgetEmitterTests
{
  private const string CallPrefix = "call ";

  [Fact]
  public async Task RunTurnAsync_HugeToolCallArguments_EmitsTheCallInsideTheArgumentBudget()
  {
    string arguments = new('a', 5_000);
    using ScriptedRootHost host = ScriptedRootHost.Start("done", [""],
        options: new ScriptedOptions(ToolCalls: [new ToolCallRequest("call-1", "probe", arguments)]));
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-a", "run it", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    string payload = Assert.Single(events, e => e.Kind == "tool"
        && e.Payload.StartsWith(CallPrefix, StringComparison.Ordinal)).Payload;
    Assert.True(payload.Length < arguments.Length, "the argument string was not shortened");
    Assert.Contains(FramedPayloadBudget.TruncatedArgumentMarker(5_000, FramedPayloadBudget.MaxArgumentChars),
        payload, StringComparison.Ordinal);

    // The documented shape survives the cut: name, position token, then the arguments.
    string body = payload[CallPrefix.Length..];
    int nameEnd = body.IndexOf(' ', StringComparison.Ordinal);
    int positionEnd = body.IndexOf(' ', nameEnd + 1, StringComparison.Ordinal);
    Assert.Equal("probe", body[..nameEnd]);
    Assert.Equal(1, body[(nameEnd + 1)..positionEnd].Count(c => c == '/'));
    Assert.StartsWith(new string('a', FramedPayloadBudget.MaxArgumentChars), body[(positionEnd + 1)..],
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunTurnAsync_ToolResultOverTheDisplayBudget_EmitsAPayloadTheSeamAccepts()
  {
    // A result inside the model-facing budget but far over the display budget: the shape
    // the emitter is handed whenever a tool returns a large but usable body.
    using ScriptedRootHost host = ScriptedRootHost.Start("done", [""],
        options: new ScriptedOptions(
            ToolCalls: [new ToolCallRequest("call-1", "probe", "{}")],
            ToolResultContent: new string('x', 900_000)));
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-b", "run it", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    string payload = Assert.Single(events, e => e.Kind == "tool"
        && e.Payload.StartsWith('{', StringComparison.Ordinal)).Payload;
    Assert.InRange(Encoding.UTF8.GetByteCount(payload), 1, FramedPayloadBudget.MaxPayloadBytes);
    RootTurnToolResultPayload parsed = Assert.IsType<RootTurnToolResultPayload>(
        RootTurnToolResultPayload.Parse(payload));
    Assert.Equal("probe", parsed.Name);
    Assert.Equal("ok", parsed.Status);
    Assert.Contains("truncated", parsed.Content, StringComparison.Ordinal);

    // The frame the emitter produced is one the seam accepts, and the turn ended on its
    // answer rather than on a fault: the seam-limit line the budget exists to remove is
    // absent from everything this turn emitted.
    using MemoryStream wire = new();
    await TransportFraming.WriteAsync(wire, new TransportEnvelope("root-turn-event", payload, 1),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    RootTurnEvent settled = Assert.Single(events, e => e.Kind == "settled");
    Assert.Equal("done", settled.Payload);
    Assert.DoesNotContain(events, e => e.Payload.Contains("RootTurnFault", StringComparison.Ordinal));
    Assert.DoesNotContain(events, e => e.Payload.Contains("seam limit", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RunTurnAsync_ToolRefusalArrivingAtTheEmitter_IsAPayloadWellInsideTheBudget()
  {
    // The production case: a tool result over the model-facing budget is refused before it
    // reaches the emitter, so what the emitter serializes is the refusal line itself.
    using ScriptedRootHost host = ScriptedRootHost.Start("done", [""],
        options: new ScriptedOptions(
            ToolCalls: [new ToolCallRequest("call-1", "probe", "{}")],
            ToolResultContent: new string('x', 2_500_000)));
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-c", "run it", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    string payload = Assert.Single(events, e => e.Kind == "tool"
        && e.Payload.StartsWith('{', StringComparison.Ordinal)).Payload;
    Assert.True(Encoding.UTF8.GetByteCount(payload) < FramedPayloadBudget.MaxTextBytes);
    RootTurnToolResultPayload parsed = Assert.IsType<RootTurnToolResultPayload>(
        RootTurnToolResultPayload.Parse(payload));
    Assert.StartsWith("Error [ToolResultTooLarge]:", parsed.Content, StringComparison.Ordinal);
    Assert.Equal("error", parsed.Status);
    Assert.DoesNotContain(events, e => e.Payload.Contains("RootTurnFault", StringComparison.Ordinal));
  }
}
