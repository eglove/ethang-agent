namespace eThangAgent.ToolDomain.Tests;

/// <summary>Magic-byte sniffing and the shared image limits (issue #20): PNG and JPEG
///     are the only accepted formats; everything else sniffs to null. The byte rules
///     are pinned so the tool and the Desktop paste path can never drift apart.</summary>
public class ImageFormatsTests
{
  [Fact]
  public void Sniff_PngMagicBytes_ReturnsImagePng()
  {
    // PNG signature: 89 50 4E 47 0D 0A 1A 0A followed by an IHDR chunk header.
    byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    Assert.Equal("image/png", ImageFormats.SniffMediaType(png));
  }

  [Fact]
  public void Sniff_JpegMagicBytes_ReturnsImageJpeg()
  {
    // JPEG: FF D8 FF then a marker byte.
    byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    Assert.Equal("image/jpeg", ImageFormats.SniffMediaType(jpeg));
  }

  [Fact]
  public void Sniff_GifBytes_ReturnsNull()
  {
    byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]; // "GIF89a"
    Assert.Null(ImageFormats.SniffMediaType(gif));
  }

  [Fact]
  public void Sniff_EmptyOrShortBytes_ReturnsNull()
  {
    Assert.Null(ImageFormats.SniffMediaType([]));
    string? shortResult = ImageFormats.SniffMediaType([0x89, 0x50]);
    Assert.Null(shortResult);
  }

  [Fact]
  public void Sniff_Null_Throws() =>
    Assert.Throws<ArgumentNullException>(() => ImageFormats.SniffMediaType(null!));

  [Fact]
  public void Limits_HaveThePinnedValues()
  {
    Assert.Equal(20 * 1024 * 1024, ImageLimits.MaxBytes);
    Assert.Equal(4, ImageLimits.MaxPerMessage);
  }
}
