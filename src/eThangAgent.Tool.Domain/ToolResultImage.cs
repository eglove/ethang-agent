namespace eThangAgent.ToolDomain;

/// <summary>One image a tool attaches to its result. Validation rules mirror the
///     conversation domain's image part (deliberate duplication: Tool.Domain must
///     not reference Conversation.Domain) - media type exactly image/png or
///     image/jpeg, non-empty valid base64; violations are programmer errors.
///     The agent loop converts these to MessagePart.ImagePart entries.</summary>
public sealed record ToolResultImage
{
  public ToolResultImage(string mediaType, string base64Data)
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

    // Heap destination: screenshots can be megabytes, and a stackalloc sized by
    // input length risks blowing the stack.
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
