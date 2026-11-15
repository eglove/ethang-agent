using System.Globalization;
using System.Text;

namespace eThangAgent.Transport.ACL;

/// <summary>The size budget of one window-facing wire payload. A conversation's tool
///     result travels to the app as a single frame, and a frame over
///     <see cref="TransportFraming.MaxFrameBytes"/> cannot be written at all — so the
///     display payload is held to a quarter of that limit, leaving room for the
///     <c>delta</c>, <c>notice</c> and <c>settled</c> frames a live turn keeps writing
///     beside it.
///     <para>Three bounds, each measured on the emitted string, never on string length:
///     the whole serialized payload (&lt;= <see cref="MaxPayloadBytes"/>), the text it
///     carries across content, summary and title (&lt;= <see cref="MaxTextBytes"/>), and
///     a tool call's argument string (&lt;= <see cref="MaxArgumentChars"/>).</para>
///     <para>A value within its bound is returned as the very same string: the budget
///     never rewrites a payload that fits, so the wire stays byte-identical for every
///     payload that fitted before this budget existed.</para></summary>
public static class FramedPayloadBudget
{
  /// <summary>Largest UTF-8 payload a <c>tool</c> event may carry; a quarter of
  ///     <see cref="TransportFraming.MaxFrameBytes"/>.</summary>
  public const int MaxPayloadBytes = 1_048_576;

  /// <summary>Largest UTF-8 size of the text fields (content, summary and title) a
  ///     <c>tool</c> payload carries together.</summary>
  public const int MaxTextBytes = 262_144;

  /// <summary>Largest number of characters a <c>tool</c> call's argument string may
  ///     carry.</summary>
  public const int MaxArgumentChars = 4_096;

  /// <summary>The head of a value that was shortened: the longest prefix of
  ///     <paramref name="text"/> whose UTF-8 encoding is at most
  ///     <paramref name="maxBytes"/> bytes long, cut at a code point boundary so a
  ///     surrogate pair or a multi-byte sequence is never split. The value itself when
  ///     it already fits.</summary>
  /// <param name="text">The text to shorten.</param>
  /// <param name="maxBytes">The byte budget; zero yields the empty string.</param>
  public static string TruncateText(string text, int maxBytes)
  {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
    if (Utf8ByteCount(text) <= maxBytes)
    {
      return text;
    }

    int used = 0;
    int index = 0;
    while (index < text.Length && used + Measure(text, index).Bytes <= maxBytes)
    {
      (int codeUnits, int bytes) = Measure(text, index);
      used += bytes;
      index += codeUnits;
    }

    return text[..index];
  }

  /// <summary>How one code point at <paramref name="index"/> occupies both a string and a
  ///     UTF-8 encoding: the number of UTF-16 code units it takes, and the number of bytes
  ///     it encodes to. An unpaired surrogate has no UTF-8 form and counts as U+FFFD's three
  ///     bytes, which is what <see cref="Encoding.UTF8"/> reports for it.</summary>
  /// <param name="text">The text the code point belongs to.</param>
  /// <param name="index">The code point's first code unit.</param>
  private static (int CodeUnits, int Bytes) Measure(string text, int index)
  {
    char current = text[index];
    if (char.IsHighSurrogate(current) && index + 1 < text.Length
        && char.IsLowSurrogate(text[index + 1]))
    {
      return (2, 4);
    }

    if (char.IsSurrogate(current) || current >= 0x800)
    {
      return (1, 3);
    }

    return (1, current < 0x80 ? 1 : 2);
  }

  /// <summary>The marker that follows a shortened text field, naming the size it had
  ///     and the size it kept, both in UTF-8 bytes.</summary>
  /// <param name="originalBytes">The field's UTF-8 size before the cut.</param>
  /// <param name="keptBytes">The field's UTF-8 size after the cut.</param>
  public static string TruncatedMarker(int originalBytes, int keptBytes) =>
      " [... truncated: " + keptBytes.ToString(CultureInfo.InvariantCulture)
      + " of " + originalBytes.ToString(CultureInfo.InvariantCulture)
      + " UTF-8 bytes shown ...]";

  /// <summary>The head of a tool call's argument string: the longest prefix of at most
  ///     <paramref name="maxChars"/> characters, backed up one character when the cut
  ///     would land between a surrogate pair. The value itself when it already fits.</summary>
  /// <param name="arguments">The call's argument string.</param>
  /// <param name="maxChars">The character budget; zero yields the empty string.</param>
  public static string TruncateArguments(string arguments, int maxChars)
  {
    ArgumentNullException.ThrowIfNull(arguments);
    ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
    return arguments.Length <= maxChars ? arguments : arguments[..CutAtCodePoint(arguments, maxChars)];
  }

  /// <summary>The marker that follows a shortened argument string, naming the length it
  ///     had and the length it kept, both in characters.</summary>
  /// <param name="originalChars">The argument string's length before the cut.</param>
  /// <param name="keptChars">The argument string's length after the cut.</param>
  public static string TruncatedArgumentMarker(int originalChars, int keptChars) =>
      " [... truncated: " + keptChars.ToString(CultureInfo.InvariantCulture)
      + " of " + originalChars.ToString(CultureInfo.InvariantCulture)
      + " characters shown ...]";

