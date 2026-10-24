using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decision (CA1849): file operations here are small and local; sync APIs inside
// async tool wrappers keep the code simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types
namespace eThangAgent.FileSystem.ACL;

public sealed class DirectFileSystemAccess(ILineEndingPolicy lineEndings) : IFileSystemAccess, IFileWriteAccess, IFileEditAccess, IDisposable
{
  private readonly ILineEndingPolicy _lineEndings =
      lineEndings ?? throw new ArgumentNullException(nameof(lineEndings));
  public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
  {
    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<FileRead>(new DomainError("FileNotFound", $"File not found: {path}")));
    }

    // Stat FIRST, then read: the version pair must describe the file as it stood
    // when the content was captured. A write racing between the stat and the read
    // makes the token OLDER than the content — the next read over-delivers (never
    // wrongly elides), the safe direction. A stat taken after the read could see a
    // newer write than the delivered lines and overstate freshness.
    FileInfo stat = new(path);
    FileVersion version = new(stat.Length, stat.LastWriteTimeUtc);

    List<string> allLines = [];
    using StreamReader sr = new(path, Encoding.UTF8);
    while (sr.ReadLine() is { } line)
    {
      allLines.Add(line);
    }

    int start = Math.Max(1, startLine) - 1;
    int end = Math.Min(endLine, allLines.Count);
    List<string> slice = [.. allLines.Skip(start).Take(end - start)];
    return Task.FromResult(Result.Success(new FileRead(slice, end, allLines.Count, version)));
  }

  public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
  {
    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<byte[]>(new DomainError("FileNotFound", $"File not found: {path}")));
    }

    try
    {
      return Task.FromResult(Result.Success(File.ReadAllBytes(path)));
    }
    catch (IOException ex)
    {
      return Task.FromResult(Result.Failure<byte[]>(new DomainError("ReadFailed", ex.Message)));
    }
  }

  public Task<Result<FileWriteOutcome>> WriteFileAsync(
      string path, string content, bool overwrite, CancellationToken ct = default)
  {
    if (File.Exists(path) && !overwrite)
    {
      return Task.FromResult(Result.Failure<FileWriteOutcome>(
          new DomainError("FileExists", $"File already exists: {path} (overwrite not requested).")));
    }

    string? dir = Path.GetDirectoryName(path);
    if (!Directory.Exists(dir))
    {
      return Task.FromResult(Result.Failure<FileWriteOutcome>(
          new DomainError("DirectoryNotFound",
              $"Parent directory does not exist: '{dir}'. Create it first (e.g. Directory.CreateDirectory), then retry the write.")));
    }

    bool created = !File.Exists(path);
    LineEnding normalizedTo = ResolveConvention(path);
    File.WriteAllText(path, LineEndings.Normalize(content, normalizedTo), new UTF8Encoding(false));
    return Task.FromResult(Result.Success(
        new FileWriteOutcome(created, new FileInfo(path).Length, normalizedTo)));
  }

  public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(
      string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(lines);
    return WriteFileAsync(path, JoinLines(path, lines), overwrite, ct);
  }

  /// <summary>Joins one-per-line content with the convention this file should land in,
  ///     so 'lines' mode never hardcodes LF.</summary>
  private string JoinLines(string path, IReadOnlyList<string> lines) =>
      string.Join(LineEndings.Terminator(ResolveConvention(path)), lines);

  /// <summary>The convention a write to this path lands in: the existing file's own
  ///     dominant convention when it has one (a tool never rewrites a file's house
  ///     style), else the project convention the policy reports.</summary>
  private LineEnding ResolveConvention(string path) =>
      File.Exists(path) && ReadTextOrNull(path) is { } existing
          ? LineEndings.DetectDominant(existing) ?? _lineEndings.Resolve(path)
          : _lineEndings.Resolve(path);

  private static string? ReadTextOrNull(string path)
  {
    try
    {
      return File.ReadAllText(path, Encoding.UTF8);
    }
    catch (IOException)
    {
      return null; // unreadable existing file: fall back to the project convention
    }
    catch (UnauthorizedAccessException)
    {
      return null;
    }
  }

  public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
      string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(bytes);

    if (File.Exists(path) && !overwrite)
    {
      return Task.FromResult(Result.Failure<FileWriteOutcome>(
          new DomainError("FileExists", $"File already exists: {path} (overwrite not requested).")));
    }

    string? dir = Path.GetDirectoryName(path);
    if (!Directory.Exists(dir))
    {
      return Task.FromResult(Result.Failure<FileWriteOutcome>(
          new DomainError("DirectoryNotFound",
              $"Parent directory does not exist: '{dir}'. Create it first (e.g. Directory.CreateDirectory), then retry the write.")));
    }

    bool created = !File.Exists(path);
    File.WriteAllBytes(path, bytes);
    // Binary content has no line endings to normalize; the reported convention is the
    // file's own where one can be read, which for an artifact like an image is simply
    // not meaningful but keeps the outcome shape uniform.
    return Task.FromResult(Result.Success(
        new FileWriteOutcome(created, new FileInfo(path).Length, LineEnding.Lf)));
  }

  public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
      string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(oldText);
    ArgumentNullException.ThrowIfNull(newText);
    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("FileNotFound", $"File not found: {path}")));
    }

    string? text = ReadAllTextRejectBinary(path);
    if (text is null)
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("BinaryFile", $"File appears to be binary (NUL byte found): {path}.")));
    }

    // Matching tolerates line-ending differences: an anchor written with LF must
    // match CRLF content and vice versa (files arrive under many conventions). The
    // match runs on normalized views; the splice maps matches back to original
    // offsets so the untouched parts of the file keep their exact bytes.
    string matchText = NormalizeNewlines(text);
    string matchAnchor = NormalizeNewlines(oldText);

    int count = 0;
    int idx = matchText.IndexOf(matchAnchor, StringComparison.Ordinal);
    while (idx >= 0)
    {
      count++;
      idx = matchText.IndexOf(matchAnchor, idx + matchAnchor.Length, StringComparison.Ordinal);
    }

    if (count == 0)
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("AnchorNotFound", AnchorMissMessage(path, oldText, matchText))));
    }

    int target = occurrences ?? count;
    if (occurrences is not null && count != occurrences.Value)
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("OccurrenceMismatch", $"Anchor occurs {count} time(s) but {occurrences} replacement(s) were requested.")));
    }

    // The replacement is rewritten into the FILE's own convention before splicing, so
    // an anchor edit can never introduce a foreign terminator (issue #251). The
    // tolerant matching above is untouched: it runs on normalized views and the offset
    // map still splices untouched bytes verbatim.
    LineEnding convention = LineEndings.DetectDominant(text) ?? _lineEndings.Resolve(path);
    string normalizedNew = LineEndings.Normalize(newText, convention);

    // Normalized and original offsets line up only when no CR was stripped; build
    // a normalized-offset -> original-offset map for the general case.
    int[] map = BuildOffsetMap(text, matchText);
    StringBuilder sb = new();
    int matchPos = 0;
    int originalPos = 0;
    int done = 0;
    while (done < target)
    {
      int normalizedIdx = matchText.IndexOf(matchAnchor, matchPos, StringComparison.Ordinal);
      int originalStart = map[normalizedIdx];
      int originalEnd = map[normalizedIdx + matchAnchor.Length];
      _ = sb.Append(text.AsSpan(originalPos, originalStart - originalPos));
      _ = sb.Append(normalizedNew);
      originalPos = originalEnd;
      matchPos = normalizedIdx + matchAnchor.Length;
      done++;
    }

    _ = sb.Append(text.AsSpan(originalPos));
    string result = sb.ToString();
    File.WriteAllText(path, result, new UTF8Encoding(false));
    int lineCount = SplitLinesLikeReadLine(result).Count;
    return Task.FromResult(Result.Success(new ReplaceOutcome(done, lineCount, convention)));
  }

  public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
      string path, int startLine, int endLine, string newText, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(newText);
    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("FileNotFound", $"File not found: {path}")));
    }

    string? text = ReadAllTextRejectBinary(path);
    if (text is null)
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("BinaryFile", $"File appears to be binary (NUL byte found): {path}.")));
    }

    List<(string Content, string Terminator)> lines = SplitLinesLikeReadLine(text);
    if (startLine < 1 || startLine > lines.Count)
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("LineRangeBeyondEof", $"'startLine' {startLine} exceeds file length ({lines.Count} lines).")));
    }

    if (endLine > lines.Count)
    {
      return Task.FromResult(Result.Failure<ReplaceOutcome>(
          new DomainError("LineRangeBeyondEof", $"'endLine' {endLine} exceeds file length ({lines.Count} lines).")));
    }

    // The replacement is rewritten into the FILE's own convention first, so range mode
    // cannot introduce a foreign terminator either (issue #251).
    LineEnding convention = LineEndings.DetectDominant(text) ?? _lineEndings.Resolve(path);
    string normalizedNew = LineEndings.Normalize(newText, convention);

    string result = SpliceRange(lines, startLine, endLine, normalizedNew);
    File.WriteAllText(path, result, new UTF8Encoding(false));
    return Task.FromResult(Result.Success(
        new ReplaceOutcome(endLine - startLine + 1, SplitLinesLikeReadLine(result).Count, convention)));
  }


  /// <summary>Applies every hunk atomically over ONE file read (issue #252). Hunks match
  ///     against the file AS IT STANDS — never against another hunk's output — so they
  ///     must name disjoint regions; touching matches are refused as <c>HunksOverlap</c>
  ///     instead of being applied in some order. Nothing is written unless every hunk
  ///     matched, so the file is byte-identical after any failure.</summary>
  public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
      string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(hunks);
    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<HunkReplaceOutcome>(
          new DomainError("FileNotFound", $"File not found: {path}")));
    }

    string? text = ReadAllTextRejectBinary(path);
    if (text is null)
    {
      return Task.FromResult(Result.Failure<HunkReplaceOutcome>(
          new DomainError("BinaryFile", $"File appears to be binary (NUL byte found): {path}.")));
    }

    string matchText = NormalizeNewlines(text);
    LineEnding convention = LineEndings.DetectDominant(text) ?? _lineEndings.Resolve(path);
    Result<List<HunkMatch>> matched = MatchHunks(path, hunks, matchText, convention);
    if (!matched.IsSuccess)
    {
      return Task.FromResult(Result.Failure<HunkReplaceOutcome>(matched.Error));
    }

    List<HunkMatch> matches = [.. matched.Value.OrderBy(m => m.Start)];
    DomainError? overlap = OverlapError(matches);
    if (overlap is not null)
    {
      return Task.FromResult(Result.Failure<HunkReplaceOutcome>(overlap));
    }

    int[] perHunk = new int[hunks.Count];
    foreach (HunkMatch match in matches)
    {
      perHunk[match.Hunk]++;
    }

    int[] map = BuildOffsetMap(text, matchText);
    StringBuilder sb = new();
    int originalPos = 0;
    foreach (HunkMatch match in matches)
    {
      _ = sb.Append(text.AsSpan(originalPos, map[match.Start] - originalPos));
      _ = sb.Append(match.Replacement);
      originalPos = map[match.Start + match.Length];
    }

    _ = sb.Append(text.AsSpan(originalPos));
    string result = sb.ToString();
    File.WriteAllText(path, result, new UTF8Encoding(false));
    return Task.FromResult(Result.Success(new HunkReplaceOutcome(
        perHunk, SplitLinesLikeReadLine(result).Count, convention)));
  }

  /// <summary>Finds every hunk's matches against the untouched file text, turning each
  ///     failure into 'hunk N: ...' so one correction lands on the right hunk. A miss
  ///     carries the full anchor-miss hint (nearest region, gutter block, ready anchor).</summary>
  private static Result<List<HunkMatch>> MatchHunks(
      string path, IReadOnlyList<EditHunk> hunks, string matchText, LineEnding convention)
  {
    List<HunkMatch> matches = [];
    for (int i = 0; i < hunks.Count; i++)
    {
      EditHunk hunk = hunks[i];
      string anchor = NormalizeNewlines(hunk.Old);
      string where = $"hunk {i + 1}";
      List<int> positions = PositionsOf(matchText, anchor);
      if (positions.Count == 0)
      {
        return Result.Failure<List<HunkMatch>>(new DomainError("AnchorNotFound",
            $"{where}: {AnchorMissMessage(path, hunk.Old, matchText)}"));
      }

      if (!hunk.All && positions.Count != hunk.Occurrences)
      {
        return Result.Failure<List<HunkMatch>>(new DomainError("OccurrenceMismatch",
            $"{where}: Anchor occurs {positions.Count} time(s) but {hunk.Occurrences} replacement(s) were requested."));
      }

      string replacement = LineEndings.Normalize(hunk.Replacement, convention);
      matches.AddRange(positions.Select(position => new HunkMatch(i, position, anchor.Length, replacement)));
    }

    return Result.Success(matches);
  }

  /// <summary>Every start offset of <paramref name="anchor"/> in the normalized text.</summary>
  private static List<int> PositionsOf(string matchText, string anchor)
  {
    List<int> positions = [];
    int idx = matchText.IndexOf(anchor, StringComparison.Ordinal);
    while (idx >= 0)
    {
      positions.Add(idx);
      idx = matchText.IndexOf(anchor, idx + anchor.Length, StringComparison.Ordinal);
    }

    return positions;
  }

  /// <summary>Two hunks whose matches touch the same text are a contradictory request —
  ///     applying the second to the first's output is a different edit than either alone —
  ///     so the whole call is refused, naming the later hunk.</summary>
  private static DomainError? OverlapError(List<HunkMatch> sorted)
  {
    for (int i = 1; i < sorted.Count; i++)
    {
      HunkMatch previous = sorted[i - 1];
      HunkMatch current = sorted[i];
      if (current.Start < previous.Start + previous.Length)
      {
        return new DomainError("HunksOverlap",
            $"hunk {current.Hunk + 1}: its match overlaps hunk {previous.Hunk + 1}'s. " +
            "Hunks are matched against the file as it stands and must name disjoint regions; " +
            "combine them into one hunk.");
      }
    }

    return null;
  }

  /// <summary>One hunk match: which hunk it belongs to, where it starts and how long it
  ///     is in NORMALIZED coordinates, and its replacement already rewritten into the
  ///     file's line-ending convention.</summary>
  private sealed record HunkMatch(int Hunk, int Start, int Length, string Replacement);

  /// <summary>Replaces lines startLine..endLine with already-normalized text. Untouched
  ///     lines keep their exact content AND terminator bytes; the replacement carries the
  ///     removed range's trailing terminator when one is owed to the following line (or to
  ///     the file's trailing-newline convention), so a content-only edit never changes the
  ///     file's line-ending style.</summary>
  private static string SpliceRange(List<(string Content, string Terminator)> lines,
      int startLine, int endLine, string normalizedNew)
  {
    StringBuilder sb = new();
    foreach ((string content, string terminator) in lines.Take(startLine - 1))
    {
      _ = sb.Append(content).Append(terminator);
    }

    string lastTerminator = lines[endLine - 1].Terminator;
    bool owesTerminator = normalizedNew.Length > 0
        && !normalizedNew.EndsWith('\n')
        && (endLine < lines.Count || lastTerminator.Length > 0);
    _ = owesTerminator ? sb.Append(normalizedNew).Append(lastTerminator) : sb.Append(normalizedNew);

    foreach ((string content, string terminator) in lines.Skip(endLine))
    {
      _ = sb.Append(content).Append(terminator);
    }

    return sb.ToString();
  }

  /// <summary>Splits text into (content, terminator) pairs exactly as
  ///     StreamReader.ReadLine delimits: "\r\n", "\r", or "\n" ends a line, and the
  ///     final line carries no terminator when the text does not end with one.
  ///     Terminators are preserved so a splice leaves untouched bytes identical.</summary>
  private static List<(string Content, string Terminator)> SplitLinesLikeReadLine(string text)
  {
    List<(string Content, string Terminator)> lines = [];
    int lineStart = 0;
    int i = 0;
    while (i < text.Length)
    {
      char c = text[i];
      if (c is not ('\r' or '\n'))
      {
        i++;
        continue;
      }

      int terminatorLength = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
      lines.Add((text[lineStart..i], text.Substring(i, terminatorLength)));
      i += terminatorLength;
      lineStart = i;
    }

    if (lineStart < text.Length)
    {
      lines.Add((text[lineStart..], string.Empty));
    }

    return lines;
  }

  /// <summary>Minimum similarity (0..1) between an anchor's first line and a file
  ///     line for the miss message to name that line as the nearest match. Below it
  ///     the file holds nothing close, and the bare miss message is the honest answer.</summary>
  private const double NearestMatchThreshold = 0.6;

  /// <summary>Context lines shown around the nearest match in the miss hint.</summary>
  private const int NearestMatchContextLines = 2;

  /// <summary>The anchor-miss failure message: the original bare miss line, plus —
  ///     when the file contains a line similar to the anchor's first line — the
  ///     nearest matching region quoted with exact line numbers and exact content
  ///     in the read tool's gutter format, then that region's own text emitted as a
  ///     ready-to-use 'old' so the retry is a paste and never a re-read (issue #252),
  ///     and a stated mismatch kind when the difference is only whitespace (issue #259).</summary>
  private static string AnchorMissMessage(string path, string anchor, string normalizedFile)
  {
    string message = $"Anchor text (length {anchor.Length}) not found in {path}.";
    string normalizedAnchor = NormalizeNewlines(anchor);
    string[] anchorLines = normalizedAnchor.Split('\n');
    string anchorHead = anchorLines[0].Trim();
    if (anchorHead.Length == 0)
    {
      return message;
    }

    List<(string Content, string Terminator)> lines = SplitLinesLikeReadLine(normalizedFile);
    int best = -1;
    double bestScore = 0;
    for (int i = 0; i < lines.Count; i++)
    {
      double score = Similarity(lines[i].Content.Trim(), anchorHead);
      if (score > bestScore)
      {
        bestScore = score;
        best = i;
      }
    }

    if (best < 0 || bestScore < NearestMatchThreshold)
    {
      return message; // nothing close in the file: the bare miss is the honest answer
    }

    int first = Math.Max(0, best - NearestMatchContextLines);
    int last = Math.Min(lines.Count - 1, best + NearestMatchContextLines);
    int width = (last + 1).ToString(CultureInfo.InvariantCulture).Length;
    StringBuilder hint = new();
    _ = hint.Append(message);
    _ = hint.AppendLine();
    _ = hint.Append(CultureInfo.InvariantCulture,
        $"Nearest match at line {best + 1}");
    _ = hint.Append(SimilarityNote(normalizedAnchor, lines, best, bestScore));
    _ = hint.AppendLine("; exact content:");
    for (int i = first; i <= last; i++)
    {
      _ = hint.AppendLine();
      _ = hint.Append(CultureInfo.InvariantCulture,
          $"{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {lines[i].Content}");
    }

    // The model's next 'old' is the file's own text at that region, verbatim:
    // copying this block costs nothing and the retry lands on the first try.
    _ = hint.AppendLine();
    _ = hint.Append(ReadyAnchorBlock(lines, best, anchorLines.Length));
    return hint.ToString();
  }

  /// <summary>How the anchor differs, stated only when the number would mislead. The
  ///     similarity is computed over TRIMMED lines, so a whitespace-only difference
  ///     scores 100% and would read as a contradiction next to a failure (issue #259):
  ///     that case names the kind instead, and every other case keeps the figure.</summary>
  private static string SimilarityNote(
      string normalizedAnchor, List<(string Content, string Terminator)> lines, int best, double score)
  {
    string[] anchorLines = normalizedAnchor.Split('\n');
    // Whitespace-only means the two spans carry the SAME characters once every kind of
    // whitespace is removed — indentation depth, an interior double space, trailing
    // spaces, a CRLF/LF mismatch. That is the only case where a high score and a
    // failure would contradict each other, so it names the kind instead of the figure.
    bool whitespaceOnly = CompactedMatches(anchorLines, lines, best)
      && !RawMatches(anchorLines, lines, best);
    return whitespaceOnly
      ? " (similarity not stated: your anchor differs from this region only in whitespace " +
        "— indentation, trailing spaces, or line endings)"
      : string.Create(CultureInfo.InvariantCulture, $" (similarity {score:P0})");
  }

  /// <summary>True when the anchor and the file's region at that span carry the same
  ///     characters once ALL whitespace is removed — the honest test for "differs only
  ///     in whitespace".</summary>
  private static bool CompactedMatches(
      string[] anchorLines, List<(string Content, string Terminator)> lines, int best) =>
      lines.Count - best >= anchorLines.Length
      && string.Equals(
          Compact(string.Join('\n', anchorLines)),
          Compact(string.Join('\n', lines.Skip(best).Take(anchorLines.Length).Select(l => l.Content))),
          StringComparison.Ordinal);

  private static string Compact(string text) =>
      string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

  /// <summary>True when the anchor's lines equal the file's lines verbatim.</summary>
  private static bool RawMatches(
      string[] anchorLines, List<(string Content, string Terminator)> lines, int best) =>
      lines.Count - best >= anchorLines.Length
      && anchorLines.SequenceEqual(lines.Skip(best).Take(anchorLines.Length).Select(l => l.Content), StringComparer.Ordinal);

  /// <summary>The ready-to-use anchor: the file's own lines at the nearest region,
  ///     verbatim, in the same line count as the anchor so the correction is a
  ///     like-for-like replacement rather than a re-read.</summary>
  private static string ReadyAnchorBlock(
      List<(string Content, string Terminator)> lines, int best, int anchorLineCount)
  {
    // Prefer the anchor's own line count (the caller usually wanted that span); fall
    // back to the single matched line when the file ends before the span does.
    int wanted = Math.Min(anchorLineCount, lines.Count - best);
    int count = wanted >= 1 ? wanted : 1;
    // Joined with an explicit '\n' rather than AppendLine: the block is meant to be
    // copied verbatim as the next 'old', so it must not pick up the platform's
    // terminator (AppendLine would leave a stray '\r' on every line).
    string[] block =
    [
        "Emitting the text that WOULD match as a ready-to-use 'old' (copy it verbatim: the retry is one call, no re-read):",
        .. lines.Skip(best).Take(count).Select(l => l.Content),
    ];
    return string.Join('\n', block);
  }

  /// <summary>Similarity between two trimmed lines (0..1): 1 for equal text, else a
  ///     Levenshtein-based ratio. Deliberately simple — the hint names a candidate,
  ///     the model reads the quoted exact content and decides.</summary>
  private static double Similarity(string a, string b)
  {
    if (a.Length == 0 && b.Length == 0)
    {
      return 1;
    }

    if (a.Length == 0 || b.Length == 0)
    {
      return 0;
    }

    if (string.Equals(a, b, StringComparison.Ordinal))
    {
      return 1;
    }

    int distance = LevenshteinDistance(a, b);
    return 1.0 - ((double)distance / Math.Max(a.Length, b.Length));
  }

  /// <summary>Plain Levenshtein distance over two short strings (lines, never whole
  ///     files — the caller compares line-by-line, so the O(n·m) table stays small).</summary>
  private static int LevenshteinDistance(string a, string b)
  {
    int[] previous = new int[b.Length + 1];
    int[] current = new int[b.Length + 1];
    for (int j = 0; j <= b.Length; j++)
    {
      previous[j] = j;
    }

    for (int i = 1; i <= a.Length; i++)
    {
      current[0] = i;
      for (int j = 1; j <= b.Length; j++)
      {
        int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
        int deletion = previous[j] + 1;
        int insertion = current[j - 1] + 1;
        current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
      }

      (previous, current) = (current, previous);
    }

    return previous[b.Length];
  }

  /// <summary>Normalizes CRLF and bare CR to LF for tolerant anchor matching.</summary>
  private static string NormalizeNewlines(string text)
      => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

  /// <summary>Maps every offset in the normalized text back to the corresponding
  ///     offset in the original (map.Length == normalized.Length + 1, so the end
  ///     offset of the last character is addressable). Only CRLF/CR-to-LF collapses
  ///     shifts offsets; every other character maps 1:1.</summary>
  private static int[] BuildOffsetMap(string original, string normalized)
  {
    int[] map = new int[normalized.Length + 1];
    int originalIdx = 0;
    for (int i = 0; i < normalized.Length; i++)
    {
      map[i] = originalIdx;
      if (normalized[i] == '\n' && originalIdx < original.Length && original[originalIdx] == '\r')
      {
        originalIdx += originalIdx + 1 < original.Length && original[originalIdx + 1] == '\n' ? 2 : 1;
      }
      else
      {
        originalIdx++;
      }
    }

    map[normalized.Length] = originalIdx;
    return map;
  }

  private static string? ReadAllTextRejectBinary(string path)
  {
    byte[] buffer = new byte[4096];
    using FileStream fs = File.OpenRead(path);
    int n = fs.Read(buffer, 0, buffer.Length);
    for (int i = 0; i < n; i++)
    {
      if (buffer[i] == 0)
      {
        return null;
      }
    }
    return File.ReadAllText(path, Encoding.UTF8);
  }

  public void Dispose() { }
}
#pragma warning restore CA1849 // Call async methods when in an async method
#pragma warning restore CA1031 // Do not catch general exception types
#pragma warning restore CA1849 // Call async methods when in an async method
