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
          new DomainError("AnchorNotFound", $"Anchor text (length {oldText.Length}) not found in {path}.")));
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
    int lineCount = result.Length == 0 ? 0 : 1 + result.Count(c => c == '\n');
    return Task.FromResult(Result.Success(new ReplaceOutcome(done, lineCount)));
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
