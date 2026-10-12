using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;

namespace eThangAgent.PDF.ACL;

/// <summary>One inline span of a markdown paragraph: the text plus its bold/italic
///     flags (the pinned subset's only inline formatting).</summary>
internal sealed record PdfMarkdownSpan(string Text, bool Bold, bool Italic);

/// <summary>The intermediate markdown block list the parser produces and the
///     composer consumes. The marker record is deliberately empty: it types the
///     closed block vocabulary (the DocxMarkdownBlock pattern).</summary>
#pragma warning disable S2094 // Named decision: deliberate empty marker record typing the closed block vocabulary.
internal abstract record PdfMarkdownBlock;
#pragma warning restore S2094

internal sealed record PdfMarkdownHeading(int Level, string Text) : PdfMarkdownBlock;

internal sealed record PdfMarkdownListItem(bool Ordered, int Number, string Text) : PdfMarkdownBlock;

internal sealed record PdfMarkdownTable(IReadOnlyList<IReadOnlyList<string>> Rows) : PdfMarkdownBlock;

internal sealed record PdfMarkdownParagraph(IReadOnlyList<PdfMarkdownSpan> Spans) : PdfMarkdownBlock;

/// <summary>A markdown-subset violation carrying the 1-based line number
///     (0 = the whole input, e.g. the character cap).</summary>
public sealed class PdfMarkdownInvalidException : Exception
{
  public PdfMarkdownInvalidException()
      : base("markdown violates the pinned subset")
  {
  }

  public PdfMarkdownInvalidException(int line)
      : base(line == 0 ? "markdown violates the pinned subset" : $"line {line}")
  {
  }

  public PdfMarkdownInvalidException(string message)
      : base(message)
  {
  }

  public PdfMarkdownInvalidException(string message, Exception inner)
      : base(message, inner)
  {
  }
}

/// <summary>The create path (task 6): renders the pinned markdown subset (the SAME
///     grammar docx's DocxMarkdownParser accepts) to a PDF through QuestPDF's fluent
///     layout, then reports the new document's page count. The QuestPDF Community
///     license is claimed once in the static constructor. Inputs over
///     <see cref="PdfLimits.MaxMarkdownChars"/> and subset violations fail
///     MarkdownInvalid; library faults map to CorruptPdf - never an exception.</summary>
public static class PdfComposer
{
  static PdfComposer() => QuestPDF.Settings.License = LicenseType.Community;

