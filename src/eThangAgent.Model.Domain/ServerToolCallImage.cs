namespace eThangAgent.ModelDomain;

/// <summary>One image a server-side tool produced inside the provider's own response
///     (the openrouter:image_generation tool's output item). Provider-neutral by design:
///     a media type plus a base64 payload, never an OpenRouter-specific type — the wire
///     ACL decodes the provider's spelling into this shape and the domain never learns
///     what a "data:" URL is.
/// <para>Validation mirrors the other image carriers (MessagePart.ImagePart,
///     ToolResultImage): media type exactly image/png or image/jpeg, non-empty valid
///     base64. Violations are programmer errors — the wire parser only constructs one
///     after validating the payload, and a malformed payload parses as NO image rather
///     than throwing (the parser's tolerance contract).</para></summary>
public sealed record ServerToolCallImage
{
  public ServerToolCallImage(string mediaType, string base64Data)
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

    // Heap destination: a generated image can be megabytes, and a stackalloc sized by
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
