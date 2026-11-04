using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>The read tool's output format: the bracketed annotation line followed by
///     numbered content lines, with the freshness suffixes and the clamp warning.</summary>
public static class ReadOutputRenderer
{
  public static string Render(string path, int startLine, int endLine, FileRead file, FileCoverage coverage, bool forced)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(coverage);
    bool clamped = file.TotalLines < endLine;
    int last = clamped ? file.TotalLines : endLine;
    int width = last.ToString(CultureInfo.InvariantCulture).Length;
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{Header(startLine, last, path, file, coverage, forced)}");
    AppendBody(sb, file.Lines, startLine, coverage, forced, width);
    AppendTail(sb, clamped, endLine, file);
    return sb.ToString();
  }

  private static string Header(int startLine, int last, string path, FileRead file, FileCoverage coverage, bool forced)
  {
    string tokenSuffix = file.Version is null ? string.Empty : $" | {file.Version.Token}";
    string elisionSuffix = ElisionSuffix(coverage, forced, startLine, last);
    return $"[read {path} lines {startLine}-{last} of {file.TotalLines} total{tokenSuffix}{elisionSuffix}]";
  }

  private static void AppendBody(StringBuilder sb, IReadOnlyList<string> lines, int firstLine,
      FileCoverage coverage, bool forced, int width)
  {
    if (!forced && !coverage.IsEmpty)
    {
      AppendUncoveredLines(sb, lines, firstLine, coverage.Elided, width);
    }
    else
    {
      AppendAllLines(sb, lines, firstLine, width);
    }
  }

  private static void AppendTail(StringBuilder sb, bool clamped, int requestedEndLine, FileRead file)
  {
    if (clamped)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"[warning] endLine {requestedEndLine} exceeded file length ({file.TotalLines}); clamped");
    }
    else
    {
      sb.Length -= Environment.NewLine.Length;  // trim trailing newline
    }
  }

  /// <summary>Appends the lines of the delivered slice that fall OUTSIDE the held
  ///     spans, keeping their absolute line numbers.</summary>
  private static void AppendUncoveredLines(StringBuilder sb, IReadOnlyList<string> lines, int firstLine,
      IReadOnlyList<LineSpan> elided, int width)
  {
    int line = firstLine;
    int spanIdx = 0;
    foreach (string text in lines)
    {
      while (spanIdx < elided.Count && line > elided[spanIdx].End)
      {
        spanIdx++;
      }

      bool held = spanIdx < elided.Count && line >= elided[spanIdx].Start;
      if (!held)
      {
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{line.ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {text}");
      }

      line++;
    }
  }

  private static void AppendAllLines(StringBuilder sb, IReadOnlyList<string> lines, int firstLine, int width)
  {
    foreach ((string? text, int i) in lines.Select((t, i) => (t, i)))
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{(firstLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {text}");
    }
  }

  /// <summary>The annotation's elision suffix: empty when nothing is held, 'forced'
  ///     when the model overrode, a whole-range elision line for full coverage, or a
  ///     span list naming exactly which ranges the model already holds.</summary>
  private static string ElisionSuffix(FileCoverage coverage, bool forced, int startLine, int last)
  {
    if (coverage.IsEmpty)
    {
      return string.Empty;
    }

    if (forced)
    {
      return " | forced";
    }

    bool fullyCovered = coverage.Elided[0].Start <= startLine && coverage.Elided[^1].End >= last;
    return fullyCovered
        ? " | unchanged since your last read; content elided"
        : $" | lines {DescribeSpans(coverage.Elided)} elided (unchanged since your last read)";
  }

  /// <summary>"lines 2-3, 7" — the elided spans, comma-separated.</summary>
  private static string DescribeSpans(IReadOnlyList<LineSpan> spans)
      => string.Join(", ", spans.Select(s => s.ToString()));
}
