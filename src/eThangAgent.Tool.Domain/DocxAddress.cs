using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Block address kinds for the pinned docx address grammar (task 1).</summary>
public enum DocxAddressKind
{
  Paragraph,
  Table,
  TableCell,
  HeaderParagraph,
  FooterParagraph,
}

/// <summary>Grammar (pinned verbatim in the tool description): p&lt;N&gt; body paragraph;
///     t&lt;N&gt; body table; t&lt;N&gt;.r&lt;R&gt;.c&lt;C&gt; table cell; sec&lt;S&gt;.header.p&lt;N&gt; /
///     sec&lt;S&gt;.footer.p&lt;N&gt; header/footer paragraph. All indices 1-based.</summary>
public sealed record DocxAddress(DocxAddressKind Kind, int BodyIndex, int Row, int Col, int Section, string Raw)
{
  /// <summary>Parses an address against the pinned grammar. Nothing is trimmed (the raw
  ///     text must already be exact), lowercase is NOT applied (the grammar is
  ///     case-sensitive), and every index parses with int.TryParse under the invariant
  ///     culture and must be >= 1. Any violation is a typed InvalidAddress failure
  ///     (an error value, never an exception) carrying the grammar reminder.</summary>
  public static Result<DocxAddress> Parse(string? raw)
  {
    if (string.IsNullOrEmpty(raw))
    {
      return Fail(raw, "address text is empty");
    }

    string text = raw; // non-null past the guard; the helpers take it verbatim.
    string[] segments = text.Split('.');
    return segments.Length switch
    {
      1 when segments[0][0] == 'p' => ParseIndex(segments[0][1..], text, DocxAddressKind.Paragraph),
      1 when segments[0][0] == 't' => ParseIndex(segments[0][1..], text, DocxAddressKind.Table),
      3 when segments[0][0] == 't' => ParseCell(segments, text),
      3 when segments[0].StartsWith("sec", StringComparison.Ordinal) => ParseLayered(segments, text),
      _ => Fail(raw, null),
    };
  }

  private static Result<DocxAddress> ParseCell(string[] segments, string raw)
  {
    if (segments[1][0] != 'r' || segments[2][0] != 'c')
    {
      return Fail(raw, null);
    }

    Result<int> row = Index(segments[1][1..]);
    if (!row.IsSuccess)
    {
      return Fail(raw, null);
    }

    Result<int> col = Index(segments[2][1..]);
    return !col.IsSuccess
      ? Fail(raw, null)
      : Result.Success(new DocxAddress(DocxAddressKind.TableCell, BodyIndexOf(raw), row.Value, col.Value, 0, raw));
  }

  private static Result<DocxAddress> ParseLayered(string[] segments, string raw)
  {
    bool isHeader = segments[1] == "header";
    bool isFooter = segments[1] == "footer";
    if ((!isHeader && !isFooter) || segments[2][0] != 'p')
    {
      return Fail(raw, null);
    }

    Result<int> section = Index(segments[0][3..]);
    if (!section.IsSuccess)
    {
      return Fail(raw, null);
    }

    Result<int> index = Index(segments[2][1..]);
    if (!index.IsSuccess)
    {
      return Fail(raw, null);
    }

    DocxAddressKind kind = isHeader ? DocxAddressKind.HeaderParagraph : DocxAddressKind.FooterParagraph;
    return Result.Success(new DocxAddress(kind, index.Value, 0, 0, section.Value, raw));
  }

  private static Result<DocxAddress> ParseIndex(string digits, string raw, DocxAddressKind kind)
  {
    Result<int> index = Index(digits);
    return !index.IsSuccess
      ? Fail(raw, null)
      : Result.Success(new DocxAddress(kind, index.Value, 0, 0, 0, raw));
  }

  private static Result<int> Index(string text)
  {
    return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 1
      ? Result.Success(value)
      : Result.Failure<int>(new DomainError(DocxErrorCodes.InvalidAddress, GrammarReminder));
  }

  private static int BodyIndexOf(string raw)
  {
    int dot = raw.IndexOf('.', StringComparison.Ordinal);
    string head = dot < 0 ? raw : raw[..dot];
    _ = int.TryParse(head[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int body);
    return body;
  }

  private static Result<DocxAddress> Fail(string? raw, string? reason)
  {
    string detail = reason is null ? $"address '{raw}'" : $"address '{raw}': {reason}";
    return Result.Failure<DocxAddress>(new DomainError(
        DocxErrorCodes.InvalidAddress, $"Invalid docx address - {detail}. {GrammarReminder}"));
  }

  private const string GrammarReminder =
      "Use p<N> (body paragraph), t<N> (body table), t<N>.r<R>.c<C> (table cell), " +
      "sec<S>.header.p<N> or sec<S>.footer.p<N> (header/footer paragraph); all indices 1-based.";
}
