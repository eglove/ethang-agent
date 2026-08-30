using System.Text;

namespace eThangAgent.Desktop.Markdown;

/// <summary>
/// Parser for the markdown subset rendered in the chat transcript: ATX headings,
/// fenced code blocks, bullet/ordered lists, paragraphs, and inline bold / italic /
/// inline-code / links (explicit [text](url) and bare URLs). Anything unrecognized
/// degrades to literal text - malformed input never throws and never loses content.
/// </summary>
internal static class MarkdownParser
{
  public static MarkdownDocument Parse(string source)
  {
    ArgumentNullException.ThrowIfNull(source);
    string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    List<Block> blocks = [];
    List<string> paragraph = [];
    int i = 0;

    while (i < lines.Length)
    {
      string line = lines[i];
      if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
      {
        FlushParagraph(paragraph, blocks);
        string language = line.TrimStart()[3..].Trim();
        (i, string code) = ConsumeFence(lines, i + 1);
        blocks.Add(new CodeBlock(language, code));
        continue;
      }

      if (TryHeading(line, out int level, out string headingText))
      {
        FlushParagraph(paragraph, blocks);
        blocks.Add(new HeadingBlock(level, ParseInlines(headingText)));
        i++;
        continue;
      }

      if (IsListItem(line))
      {
        FlushParagraph(paragraph, blocks);
        i = ConsumeList(lines, i, blocks);
        continue;
      }

      if (line.Trim().Length == 0)
      {
        FlushParagraph(paragraph, blocks);
        i++;
        continue;
      }

      paragraph.Add(line);
      i++;
    }

    FlushParagraph(paragraph, blocks);
    return new MarkdownDocument(blocks);
  }

  /// <summary>Consumes the fenced body starting at <paramref name="start"/>. An
  ///     unterminated fence consumes the rest of the input as code.</summary>
  private static (int NextIndex, string Code) ConsumeFence(string[] lines, int start)
  {
    List<string> body = [];
    int i = start;
    while (i < lines.Length && lines[i].Trim() != "```")
    {
      body.Add(lines[i]);
      i++;
    }

    return (i + 1, string.Join("\n", body));
  }

  private static bool TryHeading(string line, out int level, out string text)
  {
    int hashes = 0;
    while (hashes < line.Length && line[hashes] == '#' && hashes < 6)
    {
      hashes++;
    }

    if (hashes > 0 && hashes < line.Length && line[hashes] == ' ')
    {
      level = hashes;
      text = line[(hashes + 1)..].Trim();
      return true;
    }

    level = 0;
    text = "";
    return false;
  }

  private static bool IsListItem(string line)
  {
    string trimmed = line.TrimStart();
    if (trimmed.Length > 2 && trimmed[0] is '-' or '*' && trimmed[1] == ' ')
    {
      return true;
    }

    int dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
    return dot > 0 && OnlyDigits(trimmed.AsSpan(0, dot));
  }

  private static bool OnlyDigits(ReadOnlySpan<char> span)
  {
    foreach (char c in span)
    {
      if (!char.IsAsciiDigit(c))
      {
        return false;
      }
    }

    return span.Length > 0;
  }

  private static int ConsumeList(string[] lines, int start, List<Block> blocks)
  {
    bool ordered = false;
    List<IReadOnlyList<Inline>> items = [];
    int i = start;
    while (i < lines.Length && IsListItem(lines[i]))
    {
      if (items.Count == 0)
      {
        ordered = IsOrderedItem(lines[i]);
      }

      items.Add(ParseInlines(ItemText(lines[i])));
      i++;
    }

    blocks.Add(new ListBlock(ordered, items));
    return i;
  }

  private static bool IsOrderedItem(string line) => line.TrimStart().IndexOf(". ", StringComparison.Ordinal) >= 1;

  private static string ItemText(string line)
  {
    string trimmed = line.TrimStart();
    if (trimmed[0] is '-' or '*')
    {
      return trimmed[2..].Trim();
    }

    int dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
    return trimmed[(dot + 2)..].Trim();
  }

  private static void FlushParagraph(List<string> paragraph, List<Block> blocks)
  {
    if (paragraph.Count > 0)
    {
      blocks.Add(new ParagraphBlock(ParseInlines(string.Join("\n", paragraph))));
      paragraph.Clear();
    }
  }

  private static List<Inline> ParseInlines(string text)
  {
    List<Inline> inlines = [];
    StringBuilder buffer = new();
    int i = 0;
    while (i < text.Length)
    {
      char c = text[i];
      if (c == '`')
      {
        int close = text.IndexOf('`', i + 1);
        if (close > i)
        {
          FlushText(buffer, inlines);
          inlines.Add(new CodeSpan(text[(i + 1)..close]));
          i = close + 1;
          continue;
        }
      }

      if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
      {
        int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
        if (close >= i + 3) // non-empty inner content; whitespace-only stays literal
        {
          FlushText(buffer, inlines);
          inlines.Add(new BoldSpan(ParseInlines(text[(i + 2)..close])));
          i = close + 2;
          continue;
        }
      }

      if (c == '*')
      {
        int close = text.IndexOf('*', i + 1);
        if (close >= i + 2) // non-empty inner content; emphasis cannot open on whitespace
        {
          FlushText(buffer, inlines);
          inlines.Add(new ItalicSpan(ParseInlines(text[(i + 1)..close])));
          i = close + 1;
          continue;
        }
      }

      if (c == '[' && TryLink(text, i, out LinkSpan? link, out int end))
      {
        FlushText(buffer, inlines);
        inlines.Add(link);
        i = end;
        continue;
      }

      if (StartsBareUrl(text, i, out int urlEnd))
      {
        FlushText(buffer, inlines);
        string url = text[i..urlEnd].TrimEnd('.', ',', ';', ':', '!', '?');
        inlines.Add(new LinkSpan(url, url));
        i += url.Length; // trailing punctuation stays in the stream as text
        continue;
      }

      _ = buffer.Append(c);
      i++;
    }

    FlushText(buffer, inlines);
    return inlines;
  }

  private static void FlushText(StringBuilder buffer, List<Inline> inlines)
  {
    if (buffer.Length > 0)
    {
      inlines.Add(new TextSpan(buffer.ToString()));
      _ = buffer.Clear();
    }
  }

  private static bool TryLink(string text, int open, out LinkSpan link, out int end)
  {
    link = new LinkSpan("", "");
    end = open;
    int close = text.IndexOf(']', open + 1);
    if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
    {
      return false;
    }

    int endParen = text.IndexOf(')', close + 2);
    if (endParen < 0)
    {
      return false;
    }

    string url = text[(close + 2)..endParen];
    if (url.Length == 0 || url.AsSpan().IndexOfAny(" \t".AsSpan()) >= 0)
    {
      return false;
    }

    link = new LinkSpan(text[(open + 1)..close], url);
    end = endParen + 1;
    return true;
  }

  private static bool StartsBareUrl(string text, int start, out int end)
  {
    end = start;
    bool isHttps = text.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    bool isHttp = !isHttps && text.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    if (!isHttps && !isHttp)
    {
      return false;
    }

    int i = start;
    while (i < text.Length && !char.IsWhiteSpace(text[i]))
    {
      i++;
    }

    if (i - start <= 8)
    {
      return false; // scheme only - not a URL
    }

    end = i;
    return true;
  }
}
