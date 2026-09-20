using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Secondary parts consumer: the compaction handoff render replaces image
///     parts with the exact text marker for an image - media type, then decoded byte
///     count - and text content is unchanged. Text-only messages render
///     byte-identically to the legacy shape. The Render wire format is pinned here
///     (NOT CompactAsync): any summarizer model behind the factory contract reads the
///     same strings.</summary>
public class DefaultContextCompactorImageMarkerTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static Message WithImage(string text, string mediaType, string base64) =>
      new(Role.Tool, text, T, ToolCallId: "c1", Parts: [new MessagePart.ImagePart(mediaType, base64)]);

  [Fact]
  public void Render_ImagePart_ProducesMarkerWithDecodedByteLength()
  {
    Message evicted = WithImage("screenshot captured", "image/png", "aGVsbG8="); // 'hello' = 5 bytes

    string rendered = DefaultContextCompactor.Render([evicted], []);

    Assert.Contains("[image: image/png, 5 bytes]", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("aGVsbG8=", rendered, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_TextContent_IsUnchanged_BeforeAndAfterTheMarker()
  {
    Message evicted = WithImage("screenshot captured", "image/png", "aGVsbG8=");

    string rendered = DefaultContextCompactor.Render([evicted], []);

    Assert.Contains("[Tool] screenshot captured [image: image/png, 5 bytes] [answers c1]",
        rendered, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_TextOnlyMessage_RendersByteIdenticalToLegacyShape()
  {
    Message evicted = new(Role.User, "do the thing", T);
    Message kept = new(Role.User, "next", T);

    string rendered = DefaultContextCompactor.Render([evicted], [kept]);

    Assert.Contains("[User] do the thing", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("[image", rendered, StringComparison.Ordinal);
  }
}

/// <summary>The marker's exact rule: N is the decoded byte length of the image
///     payload. Pinned directly against the shared renderer so the compactor and the
///     context list can never drift apart.</summary>
public class ImageMarkerByteRuleTests
{
  [Fact]
  public void Marker_CarriesDecodedByteCount_NotBase64Length()
  {
    // 'aGVsbG8=' is 'hello': exactly 5 decoded bytes - not the base64 length (8),
    // not any float-derived rounding of it.
    Message message = new(Role.Tool, "shot", T0, ToolCallId: "c1",
        Parts: [new MessagePart.ImagePart("image/jpeg", "aGVsbG8=")]);

    string rendered = DefaultContextCompactor.Render([message], []);

    Assert.Contains("[image: image/jpeg, 5 bytes]", rendered, StringComparison.Ordinal);
    Assert.DoesNotContain("8 bytes", rendered, StringComparison.Ordinal);
  }

  private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