  /// <summary>The line a tool result's summary leads with when its images could not fit
  ///     the payload: the count that was dropped, and that they are dropped for size
  ///     alone.</summary>
  /// <param name="dropped">How many images the payload could not carry.</param>
  public static string ImageNotice(int dropped)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(dropped);
    return dropped.ToString(CultureInfo.InvariantCulture)
        + (dropped == 1
            ? " image not shown: too large to preview here"
            : " images not shown: too large to preview here");
  }

  /// <summary>A summary with the image notice leading it, so a dropped image is stated
  ///     where the card's header line renders. Unchanged for none dropped.</summary>
  /// <param name="summary">The tool's own summary line.</param>
  /// <param name="dropped">How many images the payload could not carry.</param>
  public static string WithImageNotice(string summary, int dropped)
  {
    ArgumentNullException.ThrowIfNull(summary);
    if (dropped == 0)
    {
      return summary;
    }

    string notice = ImageNotice(dropped);
    return summary.Length == 0 ? notice : notice + Environment.NewLine + summary;
  }

  /// <summary>The summary to carry once <paramref name="dropped"/> images were dropped:
  ///     <see cref="ImageNotice"/> leading, then as much of
  ///     <paramref name="summary"/> as still fits
  ///     <see cref="MaxTextBytes"/> beside <paramref name="content"/> and
  ///     <paramref name="title"/>, with a marker naming what the summary lost. The notice
  ///     itself always survives, so a drop is never silent.</summary>
  /// <param name="content">The result's framed content.</param>
  /// <param name="summary">The result's framed summary line.</param>
  /// <param name="title">The card's framed title, null when the tool supplied none.</param>
  /// <param name="dropped">How many images the payload could not carry.</param>
  public static string WithImageNotice(
      string content, string summary, string? title, int dropped)
  {
    ArgumentNullException.ThrowIfNull(content);
    ArgumentNullException.ThrowIfNull(summary);
    if (dropped == 0)
    {
      return summary;
    }

    string notice = ImageNotice(dropped);
    int room = MaxTextBytes - Utf8ByteCount(content) - Utf8ByteCount(title ?? string.Empty)
        - Utf8ByteCount(notice) - Environment.NewLine.Length;
    return room <= 0 || summary.Length == 0
        ? notice
        : notice + Environment.NewLine + Fit(summary, room);
  }

  /// <summary>Content, summary and title shortened together to
  ///     <see cref="MaxTextBytes"/> UTF-8 bytes: content first, then summary, then
  ///     title, each cut only when the text it carries would otherwise overrun the
  ///     budget. A field that fits is returned as the very same string, and a text set
  ///     that fits is returned unchanged, so a payload that was within budget keeps its
  ///     exact bytes.</summary>
  /// <param name="content">The result's full content.</param>
  /// <param name="summary">The result's summary line.</param>
  /// <param name="title">The card's rich title, null when the tool supplied none.</param>
  public static (string Content, string Summary, string? Title) FrameText(
      string content, string summary, string? title)
  {
    ArgumentNullException.ThrowIfNull(content);
    ArgumentNullException.ThrowIfNull(summary);
    int budget = MaxTextBytes;
    while (true)
    {
      (string fittedContent, string fittedSummary, string fittedTitle) =
          Allocate(content, summary, title ?? string.Empty, budget);
      int total = Utf8ByteCount(fittedContent) + Utf8ByteCount(fittedSummary)
          + Utf8ByteCount(fittedTitle);
      if (total <= MaxTextBytes)
      {
        return (fittedContent, fittedSummary, title is null ? null : fittedTitle);
      }

      // Each pass shortens the allocation by the overshoot, which is at least one byte,
      // and a zero allocation leaves only the markers — far inside the budget — so the
      // loop always reaches it.
      budget -= total - MaxTextBytes;
      if (budget < 0)
      {
        budget = 0;
      }
    }
  }

  private static (string Content, string Summary, string Title) Allocate(
      string content, string summary, string title, int budget)
  {
    if (Utf8ByteCount(content) + Utf8ByteCount(summary) + Utf8ByteCount(title) <= budget)
    {
      return (content, summary, title);
    }

    int summaryBytes = Utf8ByteCount(summary);
    int titleBytes = Utf8ByteCount(title);
    string fittedContent = Fit(content, budget - summaryBytes - titleBytes);
    int contentBytes = Utf8ByteCount(fittedContent);
    string fittedSummary = Fit(summary, budget - contentBytes - titleBytes);
    int fittedSummaryBytes = Utf8ByteCount(fittedSummary);
    string fittedTitle = Fit(title, budget - contentBytes - fittedSummaryBytes);
    return (fittedContent, fittedSummary, fittedTitle);
  }

  private static string Fit(string value, int available)
  {
    int bytes = Utf8ByteCount(value);
    if (bytes <= available)
    {
      return value;
    }

    int room = available - Utf8ByteCount(TruncatedMarker(bytes, 0));
    string kept = room <= 0 ? string.Empty : TruncateText(value, room);
    return kept + TruncatedMarker(bytes, Utf8ByteCount(kept));
  }

  private static int CutAtCodePoint(string value, int maxChars)
  {
    int end = Math.Min(maxChars, value.Length);
    return end > 0 && char.IsHighSurrogate(value[end - 1]) ? end - 1 : end;
  }

  private static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);
}