  /// <summary>Composes <paramref name="markdown"/> into a new PDF at
  ///     <paramref name="outputPath"/>, returning the page count or a typed error.</summary>
  public static Result<int> Compose(string markdown, string outputPath)
  {
    ArgumentNullException.ThrowIfNull(markdown);
    ArgumentNullException.ThrowIfNull(outputPath);
    if (markdown.Length > PdfLimits.MaxMarkdownChars)
    {
      return Fail(PdfErrorCodes.MarkdownInvalid,
          $"markdown is {markdown.Length} characters; the maximum is {PdfLimits.MaxMarkdownChars}.");
    }

    IReadOnlyList<PdfMarkdownBlock> blocks;
    try
    {
      blocks = Parse(markdown);
    }
    catch (PdfMarkdownInvalidException ex)
    {
      return Fail(PdfErrorCodes.MarkdownInvalid, ex.Message);
    }

    try
    {
      Document.Create(container =>
      {
        _ = container.Page(page =>
        {
          page.Size(PageSizes.A4);
          page.Margin(2, Unit.Centimetre);
          page.DefaultTextStyle(style => style.FontSize(11));
          page.Content().Column(column =>
          {
            foreach (PdfMarkdownBlock block in blocks)
            {
              RenderBlock(column, block);
            }
          });
        });
      }).GeneratePdf(outputPath);
    }
#pragma warning disable CA1031 // Named decision: the compose boundary maps every library fault to a typed DomainError; an unknown failure must never escape as an exception.
    catch (Exception ex)
    {
      return Fail(PdfErrorCodes.CorruptPdf, $"'{outputPath}' could not be composed: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031

    try
    {
      using PdfDocument doc = PdfDocument.Open(outputPath);
      return Result.Success(doc.NumberOfPages);
    }
#pragma warning disable CA1031 // Named decision: the read-back boundary maps every library fault to a typed DomainError.
    catch (Exception ex)
    {
      return Fail(PdfErrorCodes.CorruptPdf, $"'{outputPath}' could not be read back: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031
  }

  private static void RenderBlock(ColumnDescriptor column, PdfMarkdownBlock block)
  {
    switch (block)
    {
      case PdfMarkdownHeading heading:
        column.Item().PaddingTop(heading.Level == 1 ? 0 : 8).Text(text =>
        {
          TextSpanDescriptor span = text.Span(heading.Text);
          _ = heading.Level switch
          {
            1 => span.FontSize(22).Bold(),
            2 => span.FontSize(17).Bold(),
            _ => span.FontSize(14).Bold(),
          };
        });
        break;
      case PdfMarkdownListItem item:
        column.Item().PaddingTop(2).Row(row =>
        {
          _ = row.ConstantItem(20).Text(item.Ordered ? $"{item.Number}." : "\u2022");
          row.RelativeItem().Text(text => RenderSpans(text, [new PdfMarkdownSpan(item.Text, false, false)]));
        });
        break;
      case PdfMarkdownTable table:
        column.Item().PaddingTop(4).Table(t =>
        {
          t.ColumnsDefinition(columns =>
          {
            int cols = table.Rows.Count > 0 ? table.Rows[0].Count : 0;
            for (int c = 0; c < cols; c++)
            {
              columns.RelativeColumn();
            }
          });
          foreach (IReadOnlyList<string> cells in table.Rows)
          {
            foreach (string cell in cells)
            {
              _ = t.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(cell);
            }
          }
        });
        break;
      case PdfMarkdownParagraph paragraph:
        column.Item().PaddingTop(2).Text(text => RenderSpans(text, paragraph.Spans));
        break;
      default:
        break;
    }
  }

  private static void RenderSpans(TextDescriptor text, IReadOnlyList<PdfMarkdownSpan> spans)
  {
    foreach (PdfMarkdownSpan span in spans)
    {
      TextSpanDescriptor descriptor = text.Span(span.Text);
      if (span.Bold)
      {
        descriptor = descriptor.Bold();
      }

      if (span.Italic)
      {
        _ = descriptor.Italic();
      }
    }
  }

  /// <summary>Parses the pinned markdown subset into the intermediate block list
  ///     (the DocxMarkdownParser grammar, mirrored): '# '/'## '/'### ' headings;
  ///     '- ' bullets; '1. ' numbered; consecutive pipe lines form a table
  ///     ('|---|' separator rows dropped); blank line ends the block; consecutive
  ///     non-blank plain lines merge into one paragraph (soft wrap); **bold** and
  ///     *italic* inline spans. Any deviation fails with the 1-based line number.</summary>
  private static List<PdfMarkdownBlock> Parse(string markdown)
  {
    List<PdfMarkdownBlock> blocks = [];
    string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    List<string> paragraphLines = [];
    List<string>? tableLines = null;
    int tableStart = 0;
    for (int i = 0; i < lines.Length; i++)
    {
      string trimmed = lines[i].TrimEnd();
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
          tableStart = i + 1;
          tableLines = [];
        }

        tableLines.Add(trimmed);
        continue;
      }

      FlushTable(tableLines, tableStart, blocks);
      tableLines = null;

      if (trimmed.StartsWith("### ", StringComparison.Ordinal))
      {
        FlushParagraph(paragraphLines, blocks);
        blocks.Add(new PdfMarkdownHeading(3, trimmed[4..]));
        continue;
      }

      if (trimmed.StartsWith("## ", StringComparison.Ordinal))
      {
        FlushParagraph(paragraphLines, blocks);
        blocks.Add(new PdfMarkdownHeading(2, trimmed[3..]));
        continue;
      }

      if (trimmed.StartsWith("# ", StringComparison.Ordinal))
      {
        FlushParagraph(paragraphLines, blocks);
        blocks.Add(new PdfMarkdownHeading(1, trimmed[2..]));
        continue;
      }

      if (trimmed.StartsWith('#'))
      {
        throw new PdfMarkdownInvalidException(i + 1);
      }

      if (trimmed.StartsWith("- ", StringComparison.Ordinal))
      {
        FlushParagraph(paragraphLines, blocks);
        blocks.Add(new PdfMarkdownListItem(false, 0, trimmed[2..]));
        continue;
      }

      int dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
      if (dot > 0 && trimmed[..dot].All(char.IsAsciiDigit)
          && int.TryParse(trimmed[..dot], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
      {
        FlushParagraph(paragraphLines, blocks);
        blocks.Add(new PdfMarkdownListItem(true, number, trimmed[(dot + 2)..]));
        continue;
      }

      paragraphLines.Add(trimmed);
    }

    FlushParagraph(paragraphLines, blocks);
    FlushTable(tableLines, tableStart, blocks);
    return blocks;
  }

  /// <summary>A '|---|' separator row: every cell is only dashes/colons/spaces.</summary>
  private static bool IsSeparatorRow(string line)
    => line.Split('|').All(c => c.Trim().Length == 0 || c.Trim().All(d => d is '-' or ':'));

  private static void FlushParagraph(List<string> pending, List<PdfMarkdownBlock> blocks)
  {
    if (pending.Count == 0)
    {
      return;
    }

    blocks.Add(new PdfMarkdownParagraph(SpansOf(string.Join(' ', pending))));
    pending.Clear();
  }

  private static void FlushTable(List<string>? pipeLines, int firstLineNo, List<PdfMarkdownBlock> blocks)
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
        continue;
      }

      string inner = line[1..];
      if (inner.EndsWith('|'))
      {
        inner = inner[..^1];
      }

      List<string> cells = [.. inner.Split('|').Select(c => c.Trim())];
      if (rows.Count > 0 && cells.Count != rows[0].Count)
      {
        throw new PdfMarkdownInvalidException(firstLineNo + i);
      }

      rows.Add(cells);
    }

    blocks.Add(new PdfMarkdownTable(rows));
  }

  /// <summary>Splits one paragraph text into bold/italic spans: **bold** wins at
  ///     overlap; an unclosed ** or * marker is a violation.</summary>
  private static List<PdfMarkdownSpan> SpansOf(string text)
  {
    List<PdfMarkdownSpan> spans = [];
    int at = 0;
    while (at < text.Length)
    {
      int bold = text.IndexOf("**", at, StringComparison.Ordinal);
      int italic = text.IndexOf('*', at);
      if (bold < 0 && italic < 0)
      {
        spans.Add(new PdfMarkdownSpan(text[at..], false, false));
        break;
      }

      int next = bold >= 0 && (italic < 0 || bold <= italic) ? bold : italic;
      if (next > at)
      {
        spans.Add(new PdfMarkdownSpan(text[at..next], false, false));
      }

      if (next == bold)
      {
        int close = text.IndexOf("**", bold + 2, StringComparison.Ordinal);
        if (close < 0)
        {
          throw new PdfMarkdownInvalidException(0);
        }

        spans.Add(new PdfMarkdownSpan(text[(bold + 2)..close], true, false));
        at = close + 2;
      }
      else
      {
        int close = text.IndexOf('*', italic + 1);
        if (close < 0)
        {
          throw new PdfMarkdownInvalidException(0);
        }

        spans.Add(new PdfMarkdownSpan(text[(italic + 1)..close], false, true));
        at = close + 1;
      }
    }

    return spans;
  }

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<int> Fail(string code, string message)
      => Result.Failure<int>(new DomainError(code, message));
}
