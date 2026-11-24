namespace eThangAgent.Presentation.ACL.Tests;

/// <summary>Image files a media test writes to the scratch directory: real PNG and JPEG
///     bytes, so a picture's format is decided by its content rather than its name.
///     </summary>
internal static class TestPptxImage
{
  /// <summary>A 16x16 solid blue PNG.</summary>
  internal const string PngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQAQMAAAAlPW0iAAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAADUExURQAA/4p40lcAAAAHdElNRQfqCxgAGDgzb/ziAAAAJXRFWHRkYXRlOmNyZWF0ZQAyMDI2LTExLTI0VDAwOjI0OjU2KzAwOjAwtlJ5yQAAACV0RVh0ZGF0ZTptb2RpZnkAMjAyNi0xMS0yNFQwMDoyNDo1NiswMDowMMcPwXUAAAAodEVYdGRhdGU6dGltZXN0YW1wADIwMjYtMTEtMjRUMDA6MjQ6NTYrMDA6MDCQGuCqAAAADElEQVQI12NgIA0AAAAwAAHHqoWOAAAAAElFTkSuQmCC";

  /// <summary>A 16x16 solid red JPEG.</summary>
  internal const string JpegBase64 =
      "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBD/wAARCAAQABADAREAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAj/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFgEBAQEAAAAAAAAAAAAAAAAAAAcJ/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAwDAQACEQMRAD8AnRDGqYAAD//Z";

  /// <summary>A GIF header followed by no useful data: a file that is neither PNG nor
  ///     JPEG, which is what the refusal cases need.</summary>
  private const string GifBytes = "GIF89a";

  /// <summary>Writes a PNG to the scratch directory and returns its path.</summary>
  internal static string WritePng(string path) => Write(path, PngBase64);

  /// <summary>Writes a JPEG to the scratch directory and returns its path.</summary>
  internal static string WriteJpeg(string path) => Write(path, JpegBase64);

  /// <summary>Writes a file that is neither PNG nor JPEG and returns its path.</summary>
  internal static string WriteGif(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(GifBytes));
    return path;
  }

  /// <summary>The bytes a PNG written by <see cref="WritePng"/> carries.</summary>
  internal static byte[] PngBytes() => Convert.FromBase64String(PngBase64);

  /// <summary>The bytes a JPEG written by <see cref="WriteJpeg"/> carries.</summary>
  internal static byte[] JpegBytes() => Convert.FromBase64String(JpegBase64);

  private static string Write(string path, string base64)
  {
    ArgumentNullException.ThrowIfNull(path);
    File.WriteAllBytes(path, Convert.FromBase64String(base64));
    return path;
  }
}
