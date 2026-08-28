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
          new DomainError("DirectoryNotFound", $"Parent directory does not exist: {dir}.")));
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
          new DomainError("DirectoryNotFound", $"Parent directory does not exist: {dir}.")));
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

    Result<Regex?> compiled = CompilePattern(pattern, regex);
    if (!compiled.IsSuccess)
    {
      return Task.FromResult(Result.Failure<FileSearch>(compiled.Error!));
    }

    SearchPlan plan = new(compiled.Value!, pattern, glob, maxResults, contextLines);
    List<SearchMatch> matches = [];
    int scanned = 0;
    bool truncated = CollectMatches(rootPath, plan, matches, ref scanned);
    FileSearch result = new(matches, truncated, scanned);
    return Task.FromResult(Result.Success(result));
  }

  /// <summary>Compiles the regular-expression pattern when <paramref name="regex"/>;
  ///     a plain pattern matches literally.</summary>
  private static Result<Regex?> CompilePattern(string pattern, bool regex)
  {
    if (!regex)
    {
      return Result.Success<Regex?>(null);
    }

    try
    {
      Result<Regex?> compiled = Result.Success<Regex?>(new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2)));
      return compiled;
    }
    catch (ArgumentException ex)
    {
      return Result.Failure<Regex?>(new DomainError("InvalidPattern",
          $"Invalid regular expression '{pattern}': {ex.Message}"));
    }
  }

  /// <summary>Everything one search pass needs: the matcher plus its windowing limits.</summary>
  private sealed record SearchPlan(Regex? Regex, string Pattern, string? Glob, int MaxResults, int ContextLines)
  {
    internal bool IsMatch(string line) => Regex is not null
        ? Regex.IsMatch(line)
        : line.Contains(Pattern, StringComparison.Ordinal);
  }

  /// <summary>Walks the tree once, collecting matches until the result cap. Sets
  ///     <paramref name="scanned"/> to the number of readable text files examined.</summary>
  private static bool CollectMatches(string rootPath, SearchPlan plan, List<SearchMatch> matches, ref int scanned)
  {
    bool truncated = false;
    foreach (string file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
    {
      if (matches.Count >= plan.MaxResults)
      {
        truncated = true;
        break;
      }
      if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      {
        continue;
      }

      if (plan.Glob is not null && !MatchesGlob(Path.GetFileName(file), plan.Glob))
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
      CollectLineMatches(file, lines, plan, matches);
    }

    return truncated;
  }

  private static void CollectLineMatches(string file, string[] lines, SearchPlan plan, List<SearchMatch> matches)
  {
    for (int i = 0; i < lines.Length; i++)
    {
      if (!plan.IsMatch(lines[i]))
      {
        continue;
      }

      int from = Math.Max(0, i - plan.ContextLines);
      int to = Math.Min(lines.Length - 1, i + plan.ContextLines);
      string[] window = lines[from..(to + 1)];
      matches.Add(new SearchMatch(file, i + 1, window));
    }
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
