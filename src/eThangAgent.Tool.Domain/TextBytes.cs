using System.Text;
using System.Text.Unicode;

namespace eThangAgent.ToolDomain;

/// <summary>Whether a file's bytes are text, and which encoding decodes them. The line-reading
///     seam and every caller that decides "this is a text file" ask here, so the answer cannot
///     differ between the read tool and the composer's reference resolution.
///     <para>A NUL byte is the binary signal: no text encoding this harness reads produces one,
///     while every container format (an archive, a document, an image) carries them in its
///     header. Bytes that are not valid UTF-8 and declare no byte-order mark are refused for the
///     same reason - decoding them would return a page of replacement characters that describes
///     the decoder rather than the file.</para></summary>
public static class TextBytes
{
  private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

  private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];

  private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

  /// <summary>True when the bytes decode as text in some encoding this harness reads.</summary>
  public static bool IsText(ReadOnlySpan<byte> bytes) => Detect(bytes) is not null;

  /// <summary>The encoding that decodes <paramref name="bytes"/>, or null when they are not
  ///     text. Empty bytes are text: an empty file has no content to be wrong about.</summary>
  public static Encoding? Detect(ReadOnlySpan<byte> bytes)
  {
    // A byte-order mark names its encoding outright, and a UTF-16 file is full of NUL bytes
    // that would otherwise read as binary, so the marks are consulted before the NUL scan.
    Encoding? marked = MarkedEncoding(bytes);
    return marked ?? (bytes.Contains((byte)0) || !Utf8.IsValid(bytes) ? null : Encoding.UTF8);
  }

  /// <summary>The encoding a byte-order mark declares, or null when the bytes carry none.
  ///     An empty file is text and declares nothing.</summary>
  private static Encoding? MarkedEncoding(ReadOnlySpan<byte> bytes) => bytes switch
  {
    _ when bytes.IsEmpty || bytes.StartsWith(Utf8Bom) => Encoding.UTF8,
    _ when bytes.StartsWith(Utf16LeBom) => Encoding.Unicode,
    _ when bytes.StartsWith(Utf16BeBom) => Encoding.BigEndianUnicode,
    _ => null,
  };
}
