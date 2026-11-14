using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The one admission rule and naming rule for image bytes entering the harness from
///     any producer - a server-side image tool's artifact, a computer screenshot, a pasted file.
///     It lives here, in the domain that owns the image vocabulary, so the rule that decides
///     whether a payload may be stored cannot drift between the store that writes it and the
///     message part that names it.
/// <para>THE FORMAT IS SNIFFED FROM THE BYTES, never taken from the producer's claim: a claim
///     the bytes contradict is refused rather than written under a misleading extension.
///     Size is bounded by <see cref="ImageLimits.MaxBytes"/>. Every refusal is a typed
///     <see cref="DomainError"/>; nothing here throws for an expected failure.</para></summary>
public static class ImagePayload
{
  /// <summary>The PNG media type: one of the closed set the harness stores.</summary>
  public const string Png = "image/png";

  /// <summary>The JPEG media type: the other member of the closed set.</summary>
  public const string Jpeg = "image/jpeg";

  /// <summary>The typed refusal for a payload that may not be stored, or null when it may.
  ///     The cases are reported in order: empty, oversize, unsniffable, then a claim the bytes
  ///     contradict.</summary>
  /// <param name="mediaType">The media type the producer claims.</param>
  /// <param name="bytes">The image payload, decoded.</param>
  public static DomainError? Refusal(string mediaType, byte[] bytes)
  {
    ArgumentNullException.ThrowIfNull(mediaType);
    ArgumentNullException.ThrowIfNull(bytes);

    if (bytes.Length == 0)
    {
      return new DomainError("InvalidImagePayload", "The image carried no payload.");
    }

    if (bytes.Length > ImageLimits.MaxBytes)
    {
      return new DomainError("ImageTooLarge",
          $"The image is {bytes.Length} bytes; the maximum is {ImageLimits.MaxBytes} bytes (20 MB).");
    }

    string? sniffed = ImageFormats.SniffMediaType(bytes);
    return sniffed switch
    {
      null => new DomainError("UnsupportedImageFormat",
          $"The image is neither PNG nor JPEG (the producer claimed '{mediaType}')."),
      _ when !string.Equals(sniffed, mediaType, StringComparison.Ordinal) =>
          new DomainError("ImageMediaTypeMismatch",
              $"The image claims media type '{mediaType}' but its bytes are {sniffed}."),
      _ => null,
    };
  }

  /// <summary>The file extension a stored image of this media type carries, without the dot.
  ///     A media type outside the closed set is a programmer error, because naming a file by it
  ///     would be the silent fallback this rule exists to prevent.</summary>
  /// <param name="mediaType">image/png or image/jpeg.</param>
  public static string FileExtension(string mediaType) => mediaType switch
  {
    Png => "png",
    Jpeg => "jpg",
    _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType,
        "An image media type is exactly image/png or image/jpeg."),
  };

  /// <summary>True when <paramref name="path"/>'s extension is one this media type is stored
  ///     with. A stored image's name and its format must agree: a file named .png holding a
  ///     JPEG misleads whoever opens it, and a reference naming it would claim a format its own
  ///     file name denies.</summary>
  /// <param name="path">The stored image's path.</param>
  /// <param name="mediaType">The image's media type.</param>
  public static bool ExtensionAgrees(string path, string mediaType)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    string extension = Path.GetExtension(path);
    return mediaType == Jpeg
        ? extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        : extension.Equals("." + FileExtension(mediaType), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>A collision-free name for a stored image: the write instant plus a random
  ///     suffix, so two writes - even of identical bytes, even in the same millisecond - are
  ///     two files and the first is never replaced.</summary>
  /// <param name="writtenAt">The instant of the write.</param>
  /// <param name="salt">The caller's uniqueness token.</param>
  /// <param name="mediaType">The SNIFFED media type, which the extension follows.</param>
  public static string FileNameFor(DateTimeOffset writtenAt, Guid salt, string mediaType)
  {
    string stamp = writtenAt.ToUniversalTime().ToString("yyyyMMdd-HHmmssfff",
        CultureInfo.InvariantCulture);
    return $"image-{stamp}-{salt:N}.{FileExtension(mediaType)}";
  }
}
