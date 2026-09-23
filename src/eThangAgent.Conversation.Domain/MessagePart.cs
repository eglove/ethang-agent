namespace eThangAgent.ConversationDomain;

/// <summary>One content part of a conversation message. Text parts carry plain
///     text; image parts carry an image inline. Image construction-time validation
///     rejects anything but image/png and image/jpeg with undecodable base64.</summary>
public abstract record MessagePart
{
  /// <summary>A plain-text part.</summary>
  // Named decision (CA1034): the part records nest inside MessagePart by pinned design -
  // one name carries the whole vocabulary (MessagePart.TextPart / MessagePart.ImagePart).
#pragma warning disable CA1034 // Do not nest type
  public sealed record TextPart(string Text) : MessagePart;

  /// <summary>An inline image part. The media type must be exactly image/png or
  ///     image/jpeg and the payload must be non-empty valid base64; violations are
  ///     programmer errors and throw ArgumentException at construction.</summary>
  public sealed record ImagePart : MessagePart
  {
    public ImagePart(string mediaType, string base64Data)
    {
      ArgumentNullException.ThrowIfNull(mediaType);
      ArgumentNullException.ThrowIfNull(base64Data);
      if (mediaType is not ("image/png" or "image/jpeg"))
      {
        throw new ArgumentException(
            $"Image media type must be exactly \"image/png\" or \"image/jpeg\"; got \"{mediaType}\".",
            nameof(mediaType));
      }

      if (base64Data.Length == 0)
      {
        throw new ArgumentException("Image base64 payload must be non-empty.", nameof(base64Data));
      }

      // TryFromBase64String needs a destination at least the decoded size; one byte
      // per input char is always sufficient. Heap buffer: screenshots can be megabytes,
      // and a stackalloc sized by input length risks blowing the stack.
      if (!Convert.TryFromBase64String(base64Data, new byte[base64Data.Length], out _))
      {
        throw new ArgumentException("Image base64 payload is not valid base64.", nameof(base64Data));
      }

      MediaType = mediaType;
      Base64Data = base64Data;
    }

    public string MediaType { get; }

    public string Base64Data { get; }
  }
#pragma warning restore CA1034 // Do not nest type
}
