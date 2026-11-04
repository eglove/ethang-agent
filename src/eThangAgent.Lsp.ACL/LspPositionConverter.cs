namespace eThangAgent.Lsp.ACL;

/// <summary>UTF-16 position conversion (issue #353): LSP positions count UTF-16
/// code units, not UTF-8 bytes or Unicode scalar values. A surrogate pair (e.g. an
/// emoji) occupies two UTF-16 units but one scalar.</summary>
public static class LspPositionConverter
{
  /// <summary>Converts a scalar (char) offset within a line to a UTF-16 offset.</summary>
  public static int ToUtf16Offset(string line, int charOffset)
  {
    ArgumentNullException.ThrowIfNull(line);
    if (charOffset < 0 || charOffset > line.Length) throw new ArgumentOutOfRangeException(nameof(charOffset));
    int utf16 = 0;
    int i = 0;
    while (i < charOffset)
    {
      if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
      {
        utf16 += 2;
        i += 2;
      }
      else
      {
        utf16 += 1;
        i += 1;
      }
    }
    return utf16;
  }

  /// <summary>Converts a UTF-16 offset within a line back to a scalar (char) offset.</summary>
  public static int FromUtf16Offset(string line, int utf16Offset)
  {
    ArgumentNullException.ThrowIfNull(line);
    if (utf16Offset < 0) throw new ArgumentOutOfRangeException(nameof(utf16Offset));
    int utf16 = 0;
    int i = 0;
    while (i < line.Length && utf16 < utf16Offset)
    {
      if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
      {
        utf16 += 2;
        i += 2;
      }
      else
      {
        utf16 += 1;
        i += 1;
      }
    }
    return i;
  }

  /// <summary>Converts a scalar offset in a full document to (line, UTF-16 column).</summary>
  public static (int Line, int Character) ToPosition(string document, int charOffset)
  {
    ArgumentNullException.ThrowIfNull(document);
    if (charOffset < 0 || charOffset > document.Length) throw new ArgumentOutOfRangeException(nameof(charOffset));
    int line = 0;
    int lineStart = 0;
    for (int i = 0; i < charOffset && i < document.Length; i++)
    {
      if (document[i] == '\n') { line++; lineStart = i + 1; }
    }
    string lineText = document.Substring(lineStart, charOffset - lineStart);
    int col = ToUtf16Offset(lineText, lineText.Length);
    return (line, col);
  }
}
