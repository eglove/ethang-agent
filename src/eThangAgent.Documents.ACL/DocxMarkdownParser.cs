namespace eThangAgent.Documents.ACL;

/// <summary>One inline span of a markdown paragraph: the text plus its bold/italic
///     flags (the pinned subset's only inline formatting).</summary>
public sealed record DocxMarkdownSpan(string Text, bool Bold, bool Italic);

/// <summary>The intermediate markdown block list the parser produces and the
///     document builder consumes. The marker record is deliberately empty: it types
///     the closed block vocabulary (the DocxOperation pattern).</summary>
#pragma warning disable S2094 // Named decision: deliberate empty marker record typing the closed block vocabulary.
public abstract record DocxMarkdownBlock;
#pragma warning restore S2094

public sealed record DocxMarkdownHeading(int Level, string Text) : DocxMarkdownBlock;

public sealed record DocxMarkdownListItem(bool Ordered, string Text) : DocxMarkdownBlock;

public sealed record DocxMarkdownTable(IReadOnlyList<IReadOnlyList<string>> Rows) : DocxMarkdownBlock;

public sealed record DocxMarkdownParagraph(IReadOnlyList<DocxMarkdownSpan> Spans) : DocxMarkdownBlock;

/// <summary>A markdown-subset violation carrying the 1-based line number
///     (0 = the whole input, e.g. the character cap).</summary>
public sealed class MarkdownInvalidException : Exception
{
  public MarkdownInvalidException()
      : base("markdown violates the pinned subset")
  {
  }

  public MarkdownInvalidException(int line)
      : base(line == 0 ? "markdown violates the pinned subset" : $"line {line}")
  {
  }

  public MarkdownInvalidException(string message)
      : base(message)
  {
  }

  public MarkdownInvalidException(string message, Exception inner)
      : base(message, inner)
  {
  }
}

/// <summary>Parses the pinned markdown subset (task 8): '# '/'## '/'### ' headings;
///     '- ' bullets; '1. ' numbered; consecutive pipe lines form a table
///     ('|---|' separator rows dropped); blank line ends the block; consecutive
///     non-blank plain lines merge into one paragraph (soft wrap); **bold** and
///     *italic* inline spans (bold wins at overlap; an unclosed marker is
///     MarkdownInvalid naming the line). Any deviation fails MarkdownInvalid with
///     the 1-based line number.</summary>
public static class DocxMarkdownParser
{
  /// <summary>Parses the markdown into the intermediate block list. Throws
  ///     <see cref="MarkdownInvalidException"/> on subset violations (the caller
  ///     translates to the typed error).</summary>
  public static IReadOnlyList<DocxMarkdownBlock> Parse(string markdown)
  {
    ArgumentNullException.ThrowIfNull(markdown);
    if (markdown.Length > DocxLimits.MaxMarkdownChars)
    {
      throw new MarkdownInvalidException(0);
    }

    List<DocxMarkdownBlock> blocks = [];
    string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    List<string> paragraphLines = [];
    List<string>? tableLines = null;
    int tableStart = 0;
    for (int i = 0; i < lines.Length; i++)
    {
      string line = lines[i];
      string trimmed = line.TrimEnd();
      if (trimmed.Length == 0)
      {
        FlushParagraph(paragraphLines, blocks);
        FlushTable(tableLines, tableStart, blocks);
        tableLines = null;
        continue;
      }

      if (trimmed.StartsWith('|'))
      {
        paragraphLines.Clear();
        if (tableLines is null)
        {
          tableStart = i + 1; // 1-based line the table starts at
          tableLines = [];
        }

        tableLines.Add(trimmed);
        continue;
      }

      FlushTable(tableLines, tableStart, blocks);
      tableLines = null;
      if (StructuredLine(trimmed, i, paragraphLines, blocks))
      {
        continue;
      }

      paragraphLines.Add(trimmed);
    }

    FlushParagraph(paragraphLines, blocks);
    FlushTable(tableLines, tableStart, blocks);
    return blocks;
  }

