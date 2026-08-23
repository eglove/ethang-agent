using System.Text;
using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class DirectFileSystemAccess : IFileSystemAccess, IFileWriteAccess, IFileEditAccess, ISearchAccess, IDisposable
{
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return Task.FromResult(Result<FileRead>.Failure(new Error("FileNotFound", $"File not found: {path}")));

        var allLines = new List<string>();
        using var sr = new StreamReader(path, Encoding.UTF8);
        while (sr.ReadLine() is { } line)
            allLines.Add(line);

        var start = Math.Max(1, startLine) - 1;
        var end = Math.Min(endLine, allLines.Count);
        var slice = allLines.Skip(start).Take(end - start).ToList();
        return Task.FromResult(Result<FileRead>.Success(new FileRead(slice, end, allLines.Count)));
    }

    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
    {
        if (File.Exists(path) && !overwrite)
            return Task.FromResult(Result<FileWriteOutcome>.Failure(
                new Error("FileExists", $"File already exists: {path} (overwrite not requested).")));

        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            return Task.FromResult(Result<FileWriteOutcome>.Failure(
                new Error("DirectoryNotFound", $"Parent directory does not exist: {dir}.")));

        var created = !File.Exists(path);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return Task.FromResult(Result<FileWriteOutcome>.Success(
            new FileWriteOutcome(created, new FileInfo(path).Length)));
    }

    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return Task.FromResult(Result<ReplaceOutcome>.Failure(
                new Error("FileNotFound", $"File not found: {path}")));

        var text = ReadAllTextRejectBinary(path);
        if (text is null)
            return Task.FromResult(Result<ReplaceOutcome>.Failure(
                new Error("BinaryFile", $"File appears to be binary (NUL byte found): {path}.")));

        var count = 0;
        var idx = text.IndexOf(oldText, StringComparison.Ordinal);
        while (idx >= 0) { count++; idx = text.IndexOf(oldText, idx + oldText.Length, StringComparison.Ordinal); }

        if (count == 0)
            return Task.FromResult(Result<ReplaceOutcome>.Failure(
                new Error("AnchorNotFound", $"Anchor text (length {oldText.Length}) not found in {path}.")));

        var target = occurrences ?? count;
        if (occurrences is not null && count != occurrences.Value)
            return Task.FromResult(Result<ReplaceOutcome>.Failure(
                new Error("OccurrenceMismatch", $"Anchor occurs {count} time(s) but {occurrences} replacement(s) were requested.")));

        var sb = new StringBuilder();
        var pos = 0; var done = 0;
        while (done < target)
        {
            idx = text.IndexOf(oldText, pos, StringComparison.Ordinal);
            sb.Append(text.AsSpan(pos, idx - pos));
            sb.Append(newText);
            pos = idx + oldText.Length;
            done++;
        }
        sb.Append(text.AsSpan(pos));
        var result = sb.ToString();
        File.WriteAllText(path, result, new UTF8Encoding(false));
        var lineCount = result.Length == 0 ? 0 : 1 + result.Count(c => c == '\n');
        return Task.FromResult(Result<ReplaceOutcome>.Success(new ReplaceOutcome(done, lineCount)));
    }

    public Task<Result<FileSearch>> SearchFilesAsync(
        string rootPath, string pattern, bool regex, string? glob,
        int maxResults, int contextLines, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath))
            return Task.FromResult(Result<FileSearch>.Failure(
                new Error("RootNotFound", $"Search root not found: {rootPath}")));

        Regex? rx = null;
        if (regex)
        {
            try { rx = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex)
            {
                return Task.FromResult(Result<FileSearch>.Failure(
                    new Error("InvalidPattern", $"Invalid regular expression '{pattern}': {ex.Message}")));
            }
        }

        var matches = new List<SearchMatch>();
        var scanned = 0;
        var truncated = false;

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            if (matches.Count >= maxResults) { truncated = true; break; }
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            if (glob is not null && !MatchesGlob(Path.GetFileName(file), glob)) continue;

            // Skip binary files
            if (!IsTextFile(file)) continue;

            var lines = ReadAllLines(file);
            if (lines is null) continue;
            scanned++;

            for (var i = 0; i < lines.Length; i++)
            {
                var isMatch = rx is not null
                    ? rx.IsMatch(lines[i])
                    : lines[i].Contains(pattern, StringComparison.Ordinal);

                if (isMatch)
                {
                    var from = Math.Max(0, i - contextLines);
                    var to = Math.Min(lines.Length - 1, i + contextLines);
                    var window = lines[from..(to + 1)];
                    matches.Add(new SearchMatch(file, i + 1, window));
                }
            }
        }

        return Task.FromResult(Result<FileSearch>.Success(
            new FileSearch(matches, truncated, scanned)));
    }

    private static string? ReadAllTextRejectBinary(string path)
    {
        var buffer = new byte[4096];
        using var fs = File.OpenRead(path);
        var n = fs.Read(buffer, 0, buffer.Length);
        for (var i = 0; i < n; i++) { if (buffer[i] == 0) return null; }
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static bool IsTextFile(string path)
    {
        try
        {
            var buffer = new byte[4096];
            using var fs = File.OpenRead(path);
            var n = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < n; i++) { if (buffer[i] == 0) return false; }
            return true;
        }
        catch { return false; }
    }

    private static string[]? ReadAllLines(string path)
    {
        try { return File.ReadAllLines(path, Encoding.UTF8); }
        catch { return null; }
    }

    private static bool MatchesGlob(string fileName, string glob)
    {
        // Simple * pattern: convert to regex for basic wildcard matching
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*") + "$";
        try { return Regex.IsMatch(fileName, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch { return false; }
    }

    public void Dispose() { }
}
