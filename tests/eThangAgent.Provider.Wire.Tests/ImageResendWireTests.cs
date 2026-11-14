using System.Text.Encodings.Web;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Provider.Wire.Tests;

// A picture is worth re-sending on the turn that produced it and worth nothing on any later turn.
// These checks read the SERIALIZED REQUEST BODY - not the domain object - because the claim is
// about what leaves this process: no data: URL for an image the current turn did not produce, and
// an explicit marker in its place so the history never reads as a message that silently lost
// content.
//
// The turn boundary is the LAST User-role message (ImageResendPlan), which is where every turn
// begins: Conversation.AddUserMessage. The awkward cases are pinned here too - an image in the
// very first message, a transcript with no images at all, and a legacy inline image, which gets
// the same treatment as a stored reference because the rule is about which TURN, not which FORM.
public class ImageResendWireTests
{
  private static readonly DateTimeOffset SentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);

  private const string StoredPath = @"C:\images\ethang-agent\shot.png";

  private static readonly JsonSerializerOptions WireJson = new()
  {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };

  private static Message User(string text, params MessagePart[] parts) =>
      new(Role.User, text, SentAt, Parts: parts.Length == 0 ? null : parts);

  private static Message Tool(string text, params MessagePart[] parts) =>
      new(Role.Tool, text, SentAt, ToolCallId: "c1", Parts: parts.Length == 0 ? null : parts);

  private static Message AssistantCall() =>
      new(Role.Assistant, "", SentAt, [new ToolCall("c1", "computer", "{}")]);

  private static string Body(IReadOnlyList<Message> messages) =>
      JsonSerializer.Serialize(ResponsesApiRequestCore.BuildInput(new ModelRequest(messages)), WireJson);

  // --- Behaviour 1: no data URL leaves for a non-current-turn image ---

  /// <summary>The plan's own example: the last user message carries an image and an earlier tool
  ///     message carries one. The request encodes the last user image and NOT the earlier one, and
  ///     the earlier one's rendered text carries the marker.</summary>
  [Fact]
  public void BuildInput_EarlierToolImageAndCurrentUserImage_EncodesOnlyTheCurrentOne()
  {
    string body = Body(
    [
      User("start"),
      AssistantCall(),
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "b2xkZXI=")),
      User("here is another", new MessagePart.ImagePart("image/png", "bmV3ZXI=")),
    ]);

    // Exactly one image_url, and it is the current turn's payload.
    Assert.Equal(1, Occurrences(body, "\"image_url\""));
    Assert.Contains("data:image/png;base64,bmV3ZXI=", body, StringComparison.Ordinal);
    Assert.DoesNotContain("b2xkZXI=", body, StringComparison.Ordinal);
    // The withheld image still says it was here, and why.
    Assert.Contains("[image not re-sent: image/png, 5 bytes", body, StringComparison.Ordinal);
  }

  /// <summary>A tool image produced in the CURRENT turn is still encoded: the rule withholds an
  ///     image for being from an earlier turn, never for being a tool result. This is the guarantee
  ///     the marginal-cost step made - the model can still look at what it just captured.</summary>
  [Fact]
  public void BuildInput_ToolImageFromTheCurrentTurn_IsStillEncoded()
  {
    string body = Body(
    [
      User("take a screenshot"),
      AssistantCall(),
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "c2hvdA==")),
    ]);

    Assert.Contains("data:image/png;base64,c2hvdA==", body, StringComparison.Ordinal);
    Assert.DoesNotContain("not re-sent", body, StringComparison.Ordinal);
  }

  // --- The awkward case: the only image is in the very first message ---

  [Fact]
  public void BuildInput_OnlyImageInTheFirstMessage_EmitsNoDataUrlAtAll()
  {
    string body = Body(
    [
      User("look at this", new MessagePart.ImagePart("image/png", "Zmlyc3Q=")),
      new Message(Role.Assistant, "I see a window", SentAt),
      User("now do something else"),
    ]);

    Assert.DoesNotContain("image_url", body, StringComparison.Ordinal);
    Assert.DoesNotContain("Zmlyc3Q=", body, StringComparison.Ordinal);
    Assert.Contains("[image not re-sent: image/png, 5 bytes", body, StringComparison.Ordinal);
  }

  // --- The awkward case: no images at all renders byte-identically to today ---

  [Fact]
  public void BuildInput_TranscriptWithNoImages_RendersByteIdenticallyToTheLegacyShape()
  {
    string body = Body(
    [
      User("do the thing"),
      AssistantCall(),
      Tool("file contents"),
      new Message(Role.Assistant, "done", SentAt),
    ]);

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"message","role":"user","content":[{"type":"input_text","text":"do the thing"}]},{"type":"function_call","call_id":"c1","name":"computer","arguments":"{}"},{"type":"function_call_output","call_id":"c1","output":"file contents"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]""",
        body);
  }

  // --- The awkward case: a legacy inline image on an earlier turn ---

  /// <summary>The rule is about which TURN, not which FORM: a legacy inline part from an earlier
  ///     turn is withheld exactly as a stored reference is, so an unstripped database behaves the
  ///     same way as a stripped one for the messages it holds.</summary>
  [Fact]
  public void BuildInput_LegacyInlineImageOnAnEarlierTurn_IsWithheldLikeAReference()
  {
    string body = Body(
    [
      Tool("computer: observe", new MessagePart.ImagePart("image/jpeg", "bGVnYWN5")),
      User("carry on"),
    ]);

    Assert.DoesNotContain("image_url", body, StringComparison.Ordinal);
    Assert.Contains("[image not re-sent: image/jpeg, 6 bytes", body, StringComparison.Ordinal);
  }

  // --- Behaviour 2: a stored reference is never encoded, whatever the turn ---

  [Fact]
  public void BuildInput_StoredReferenceOnTheCurrentTurn_IsNeverEncoded_AndNamesItsPath()
  {
    string body = Body(
    [
      User("carry on"),
      AssistantCall(),
      Tool("computer: observe", new MessagePart.ImageReferencePart(StoredPath, "image/png")),
    ]);

    Assert.DoesNotContain("image_url", body, StringComparison.Ordinal);
    Assert.Contains(@"[image: image/png, C:\\images\\ethang-agent\\shot.png]", body, StringComparison.Ordinal);
  }

  // --- Behaviour 4: the stored part is not overwritten ---

  /// <summary>The design is not 'delete the bytes so they stop being sent': the request is a
  ///     projection, and the transcript it was built from still holds every part it held before.</summary>
  [Fact]
  public void BuildInput_LeavesTheRequestedPartsUntouched()
  {
    MessagePart.ImagePart earlier = new("image/png", "b2xkZXI=");
    MessagePart.ImageReferencePart stored = new(StoredPath, "image/png");
    Message[] messages =
    [
      Tool("computer: observe", earlier),
      User("carry on"),
      Tool("computer: observe again", stored),
    ];

    _ = Body(messages);

    Assert.Same(earlier, messages[0].Parts![0]);
    Assert.Equal("b2xkZXI=", Assert.IsType<MessagePart.ImagePart>(messages[0].Parts![0]).Base64Data);
    Assert.Same(stored, messages[2].Parts![0]);
    Assert.Equal(StoredPath, Assert.IsType<MessagePart.ImageReferencePart>(messages[2].Parts![0]).Path);
  }

  // --- Behaviour 3: exactly one place decides, so the wire and the context list agree ---

  /// <summary>THE AGREEMENT CHECK. The wire encoder and the context list are two of the three
  ///     consumers of the resend decision; this runs both over the same transcripts and asserts
  ///     they withhold the same images. A second, independent rule in either consumer would show
  ///     up here as a disagreement rather than as a request that quietly costs more.
  ///     <para>The compactor is the third consumer and it agrees by construction - it reads the
  ///     same plan through the same projection - which its own suite pins in
  ///     Agent.Domain.Tests/ImageResendRenderTests.</para></summary>
  [Fact]
  public void TheWireAndTheContextList_WithholdTheSameImages()
  {
    Message[] messages =
    [
      User("look", new MessagePart.ImagePart("image/png", "Zmlyc3Q=")),
      AssistantCall(),
      Tool("computer: observe", new MessagePart.ImagePart("image/png", "c2Vjb25k")),
      User("and now", new MessagePart.ImagePart("image/jpeg", "dGhpcmQ=")),
    ];

    string body = Body(messages);
    string listing = new ConversationContextService(new Conversation(messages)).List();

    // The wire sent exactly the current turn's image; the listing marks exactly the two it did not.
    Assert.Equal(1, Occurrences(body, "\"image_url\""));
    Assert.Contains("data:image/jpeg;base64,dGhpcmQ=", body, StringComparison.Ordinal);
    Assert.Equal(2, Occurrences(listing, "[image not re-sent: "));
    Assert.Contains("[image not re-sent: image/png, 5 bytes", listing, StringComparison.Ordinal);
    Assert.Contains("[image not re-sent: image/png, 6 bytes", listing, StringComparison.Ordinal);
  }

  /// <summary>The marker is a SUPERSET of the spelling the stored form already uses: it keeps the
  ///     media type and the decoded size, so nothing a reader could learn from the old marker is
  ///     lost, and it adds the reason.</summary>
  [Fact]
  public void TheWithheldMarker_KeepsTheMediaTypeAndTheDecodedSize_AndAddsTheReason()
  {
    string marker = ImagePartMarker.MarkerForNotResent("image/png", 5);

    Assert.Contains("image/png", marker, StringComparison.Ordinal);
    Assert.Contains("5 bytes", marker, StringComparison.Ordinal);
    Assert.Contains("not re-sent", marker, StringComparison.Ordinal);
    Assert.StartsWith("[image", marker, StringComparison.Ordinal);
  }

  private static int Occurrences(string haystack, string needle)
  {
    int count = 0;
    for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
        i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
    {
      count++;
    }

    return count;
  }
}
