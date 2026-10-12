using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One page-range segment from the pinned grammar: a 1-based <see cref="Start"/>
///     and an optional inclusive <see cref="End"/>. A null <see cref="End"/> means no
///     explicit end was written — a bare page 'N' (a single page) or the open tail 'N-'
///     (from N to the document's last page, resolved against the total page count in the
///     ACL, never in the domain parser).</summary>
public sealed record PdfPageSegment(int Start, int? End);

/// <summary>The pinned page-range grammar (task 1): 1-based, comma-separated segments
///     'N', 'N-M', and the open tail 'N-'. Segments must not overlap; the open tail must
///     be the LAST segment (it claims every page from its start to the end of the
///     document, so nothing may follow it). Whitespace around segments is tolerated.
///     Every violation is a typed InvalidPageRange failure naming the offending segment
///     — never an exception, never a clamp.</summary>
public sealed record PdfPageRange(IReadOnlyList<PdfPageSegment> Segments)
{
  /// <summary>Parses page-range text against the pinned grammar. Empty text, an empty
  ///     segment, a non-numeric token, a zero-or-negative start, an end below its start,
  ///     an overlapping segment, or any segment after the open tail is a typed
  ///     InvalidPageRange failure.</summary>
  public static Result<PdfPageRange> Parse(string? text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return Fail(text, "the range is empty");
    }

    string[] parts = text.Split(',');
    List<PdfPageSegment> segments = new(parts.Length);
    HashSet<int> claimed = [];
    bool sawOpenTail = false;

    foreach (string part in parts)
    {
      string segment = part.Trim();
      if (segment.Length == 0)
      {
        return Fail(text, "an empty segment");
      }

      if (sawOpenTail)
      {
        return Fail(text, $"segment '{segment}' follows the open tail, which must be last");
      }

      Result<(int Start, int? End, bool OpenTail)> parsed = ParseSegment(segment);
      if (!parsed.IsSuccess)
      {
        return Fail(text, parsed.Error.Message);
      }

      (int start, int? end, bool openTail) = parsed.Value;
      if (openTail)
      {
        sawOpenTail = true;
        segments.Add(new PdfPageSegment(start, null));
        continue;
      }

      int last = end ?? start;
      for (int page = start; page <= last; page++)
      {
        if (!claimed.Add(page))
        {
          return Fail(text, $"segment '{segment}' overlaps an earlier segment at page {page}");
        }
      }

      segments.Add(new PdfPageSegment(start, end));
    }

    return Result.Success(new PdfPageRange(segments));
  }

  private static Result<(int Start, int? End, bool OpenTail)> ParseSegment(string segment)
  {
    int dash = segment.IndexOf('-', StringComparison.Ordinal);
    if (dash < 0)
    {
      Result<int> single = ParsePage(segment);
      return single.IsSuccess
        ? Result.Success((single.Value, (int?)null, false))
        : SegmentFail($"segment '{segment}' is not a page number");
    }

    string startText = segment[..dash];
    string endText = segment[(dash + 1)..];
    if (startText.Length == 0)
    {
      return SegmentFail($"segment '{segment}' has no start page");
    }

    Result<int> start = ParsePage(startText);
    if (!start.IsSuccess)
    {
      return SegmentFail($"segment '{segment}' has an invalid start page");
    }

    if (endText.Length == 0)
    {
      return Result.Success((start.Value, (int?)null, true));
    }

    Result<int> end = ParsePage(endText);
    return !end.IsSuccess
      ? SegmentFail($"segment '{segment}' has an invalid end page")
      : OrderChecked(segment, start.Value, end.Value);
  }

  private static Result<(int Start, int? End, bool OpenTail)> OrderChecked(string segment, int start, int end) =>
      end < start
        ? SegmentFail($"segment '{segment}' ends before it starts")
        : Result.Success((start, (int?)end, false));

  private static Result<int> ParsePage(string text)
  {
    return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 1
      ? Result.Success(value)
      : Result.Failure<int>(new DomainError(PdfErrorCodes.InvalidPageRange, "invalid page number"));
  }

  private static Result<(int Start, int? End, bool OpenTail)> SegmentFail(string reason) =>
      Result.Failure<(int, int?, bool)>(new DomainError(PdfErrorCodes.InvalidPageRange, reason));

  private static Result<PdfPageRange> Fail(string? text, string reason)
  {
    return Result.Failure<PdfPageRange>(new DomainError(
        PdfErrorCodes.InvalidPageRange, $"Invalid page range '{text}' - {reason}. {GrammarReminder}"));
  }

  private const string GrammarReminder =
      "Use 1-based, comma-separated segments: N, N-M, or the open tail N- (last only).";
}
