namespace eThangAgent.ToolDomain;

/// <summary>Magic-byte format sniffing shared by every image entry point (issue #20):
///     the read_image tool sniffs workspace files; the Desktop paste path sniffs
///     dropped files. One source of truth so the accepted-format rule can never drift
///     between the two channels. PNG: 89 50 4E 47 0D 0A 1A 0A; JPEG: FF D8 FF.</summary>
public static class ImageFormats
{
  private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
  private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

  /// <summary>The sniffed media type: exactly "image/png" or "image/jpeg", or null
  ///     when the bytes match neither (null is the caller's rejection signal).</summary>
  public static string? SniffMediaType(byte[] bytes)
  {
    ArgumentNullException.ThrowIfNull(bytes);
    return (bytes: PngBytes(bytes), jpeg: JpegBytes(bytes)) switch
    {
      (true, _) => "image/png",
      (_, true) => "image/jpeg",
      _ => null,
    };
  }

  /// <summary>True when the bytes start with the 8-byte PNG signature.</summary>
  public static bool PngBytes(byte[] bytes)
  {
    ArgumentNullException.ThrowIfNull(bytes);
    return bytes.Length >= PngMagic.Length && bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic);
  }

  /// <summary>True when the bytes start with the 3-byte JPEG marker.</summary>
  public static bool JpegBytes(byte[] bytes)
  {
    ArgumentNullException.ThrowIfNull(bytes);
    return bytes.Length >= JpegMagic.Length && bytes.AsSpan(0, JpegMagic.Length).SequenceEqual(JpegMagic);
  }
}

/// <summary>The shared image bounds (issue #20): 20 MB decoded per image covers
///     full-screen 4K captures with headroom; 4 images per message is a
///     provider-friendly batch. Enforced at every entry point — typed errors and
///     notices, never silent clamps.</summary>
public static class ImageLimits
{
  public const int MaxBytes = 20 * 1024 * 1024;

  public const int MaxPerMessage = 4;
}
