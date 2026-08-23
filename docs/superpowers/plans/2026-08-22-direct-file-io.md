# Direct File I/O and Git — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace PowerShellFileSystemAccess and PowerShellGitAccess with direct BCL implementations, eliminating the single-file publish crash.

**Architecture:** Two new classes (`DirectFileSystemAccess`, `DirectGitAccess`) implement the same domain interfaces using only `System.IO`, `System.Text`, and `System.Text.RegularExpressions`. The `System.Management.Automation` and `PowerShell.ACL` references are removed from `FileSystem.ACL.csproj`. No domain contract changes.

**Tech Stack:** .NET 10, C#, xUnit, System.IO, System.Text.RegularExpressions, System.Diagnostics.Process

**Spec:** `docs/superpowers/specs/2026-08-22-untangle-powershell-design.md`

## Global Constraints

- All scripts are PowerShell (`.ps1`). Build: `dotnet build`. Test: `dotnet test`.
- Error codes stay identical: `FileNotFound`, `FileExists`, `AnchorNotFound`, `OccurrenceMismatch`, `BinaryFile`, `InvalidPattern`, `RootNotFound`, `FileSystemError`.
- Domain contracts (interfaces, records) are unchanged — only implementations change.
- Every task leaves the build green.
- Unit tests use temp directories (no shell, no runspace).

---

### Task 1: DirectFileSystemAccess with ReadLinesAsync

**Files:**

- Create: `src/eThangAgent.FileSystem.ACL/DirectFileSystemAccess.cs`
- Create: `tests/eThangAgent.FileSystem.ACL.Tests/DirectFileSystemAccessTests.cs`

**Interfaces:**

- Consumes: `IFileSystemAccess` (Task 1 only — the interface method `ReadLinesAsync`)
- Produces: `DirectFileSystemAccess` with `ReadLinesAsync` implemented

- [ ] **Step 1: Write the failing test**

Create `tests/eThangAgent.FileSystem.ACL.Tests/DirectFileSystemAccessTests.cs`:

