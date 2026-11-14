using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A restarted session hydrates a message whose large image was persisted as a FILE PLUS
///     A POINTER. Every renderer that used to assume "a non-text part is an image with a payload"
///     must render that pointer instead - a throw would take the compaction handoff, the
///     context_edit listing, or the whole turn with it.
///     <para>The marker names the media type and the path: a stored image has no payload to
///     measure, so the byte count an inline marker carries would be a fiction, and the path is
///     the one fact that lets the model pull the picture back with read_image.</para></summary>
public class StoredImageRenderTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string StoredPath = @"C:\images\ethang-agent\shot.png";

  private static Message WithStored(string text) => new(Role.Tool, text, T, ToolCallId: "c1",
      Parts: [new MessagePart.ImageReferencePart(StoredPath, "image/png")]);

  private static string Marker => "[image: image/png, " + StoredPath + "]";

  // --- The compactor's LLM handoff render ---

  [Fact]
  public void CompactorRender_StoredImage_BecomesThePathMarker_NotAnException()
  {
    string rendered = DefaultContextCompactor.Render([WithStored("computer: observe")], []);

    Assert.Contains(Marker, rendered, StringComparison.Ordinal);
    Assert.Contains("computer: observe", rendered, StringComparison.Ordinal);
  }

  // --- The shared renderer, at the one projection both consumers use ---

  [Fact]
  public void PartRenders_StoredImage_ProjectsThePath_AndTextPartsAreUnchanged()
  {
    Message message = new(Role.Tool, "beside it", T, ToolCallId: "c1",
        Parts:
        [
          new MessagePart.ImageReferencePart(StoredPath, "image/png"),
          new MessagePart.TextPart("beside it"),
        ]);

    IReadOnlyList<PartRender> renders = PartRenders.Of(message.Parts!,
        ImageResendPlan.For([message]), 0);

    Assert.Equal(2, renders.Count);
    Assert.Equal(StoredPath, renders[0].ImagePath);
    Assert.Null(renders[0].ImageBase64);
    Assert.Equal("beside it", renders[1].Text);
  }

  /// <summary>The inline form still renders with its DECODED byte count, so the two forms are
  ///     distinguishable in the text and nothing about the legacy render moved.</summary>
  [Fact]
  public void CompactorRender_InlineImage_KeepsItsByteCountMarker()
  {
    Message inline = new(Role.Tool, "older screenshot", T, ToolCallId: "c1",
        Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]);

    string rendered = DefaultContextCompactor.Render([inline], []);

    Assert.Contains("[image: image/png, 5 bytes]", rendered, StringComparison.Ordinal);
  }

  /// <summary>Every part kind is represented: the renderer refuses an unknown kind loudly rather
  ///     than dropping it, which is the rule the meta_json mapping follows too.</summary>
  [Fact]
  public void PartRenders_UnknownPartKind_Throws_RatherThanDroppingIt() =>
      _ = Assert.Throws<InvalidOperationException>(() => PartRenders.Of([new UnrenderablePart()],
          ImageResendPlan.For([]), 0));

  private sealed record UnrenderablePart : MessagePart;
}
