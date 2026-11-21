namespace eThangAgent.Provider.Wire;

/// <summary>THE ONE PLACE A data: URL IS SPELLED — an image's bytes leaving this process.
///     <para>EVERY PROVIDER ENCODES THROUGH HERE, so the wire shape an image travels in is one
///     string built one way. A provider that spelled its own would be a second answer waiting to
///     disagree about the same bytes.</para>
///     <para>THE CALLER OWNS THE DECISION, NOT THE SPELLING: whether an image's bytes are sent at
///     all is <c>ImageResendPlan</c>'s answer, and every caller consults it before reaching here.
///     This type builds the URL for a decision already made.</para></summary>
public static class ImageDataUrl
{
  /// <summary>The data URL carrying <paramref name="base64Data"/> as <paramref name="mediaType"/>.
  ///     </summary>
  /// <param name="mediaType">The image's media type, such as image/png.</param>
  /// <param name="base64Data">The image's bytes, base64-encoded.</param>
  public static string For(string mediaType, string base64Data)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
    ArgumentException.ThrowIfNullOrWhiteSpace(base64Data);
    return $"data:{mediaType};base64,{base64Data}";
  }
}
