using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>One part to render, projected from a consumer's own part type: an
///     image part carries its media type and base64 payload; a text part carries
///     its text. Exactly one side is populated.</summary>
public sealed record PartRender(string? ImageMediaType, string? ImageBase64, string? Text)
{
  public static PartRender ForImage(string mediaType, string base64Data) => new(mediaType, base64Data, null);

  public static PartRender ForText(string text) => new(null, null, text);
}

/// <summary>The one image-parts renderer shared by every secondary parts consumer:
///     the compactor's LLM handoff render (Agent Domain) and the context list the
///     context_edit tool shows (Conversation Domain, through its port). An image part
///     becomes the text marker '[image: mediaType, N bytes]' - N is the DECODED byte
///     length, computed from the base64 payload length without allocating the decoded
///     buffer; a text part appends verbatim, space-separated. Consumers project their
///     own part types into <see cref="PartRender"/>, so this class needs no
///     dependency on any conversation type and every consumer renders identically.
///     The byte rule and the marker shape are pinned by tests in the Agent Domain
///     suite (DefaultContextCompactorImageMarkerTests).</summary>
public static class ImagePartMarker
{
  /// <summary>Appends the parts render to <paramref name="sb"/>: images as
  ///     '[image: mediaType, N bytes]' markers, text verbatim after one space.
  ///     The exact strings are pinned by tests.</summary>
  public static void AppendParts(StringBuilder sb, IReadOnlyList<PartRender> parts)
  {
    ArgumentNullException.ThrowIfNull(sb);
    ArgumentNullException.ThrowIfNull(parts);
    foreach (PartRender part in parts)
    {
      if (part.ImageMediaType is { } mediaType)
      {
        _ = sb.Append(" [image: ").Append(mediaType).Append(", ")
            .Append(DecodedByteCount(part.ImageBase64!)).Append(" bytes]");
      }
      else if (part.Text is { } text)
      {
        _ = sb.Append(' ').Append(text);
      }
    }
  }

  /// <summary>Decoded byte length of a base64 payload: 3/4 of the character count
  ///     minus padding characters. Exact for every valid base64 input.</summary>
  public static int DecodedByteCount(string base64Data)
  {
    ArgumentNullException.ThrowIfNull(base64Data);
    int padding = 0;
    if (base64Data.Length >= 2 && base64Data.EndsWith("==", StringComparison.Ordinal))
    {
      padding = 2;
    }
    else if (base64Data.Length >= 1 && base64Data.EndsWith('='))
    {
      padding = 1;
    }

    return (base64Data.Length * 3 / 4) - padding;
  }
}