  /// <summary>One structured (non-plain) line: heading, list item, or a bad
  ///     heading marker. Returns true when the line was consumed as a block (the
  ///     loop continues); plain paragraph lines fall through to the paragraph
  ///     accumulator. The line number is the 1-based source line.</summary>
  private static bool StructuredLine(string trimmed, int lineNo, List<string> paragraphLines, List<DocxMarkdownBlock> blocks)
  {
    if (trimmed.StartsWith("### ", StringComparison.Ordinal))
    {
      FlushParagraph(paragraphLines, blocks);
      blocks.Add(new DocxMarkdownHeading(3, trimmed[4..]));
      return true;
    }

    if (trimmed.StartsWith("## ", StringComparison.Ordinal))
    {
      FlushParagraph(paragraphLines, blocks);
      blocks.Add(new DocxMarkdownHeading(2, trimmed[3..]));
      return true;
    }

    if (trimmed.StartsWith("# ", StringComparison.Ordinal))
    {
      FlushParagraph(paragraphLines, blocks);
      blocks.Add(new DocxMarkdownHeading(1, trimmed[2..]));
      return true;
    }

    if (trimmed.StartsWith('#'))
    {
      throw new MarkdownInvalidException(lineNo + 1);
    }

    if (trimmed.StartsWith("- ", StringComparison.Ordinal))
    {
      FlushParagraph(paragraphLines, blocks);
      blocks.Add(new DocxMarkdownListItem(false, trimmed[2..]));
      return true;
    }

    return NumberedItem(trimmed, paragraphLines, blocks);
  }

  /// <summary>A '1. ' numbered list item: the prefix before ". " is all ASCII
  ///     digits and non-empty. Returns true when the line was consumed.</summary>
  private static bool NumberedItem(string trimmed, List<string> paragraphLines, List<DocxMarkdownBlock> blocks)
  {
    int dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
    if (dot > 0 && trimmed[..dot].All(char.IsAsciiDigit))
    {
      FlushParagraph(paragraphLines, blocks);
      blocks.Add(new DocxMarkdownListItem(true, trimmed[(dot + 2)..]));
      return true;
    }

    return false;
  }

  /// <summary>A '|---|' separator row: every cell is only dashes/colons/spaces.</summary>
  private static bool IsSeparatorRow(string line)
    => line.Split('|').All(c => c.Trim().Length == 0 || c.Trim().All(d => d is '-' or ':'));

  private static void FlushParagraph(List<string> pending, List<DocxMarkdownBlock> blocks)
  {
    if (pending.Count == 0)
    {
      return;
    }

    string joined = string.Join(' ', pending);
    blocks.Add(new DocxMarkdownParagraph(SpansOf(joined)));
    pending.Clear();
  }

  private static void FlushTable(List<string>? pipeLines, int firstLineNo, List<DocxMarkdownBlock> blocks)
  {
    if (pipeLines is null || pipeLines.Count == 0)
    {
      return;
    }

    List<List<string>> rows = [];
    for (int i = 0; i < pipeLines.Count; i++)
    {
      string line = pipeLines[i];
      if (IsSeparatorRow(line))
      {
        continue; // separator row, dropped
      }

      string inner = line[1..];
      if (inner.EndsWith('|'))
      {
        inner = inner[..^1];
      }

      List<string> cells = [.. inner.Split('|').Select(c => c.Trim())];
      if (rows.Count > 0 && cells.Count != rows[0].Count)
      {
        throw new MarkdownInvalidException(firstLineNo + i);
      }

      rows.Add(cells);
    }

    blocks.Add(new DocxMarkdownTable(rows));
  }

  /// <summary>Splits one paragraph text into bold/italic spans: **bold** wins at
  ///     overlap; an unclosed ** or * marker is a violation (line resolved by the
  ///     caller's wrapper).</summary>
  public static IReadOnlyList<DocxMarkdownSpan> SpansOf(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    List<DocxMarkdownSpan> spans = [];
    int at = 0;
    while (at < text.Length)
    {
      int bold = text.IndexOf("**", at, StringComparison.Ordinal);
      int italic = text.IndexOf('*', at);
      if (bold < 0 && italic < 0)
      {
        spans.Add(new DocxMarkdownSpan(text[at..], false, false));
        break;
      }

      int next = bold >= 0 && (italic < 0 || bold <= italic) ? bold : italic;
      if (next > at)
      {
        spans.Add(new DocxMarkdownSpan(text[at..next], false, false));
      }

      at = next == bold ? BoldSpan(text, bold, spans) : ItalicSpan(text, italic, spans);
    }

    return spans;

    // Consumes one **bold** span: the closing "**" must exist (an unclosed
    // marker is a violation); returns the index after it.
    static int BoldSpan(string text, int open, List<DocxMarkdownSpan> spans)
    {
      int close = text.IndexOf("**", open + 2, StringComparison.Ordinal);
      if (close < 0)
      {
        throw new MarkdownInvalidException(0);
      }

      spans.Add(new DocxMarkdownSpan(text[(open + 2)..close], true, false));
      return close + 2;
    }

    // Consumes one *italic* span: the closing '*' must exist (an unclosed
    // marker is a violation); returns the index after it.
    static int ItalicSpan(string text, int open, List<DocxMarkdownSpan> spans)
    {
      int close = text.IndexOf('*', open + 1);
      if (close < 0)
      {
        throw new MarkdownInvalidException(0);
      }

      spans.Add(new DocxMarkdownSpan(text[(open + 1)..close], false, true));
      return close + 1;
    }
  }
}
