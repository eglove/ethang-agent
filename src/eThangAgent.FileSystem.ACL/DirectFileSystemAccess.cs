using System.Text;
using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decision (CA1849): file operations here are small and local; sync APIs inside
// async tool wrappers keep the code simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types
namespace eThangAgent.FileSystem.ACL;

// Named decision (CA1849): file operations here are small and local; sync APIs inside
// async tool wrappers keep the code simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class DirectFileSystemAccess : IFileSystemAccess, IFileWriteAccess, IFileEditAccess, ISearchAccess, IDisposable
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
          new DomainError("DirectoryNotFound", $"Parent directory does not exist: {dir}.")));
    }

    bool created = !File.Exists(path);
    File.WriteAllText(path, content, new UTF8Encoding(false));
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

    int count = 0;
    int idx = text.IndexOf(oldText, StringComparison.Ordinal);
    while (idx >= 0)
    {
      count++;
      idx = text.IndexOf(oldText, idx + oldText.Length, StringComparison.Ordinal);
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

    StringBuilder sb = new();
    int pos = 0;
    int done = 0;
    while (done < target)
    {
      idx = text.IndexOf(oldText, pos, StringComparison.Ordinal);
      _ = sb.Append(text.AsSpan(pos, idx - pos));
      _ = sb.Append(newText);
      pos = idx + oldText.Length;
      done++;
    }
    _ = sb.Append(text.AsSpan(pos));
    string result = sb.ToString();
    File.WriteAllText(path, result, new UTF8Encoding(false));
    int lineCount = result.Length == 0 ? 0 : 1 + result.Count(c => c == '\n');
    return Task.FromResult(Result.Success(new ReplaceOutcome(done, lineCount)));
  }

  public Task<Result<FileSearch>> SearchFilesAsync(
      string rootPath, string pattern, bool regex, string? glob,
      int maxResults, int contextLines, CancellationToken ct = default)
  {
    if (!Directory.Exists(rootPath))
    {
      return Task.FromResult(Result.Failure<FileSearch>(
          new DomainError("RootNotFound", $"Search root not found: {rootPath}")));
    }

    Regex? rx = null;
    if (regex)
    {
      try
      {
        rx = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
      }
      catch (ArgumentException ex)
      {
        return Task.FromResult(Result.Failure<FileSearch>(
            new DomainError("InvalidPattern", $"Invalid regular expression '{pattern}': {ex.Message}")));
      }
    }

    List<SearchMatch> matches = [];
    int scanned = 0;
    bool truncated = false;

    foreach (string file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
    {
      if (matches.Count >= maxResults)
      {
        truncated = true;
        break;
      }
      if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      {
        continue;
      }

      if (glob is not null && !MatchesGlob(Path.GetFileName(file), glob))
      {
        continue;
      }

      // Skip binary files
      if (!IsTextFile(file))
      {
        continue;
      }

      string[]? lines = ReadAllLines(file);
      if (lines is null)
      {
        continue;
      }

      scanned++;

      for (int i = 0; i < lines.Length; i++)
      {
        bool isMatch = rx is not null
            ? rx.IsMatch(lines[i])
            : lines[i].Contains(pattern, StringComparison.Ordinal);

        if (isMatch)
        {
          int from = Math.Max(0, i - contextLines);
          int to = Math.Min(lines.Length - 1, i + contextLines);
          string[] window = lines[from..(to + 1)];
          matches.Add(new SearchMatch(file, i + 1, window));
        }
      }
    }

    return Task.FromResult(Result.Success(
        new FileSearch(matches, truncated, scanned)));
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

  private static bool IsTextFile(string path)
  {
    try
    {
      byte[] buffer = new byte[4096];
      using FileStream fs = File.OpenRead(path);
      int n = fs.Read(buffer, 0, buffer.Length);
      for (int i = 0; i < n; i++)
      {
        if (buffer[i] == 0)
        {
          return false;
        }
      }
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }

  private static string[]? ReadAllLines(string path)
  {
    try
    {
      return File.ReadAllLines(path, Encoding.UTF8);
    }
    catch (Exception)
    {
      return null;
    }
  }

  private static bool MatchesGlob(string fileName, string glob)
  {
    // Simple * pattern: convert to regex for basic wildcard matching
    string pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
    try
    {
      return Regex.IsMatch(fileName, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }
    catch (Exception)
    {
      return false;
    }
  }

  public void Dispose() { }
}
#pragma warning restore CA1849 // Call async methods when in an async method
#pragma warning restore CA1031 // Do not catch general exception types
#pragma warning restore CA1849 // Call async methods when in an async method
