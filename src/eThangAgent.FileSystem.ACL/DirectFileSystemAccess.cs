using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decision (CA1849): file operations here are small and local; sync APIs inside
// async tool wrappers keep the code simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types
namespace eThangAgent.FileSystem.ACL;

public sealed class DirectFileSystemAccess : IFileSystemAccess, IFileWriteAccess, IFileEditAccess, IDisposable
{
  public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
  {
    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<FileRead>(new DomainError("FileNotFound", $"File not found: {path}")));
    }

    List<string> allLines = [];
    using StreamReader sr = new(path, Encoding.UTF8);
    while (sr.ReadLine() is { } line)
    {
      allLines.Add(line);
    }

    int start = Math.Max(1, startLine) - 1;
    int end = Math.Min(endLine, allLines.Count);
    List<string> slice = [.. allLines.Skip(start).Take(end - start)];
    return Task.FromResult(Result.Success(new FileRead(slice, end, allLines.Count)));
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
    File.WriteAllText(path, content, new UTF8Encoding(false));
    return Task.FromResult(Result.Success(
        new FileWriteOutcome(created, new FileInfo(path).Length)));
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
    return Task.FromResult(Result.Success(
        new FileWriteOutcome(created, new FileInfo(path).Length)));
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
      _ = sb.Append(newText);
      originalPos = originalEnd;
      matchPos = normalizedIdx + matchAnchor.Length;
      done++;
    }

    _ = sb.Append(text.AsSpan(originalPos));
    string result = sb.ToString();
    File.WriteAllText(path, result, new UTF8Encoding(false));
    int lineCount = SplitLinesLikeReadLine(result).Count;
    return Task.FromResult(Result.Success(new ReplaceOutcome(done, lineCount)));
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

    // Splice: untouched lines keep their exact content AND terminator bytes. The
    // replacement carries the removed range's trailing terminator when one is
    // owed to the following line (or to the file's trailing-newline convention),
    // so a content-only edit never changes the file's line-ending style.
    StringBuilder sb = new();
    foreach ((string content, string terminator) in lines.Take(startLine - 1))
    {
      _ = sb.Append(content).Append(terminator);
    }

    string lastTerminator = lines[endLine - 1].Terminator;
    bool owesTerminator = newText.Length > 0
        && !newText.EndsWith('\n')
        && (endLine < lines.Count || lastTerminator.Length > 0);
    _ = owesTerminator ? sb.Append(newText).Append(lastTerminator) : sb.Append(newText);

    foreach ((string content, string terminator) in lines.Skip(endLine))
    {
      _ = sb.Append(content).Append(terminator);
    }

    string result = sb.ToString();
    File.WriteAllText(path, result, new UTF8Encoding(false));
    return Task.FromResult(Result.Success(
        new ReplaceOutcome(endLine - startLine + 1, SplitLinesLikeReadLine(result).Count)));
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
  ///     in the read tool's gutter format. One retry then corrects the anchor
  ///     (a missed quote, a wrong indentation depth) instead of a full re-read.</summary>
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
        $"Nearest match at line {best + 1} (similarity {bestScore:P0}); exact content:");
    for (int i = first; i <= last; i++)
    {
      _ = hint.AppendLine();
      _ = hint.Append(CultureInfo.InvariantCulture,
          $"{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {lines[i].Content}");
    }

    return hint.ToString();
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