```csharp
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

public class DirectFileSystemAccessTests : IDisposable
{
    private readonly string _tempDir;
    public DirectFileSystemAccessTests() => _tempDir = Path.Combine(Path.GetTempPath(), "ethang-dfs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public async Task ReadLinesAsync_ReadsInRange()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "line1\nline2\nline3\nline4\nline5");
        var access = new DirectFileSystemAccess();

        var r = await access.ReadLinesAsync(path, 2, 4);

        Assert.True(r.IsSuccess);
        Assert.Equal(new[] { "line2", "line3", "line4" }, r.Value!.Lines);
        Assert.Equal(4, r.Value.LastLineRead);
        Assert.Equal(5, r.Value.TotalLines);
    }

    [Fact]
    public async Task ReadLinesAsync_FileNotFound_ReturnsError()
    {
        var access = new DirectFileSystemAccess();
        var r = await access.ReadLinesAsync(Path.Combine(_tempDir, "nope.txt"), 1, 5);

        Assert.False(r.IsSuccess);
        Assert.Equal("FileNotFound", r.Error!.Code);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```powershell
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --filter "DirectFileSystemAccessTests" --nologo -v q
```

Expected: FAIL — `DirectFileSystemAccess` does not exist yet.

- [ ] **Step 3: Write minimal DirectoryFileSystemAccess**

Create `src/eThangAgent.FileSystem.ACL/DirectFileSystemAccess.cs`:

```csharp
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class DirectFileSystemAccess : IFileSystemAccess, IDisposable
{
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return Task.FromResult(Result<FileRead>.Failure(new Error("FileNotFound", $"File not found: {path}")));

        var allLines = new List<string>();
        // Count total lines by reading everything once (acceptable for tool-requested bounded reads)
        using var sr = new StreamReader(path, Encoding.UTF8);
        while (sr.ReadLine() is { } line)
            allLines.Add(line);

        var start = Math.Max(1, startLine) - 1;
        var end = Math.Min(endLine, allLines.Count);
        var slice = allLines.Skip(start).Take(end - start).ToList();
        return Task.FromResult(Result<FileRead>.Success(new FileRead(slice, end, allLines.Count)));
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: Run test to verify it passes**

```powershell
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --filter "DirectFileSystemAccessTests" --nologo -v q
```

Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/eThangAgent.FileSystem.ACL.Tests/DirectFileSystemAccessTests.cs src/eThangAgent.FileSystem.ACL/DirectFileSystemAccess.cs
git commit -m "feat: add DirectFileSystemAccess with ReadLinesAsync"
```

---

### Task 2: DirectFileSystemAccess — WriteFileAsync, ReplaceInFileAsync, SearchFilesAsync

**Files:**

- Modify: `src/eThangAgent.FileSystem.ACL/DirectFileSystemAccess.cs`
- Modify: `tests/eThangAgent.FileSystem.ACL.Tests/DirectFileSystemAccessTests.cs`

**Interfaces:**

- Consumes: `IFileWriteAccess`, `IFileEditAccess`, `ISearchAccess`
- Produces: Full `DirectFileSystemAccess` implementing all four interfaces

- [ ] **Step 1: Write tests for all remaining methods**

Append to `DirectFileSystemAccessTests.cs`:

```csharp
    [Fact]
    public async Task WriteFileAsync_CreatesFile()
    {
        var path = Path.Combine(_tempDir, "new.txt");
        Directory.CreateDirectory(_tempDir);
        var access = new DirectFileSystemAccess();

        var r = await ((IFileWriteAccess)access).WriteFileAsync(path, "hello world", overwrite: false);

        Assert.True(r.IsSuccess);
        Assert.True(r.Value!.Created);
        Assert.Equal(11L, r.Value.BytesWritten);
        Assert.Equal("hello world", File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteFileAsync_FileExistsWithoutOverwrite_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "exists.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "old");
        var access = new DirectFileSystemAccess();

        var r = await ((IFileWriteAccess)access).WriteFileAsync(path, "new", overwrite: false);

        Assert.False(r.IsSuccess);
        Assert.Equal("FileExists", r.Error!.Code);
    }

    [Fact]
    public async Task ReplaceInFileAsync_AllOccurrences()
    {
        var path = Path.Combine(_tempDir, "replace.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "a X b X c");
        var access = new DirectFileSystemAccess();

        var r = await ((IFileEditAccess)access).ReplaceInFileAsync(path, "X", "Y", occurrences: null);

        Assert.True(r.IsSuccess);
        Assert.Equal(2, r.Value!.Replaced);
        Assert.Equal("a Y b Y c", File.ReadAllText(path));
    }

    [Fact]
    public async Task ReplaceInFileAsync_AnchorNotFound_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "noanchor.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "hello");
        var access = new DirectFileSystemAccess();

        var r = await ((IFileEditAccess)access).ReplaceInFileAsync(path, "XYZ", "Z", occurrences: null);

        Assert.False(r.IsSuccess);
        Assert.Equal("AnchorNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task SearchFilesAsync_LiteralMatch_FindsLines()
    {
        var path = Path.Combine(_tempDir, "search.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "alpha\nbeta\ngamma");
        var access = new DirectFileSystemAccess();

        var r = await ((ISearchAccess)access).SearchFilesAsync(_tempDir, "beta", regex: false, glob: null, maxResults: 10, contextLines: 0);

        Assert.True(r.IsSuccess);
        Assert.Single(r.Value!.Matches);
        Assert.Equal("beta", r.Value.Matches[0].Lines[0]);
        Assert.Equal(2, r.Value.Matches[0].LineNumber);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --filter "DirectFileSystemAccessTests" --nologo -v q
```

Expected: FAIL — new methods not yet implemented.

- [ ] **Step 3: Implement all remaining methods**

Replace `DirectFileSystemAccess.cs` entirely:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --filter "DirectFileSystemAccessTests" --nologo -v q
```

Expected: PASS (all 7 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/eThangAgent.FileSystem.ACL.Tests/DirectFileSystemAccessTests.cs src/eThangAgent.FileSystem.ACL/DirectFileSystemAccess.cs
git commit -m "feat: complete DirectFileSystemAccess with write, edit, and search"
```

---

### Task 3: DirectGitAccess

**Files:**

- Create: `src/eThangAgent.FileSystem.ACL/DirectGitAccess.cs`
- Create: `tests/eThangAgent.FileSystem.ACL.Tests/DirectGitAccessTests.cs`

**Interfaces:**

- Consumes: `IGitQueryAccess`, `IGitCommitAccess` — `GetStatusAsync`, `GetDiffAsync`, `CommitAsync`
- Produces: `DirectGitAccess` implementing both git interfaces

- [ ] **Step 1: Write tests**

Create `tests/eThangAgent.FileSystem.ACL.Tests/DirectGitAccessTests.cs`:

```csharp
using System.Diagnostics;
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

public class DirectGitAccessTests : IDisposable
{
    private readonly string _repoDir;
    public DirectGitAccessTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "ethang-git-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        RunGit("init");
        RunGit("config", "user.email", "test@test.com");
        RunGit("config", "user.name", "Test");
    }

    public void Dispose() { try { Directory.Delete(_repoDir, true); } catch { } }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git", string.Join(' ', args.Select(EscapeArg)))
        {
            WorkingDirectory = _repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(30000);
    }

    private static string EscapeArg(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    [Fact]
    public async Task GetStatusAsync_CleanRepo_ReturnsNoEntries()
    {
        var access = new DirectGitAccess();

        var r = await access.GetStatusAsync(_repoDir);

        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Entries);
    }

    [Fact]
    public async Task GetStatusAsync_WithUnstagedFile_ReturnsEntry()
    {
        File.WriteAllText(Path.Combine(_repoDir, "test.txt"), "hello");
        var access = new DirectGitAccess();

        var r = await access.GetStatusAsync(_repoDir);

        Assert.True(r.IsSuccess);
        Assert.NotEmpty(r.Value!.Entries);
    }

    [Fact]
    public async Task GetDiffAsync_CleanRepo_ReturnsEmptyDiff()
    {
        var access = new DirectGitAccess();

        var r = await access.GetDiffAsync(_repoDir, "unstaged", path: null);

        Assert.True(r.IsSuccess);
        Assert.Equal(0, r.Value!.Stats.Files);
    }

    [Fact]
    public async Task CommitAsync_WithStagedChange_ReturnsHash()
    {
        File.WriteAllText(Path.Combine(_repoDir, "staged.txt"), "content");
        RunGit("add", "staged.txt");
        var access = new DirectGitAccess();

        var r = await access.CommitAsync(_repoDir, "feat: test commit");

        Assert.True(r.IsSuccess);
        Assert.NotNull(r.Value!.Hash);
        Assert.Equal("feat: test commit", r.Value.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --filter "DirectGitAccessTests" --nologo -v q
```

Expected: FAIL — `DirectGitAccess` does not exist yet.

- [ ] **Step 3: Implement DirectGitAccess**

Create `src/eThangAgent.FileSystem.ACL/DirectGitAccess.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class DirectGitAccess : IGitQueryAccess, IGitCommitAccess, IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    public Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        return RunGitAsync(repoPath, "status --porcelain", output =>
        {
            if (string.IsNullOrWhiteSpace(output))
                return Result<GitStatus>.Success(new GitStatus([], ""));

            var lines = output.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var entries = new List<GitStatusEntry>();
            foreach (var line in lines)
            {
                if (line.Length < 3) continue;
                var code = line[..2].Trim();
                var path = line[3..].Trim();
                entries.Add(new GitStatusEntry(code, path));
            }
            return Result<GitStatus>.Success(new GitStatus(entries, ""));
        }, ct);
    }

    public Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default)
    {
        var args = scope switch
        {
            "staged" => "diff --cached --stat --patch",
            _ => "diff --stat --patch"
        };
        if (path is not null)
            args += $" -- {EscapeArg(path)}";

        return RunGitAsync(repoPath, args, output =>
        {
            var stats = ParseDiffStats(output);
            var patch = output.Length > 20000 ? output[..20000] + "\n[truncated]" : output;
            var truncated = output.Length > 20000;
            return Result<GitDiff>.Success(new GitDiff(stats, patch, truncated, output.Length));
        }, ct);
    }

    public Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        return RunGitAsync(repoPath, $"commit -m {EscapeArg(message)}", output =>
        {
            var branch = RunGitSync(repoPath, "rev-parse --abbrev-ref HEAD");
            var hash = RunGitSync(repoPath, "rev-parse HEAD");
            return Result<GitCommitOutcome>.Success(
                new GitCommitOutcome(hash.TrimEnd(), branch.TrimEnd(), message));
        }, ct);
    }

    private static async Task<Result<T>> RunGitAsync<T>(
        string repoPath, string args, Func<string, Result<T>> parse, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync(ct);
                return Result<T>.Failure(new Error("GitError", err.TrimEnd()));
            }
            return parse(output);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(new Error("FileSystemError", ex.Message));
        }
    }

    private static string RunGitSync(string repoPath, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        return p.StandardOutput.ReadToEnd();
    }

    private static GitDiffStats ParseDiffStats(string output)
    {
        // Parse the last line of --stat output: "N files changed, X insertions(+), Y deletions(-)"
        var lines = output.Split('\n');
        foreach (var line in lines.Reverse())
        {
            if (!line.Contains("file") && !line.Contains("changed")) continue;
            var files = ExtractInt(line, "file");
            var adds = ExtractInt(line, "insertion");
            var dels = ExtractInt(line, "deletion");
            if (files > 0 || adds > 0 || dels > 0)
                return new GitDiffStats(files, adds, dels);
        }
        return new GitDiffStats(0, 0, 0);
    }

    private static int ExtractInt(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        var before = text[..idx].TrimEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return before.Length > 0 && int.TryParse(before[^1], out var n) ? n : 0;
    }

    private static string EscapeArg(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    public void Dispose() { }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --filter "DirectGitAccessTests" --nologo -v q
```

Expected: PASS (all 4 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/eThangAgent.FileSystem.ACL.Tests/DirectGitAccessTests.cs src/eThangAgent.FileSystem.ACL/DirectGitAccess.cs
git commit -m "feat: add DirectGitAccess using git CLI"
```

---

### Task 4: Delete old classes, update csproj, wire DI, verify

**Files:**

- Delete: `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs`
- Delete: `src/eThangAgent.FileSystem.ACL/PowerShellGitAccess.cs`
- Delete: `tests/eThangAgent.FileSystem.ACL.Tests/PowerShellFileSystemAccessTests.cs`
- Modify: `src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj`
- Modify: `src/eThangAgent.CLI/Program.cs` (DI registrations)

**Interfaces:**

- Consumes: `DirectFileSystemAccess`, `DirectGitAccess`
- Produces: Wired DI container, clean build

- [ ] **Step 1: Update csproj — remove PowerShell references**

Replace `src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Update DI in Program.cs**

Replace lines 72-79 (`PowerShellFileSystemAccess` + `PowerShellGitAccess` registrations):

```csharp
            .AddSingleton<DirectFileSystemAccess>()
            .AddSingleton<IFileSystemAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<IFileWriteAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<IFileEditAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<ISearchAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<DirectGitAccess>()
            .AddSingleton<IGitQueryAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
            .AddSingleton<IGitCommitAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
```

Also add `using eThangAgent.FileSystem.ACL;` if not already present at top of Program.cs (it already exists).

- [ ] **Step 3: Delete old files**

```powershell
Remove-Item src\eThangAgent.FileSystem.ACL\PowerShellFileSystemAccess.cs
Remove-Item src\eThangAgent.FileSystem.ACL\PowerShellGitAccess.cs
Remove-Item tests\eThangAgent.FileSystem.ACL.Tests\PowerShellFileSystemAccessTests.cs
```

- [ ] **Step 4: Build and run full FileSystem test suite**

```powershell
dotnet build --nologo
dotnet test tests\eThangAgent.FileSystem.ACL.Tests --nologo -v q
```

Expected: Build succeeds, all FileSystem tests pass.

- [ ] **Step 5: Run full solution test suite**

```powershell
dotnet test --nologo -v q
```

Expected: All 17 test projects pass.

- [ ] **Step 6: Publish and smoke-test the exe**

```powershell
dotnet publish src\eThangAgent.CLI -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true --nologo -v q
$env:OPENROUTER_API_KEY='dummy'
'quit' | & src\eThangAgent.CLI\bin\Release\net10.0\win-x64\publish\eThangAgent.CLI.exe 2>&1
```

Expected: Exe starts, prints prompt, exits cleanly — NO `ArgumentNullException` / crash dump.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: switch to DirectFileSystemAccess and DirectGitAccess, remove PowerShell from file I/O"
```
