using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>One part to render, projected from a consumer's own part type: an
///     image part carries its media type and base64 payload; a STORED image carries its
///     media type and the path that names its file; a text part carries its text. Exactly
///     one slot is populated.</summary>
public sealed record PartRender(string? ImageMediaType, string? ImageBase64, string? Text,
    string? ImagePath = null)
{
  public static PartRender ForImage(string mediaType, string base64Data) => new(mediaType, base64Data, null);

  public static PartRender ForStoredImage(string mediaType, string path) =>
      new(mediaType, null, null, path);

  public static PartRender ForText(string text) => new(null, null, text);
}

/// <summary>The one image-parts renderer shared by every secondary parts consumer:
///     the compactor's LLM handoff render (Agent Domain) and the context list the
///     context_edit tool shows (Conversation Domain, through its port). An image part
///     becomes the text marker '[image: mediaType, N bytes]' - N is the DECODED byte
///     length, computed from the base64 payload length without allocating the decoded
///     buffer; a STORED image part becomes '[image: mediaType, path]', because a stored
///     image has no payload to measure and its path is what names it; a text part appends
///     verbatim, space-separated. Consumers project their own part types into
///     <see cref="PartRender"/>, so this class needs no dependency on any conversation type
///     and every consumer renders identically. The byte rule and the marker shape are pinned
///     by tests in the Agent Domain suite (DefaultContextCompactorImageMarkerTests).</summary>
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
      if (part is { ImagePath: { } path, ImageMediaType: { } storedMediaType })
      {
        _ = sb.Append(' ').Append(MarkerForStored(storedMediaType, path));
      }
      else if (part.ImageMediaType is { } mediaType)
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

  /// <summary>The one spelling of a STORED image in rendered text: the media type and the
  ///     absolute path that names the file. A stored image has no payload to measure, so the
  ///     byte count an inline marker carries would be a fiction; the path is what survives and
  ///     what the model hands to read_image.</summary>
  /// <param name="mediaType">The stored image's media type.</param>
  /// <param name="path">The absolute path of the stored file.</param>
  public static string MarkerForStored(string mediaType, string path)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    return "[image: " + mediaType + ", " + path + "]";
  }

  /// <summary>The one spelling of an image the request did NOT re-send: the media type, the
  ///     decoded size, and why. A picture that was sent once and deliberately withheld after that
  ///     is not a picture that vanished - the record keeps saying it was here, so the transcript
  ///     never reads as a message that silently lost content.
  ///     <para>THE SIZE IS THE POINT of the difference from <see cref="MarkerForStored"/>: a stored
  ///     image has no payload left to measure, but a withheld one still holds its bytes and the
  ///     model is being told how much it is NOT paying for.</para></summary>
  /// <param name="mediaType">The withheld image's media type.</param>
  /// <param name="decodedBytes">The withheld image's decoded size.</param>
  public static string MarkerForNotResent(string mediaType, int decodedBytes)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
    return "[image not re-sent: " + mediaType + ", "
        + decodedBytes.ToString(CultureInfo.InvariantCulture)
        + " bytes - already sent on the turn that produced it]";
  }

  /// <summary>The one spelling of an image that did NOT land: the media type and the decoded
  ///     size, with no path, because there is no file to name.
  ///     <para>THE RECORD IS THE POINT. An image that could not be stored is still something the
  ///     conversation produced, so the transcript says so rather than reading as a message that
  ///     never had a picture. What is missing is the picture, not the fact of it.</para></summary>
  /// <param name="mediaType">The image's media type.</param>
  /// <param name="decodedBytes">The image's decoded size, which is what was lost.</param>
  public static string MarkerForUnstored(string mediaType, int decodedBytes)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
    return "[image not stored: " + mediaType + ", " + decodedBytes.ToString(CultureInfo.InvariantCulture)
        + " bytes]";
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
    else if (base64Data.Length >= 1 && base64Data.EndsWith('=', StringComparison.Ordinal))
    {
      padding = 1;
    }

    return (base64Data.Length * 3 / 4) - padding;
  }
}
