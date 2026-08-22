# Stage 1 / SP1 — File Manipulation Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the model safe, strictly-validated `write`, `edit`, and `search_files` tools through the FileSystem ACL, per spec `docs/superpowers/specs/2026-08-21-stage-1-methodology-port-design.md` (SP1).

**Architecture:** Three new tools in the Tool Domain following the exact `ReadTool` house pattern (strict input records with `Result<T>` parsing, `ITool` definitions with verbatim-documented format contracts, errors returned as `Error [Code]: message` results). New narrow ACL interfaces (`IFileWriteAccess`, `IFileEditAccess`, `ISearchAccess`) implemented by the existing `PowerShellFileSystemAccess` on its open runspace — `IFileSystemAccess` stays untouched so existing fakes are unbroken. Paths resolve through a `WorkspacePathResolver` anchored at the workspace root; escape attempts are rejected, never coerced.

**Tech Stack:** C# / .NET 10, xUnit, PowerShell runspaces (in-process), System.Text.Json for argument parsing.

**Spec:** `docs/superpowers/specs/2026-08-21-stage-1-methodology-port-design.md`

## Global Constraints

- Windows-only, PowerShell is the only shell; no `.sh`/`.cmd`/`.bat` artifacts.
- Strict correctness at boundaries: required parameters are required, types exact, unknown parameters rejected, nothing silently coerced or defaulted. The single sanctioned leniency: `search_files` `maxResults` overshoot clamps to the cap **with a visible `[warning]` line**.
- Expected failures flow through `Result<T>` / error values; exceptions are programmer error only.
- Domain code never touches `System.IO` directly except the path-resolution helpers (`Path.*`) inside `WorkspacePathResolver`; all file I/O goes through the new access interfaces.
- Every task ends with `dotnet build` + targeted `dotnet test` green.
- DI wiring only at the composition root (`src/eThangAgent.CLI/Program.cs`).
- Model-facing outputs use the annotation-line format contracts below, documented verbatim in each tool description.
- Unit tests use fakes only (no PowerShell, HTTP, SQLite); integration tests exercise `PowerShellFileSystemAccess` against real temp directories.
- Namespaces: domain types in `eThangAgent.ToolDomain`; ACL implementation in `eThangAgent.FileSystem.ACL`.

## File Structure

```text
src/eThangAgent.Tool.Domain/
  WorkspacePathResolver.cs        # NEW — resolves + jails paths against workspace root
  FileWriteOutcome.cs             # NEW — (bool Created, long BytesWritten)
  ReplaceOutcome.cs               # NEW — (int Replaced, int NewLineCount)
  FileSearch.cs                   # NEW — SearchMatch record + FileSearch page (truncation-aware)
  IFileWriteAccess.cs             # NEW — seam for write
  IFileEditAccess.cs              # NEW — seam for literal replacement
  ISearchAccess.cs                # NEW — seam for bounded search
  WriteToolInput.cs / WriteTool.cs        # NEW
  EditToolInput.cs / EditTool.cs          # NEW
  SearchToolInput.cs / SearchTool.cs      # NEW
src/eThangAgent.FileSystem.ACL/
  PowerShellFileSystemAccess.cs   # MODIFY — add three script-backed methods, implement new interfaces
src/eThangAgent.CLI/
  Program.cs                      # MODIFY — resolver singleton + three AgentToolBinding entries
README.md                         # MODIFY — tool list

tests/eThangAgent.Tool.Domain.Tests/
  WorkspacePathResolverTests.cs   # NEW
  WriteToolTests.cs               # NEW (input parsing + tool formatting over fakes)
  EditToolTests.cs                # NEW
  SearchToolTests.cs              # NEW
tests/eThangAgent.FileSystem.ACL.Tests/
  FileWriteIntegrationTests.cs    # NEW (real temp dirs, real runspace)
  FileEditIntegrationTests.cs     # NEW
  FileSearchIntegrationTests.cs   # NEW
```

Existing reference implementations to copy patterns from (read them first): `src/eThangAgent.Tool.Domain/ReadTool.cs`, `ReadToolInput.cs`, `PowerShellFileSystemAccess.cs`, `tests/eThangAgent.Tool.Domain.Tests/ReadToolTests.cs`.

---

### Task 1: Workspace path resolution seam

**Files:**

- Create: `src/eThangAgent.Tool.Domain/WorkspacePathResolver.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/WorkspacePathResolverTests.cs`

**Interfaces:**

- Consumes: nothing (pure domain value logic).
- Produces: `sealed class WorkspacePathResolver(string root)` with `Result<string> Resolve(string path)` — resolves workspace-relative or absolute paths to a canonical absolute path guaranteed inside `root`; fails with `InvalidPath` (unresolvable) or `PathOutsideWorkspace` (escape attempt) otherwise.

- [ ] **Step 1: Write the failing tests**

```csharp
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class WorkspacePathResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-ws").FullName;
    private WorkspacePathResolver MakeResolver() => new(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RelativePath_ResolvesAgainstRoot()
    {
        var r = MakeResolver().Resolve("src\\a.cs");
        Assert.True(r.IsSuccess);
        Assert.Equal(Path.Combine(_root, "src", "a.cs"), r.Value);
    }

    [Fact]
    public void DotSegments_Collapse()
    {
        var r = MakeResolver().Resolve("src\\.\\b.cs");
        Assert.True(r.IsSuccess);
        Assert.Equal(Path.Combine(_root, "src", "b.cs"), r.Value);
    }

    [Fact]
    public void AbsolutePathInsideRoot_Accepted()
    {
        var p = Path.Combine(_root, "c.cs");
        var r = MakeResolver().Resolve(p);
        Assert.True(r.IsSuccess);
        Assert.Equal(p, r.Value);
    }

    [Fact]
    public void ParentEscape_Rejected()
    {
        var r = MakeResolver().Resolve("..\\outside.txt");
        Assert.False(r.IsSuccess);
        Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
        Assert.Contains(_root, r.Error.Message);
    }

    [Fact]
    public void SiblingPrefixEscape_Rejected()
    {
        // A sibling directory whose name shares a prefix with the root starts
        // with _root as a raw string but is outside it — comparison must be segment-aware.
        var sibling = _root.TrimEnd('\\') + "x\\file.txt";
        var r = MakeResolver().Resolve(sibling);
        Assert.False(r.IsSuccess);
        Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
    }

    [Fact]
    public void RootItself_Accepted()
    {
        var r = MakeResolver().Resolve(_root);
        Assert.True(r.IsSuccess);
        Assert.Equal(Path.GetFullPath(_root), r.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespace_Rejected(string path)
    {
        var r = MakeResolver().Resolve(path);
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidPath", r.Error!.Code);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter WorkspacePathResolverTests`
Expected: FAIL — compile error, `WorkspacePathResolver` does not exist.

- [ ] **Step 3: Implement**

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Resolves tool-supplied paths against the workspace root and refuses
/// anything that resolves outside it. Segment-aware: a sibling directory whose name
/// merely shares a prefix with the root is correctly rejected.</summary>
public sealed class WorkspacePathResolver
{
    private readonly string _root;

    public WorkspacePathResolver(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root must be a non-empty path.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    public Result<string> Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string>.Failure(new Error("InvalidPath",
                "'path' must be a non-empty string."));

        string candidate = Path.IsPathRooted(path) ? path : Path.Combine(_root, path);
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Failure(new Error("InvalidPath",
                $"'path' could not be resolved: {ex.Message}"));
        }

        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(full, _root, StringComparison.Ordinal))
        {
            return Result<string>.Failure(new Error("PathOutsideWorkspace",
                $"'{path}' resolves to '{full}', which is outside the workspace '{_root}'. " +
                "Use a path inside the workspace."));
        }

        return Result<string>.Success(full);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter WorkspacePathResolverTests`
Expected: PASS (all 8).

- [ ] **Step 5: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/WorkspacePathResolver.cs tests/eThangAgent.Tool.Domain.Tests/WorkspacePathResolverTests.cs
git commit -m "feat(tools): workspace path resolver with segment-aware escape rejection"
```

### Task 2: `write` backend — `IFileWriteAccess` + PowerShell implementation

**Files:**
- Create: `src/eThangAgent.Tool.Domain/IFileWriteAccess.cs`
- Create: `src/eThangAgent.Tool.Domain/FileWriteOutcome.cs`
- Modify: `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs` (implement the new interface; existing members untouched)
- Test: `tests/eThangAgent.FileSystem.ACL.Tests/FileWriteIntegrationTests.cs`

**Interfaces:**
- Consumes: existing runspace/semaphore infrastructure in `PowerShellFileSystemAccess`; `Result<T>` / `Error` from SharedKernel.
- Produces: `Task<Result<FileWriteOutcome>> WriteFileAsync(string path, string content, bool overwrite, CancellationToken ct = default)` on new interface `IFileWriteAccess`; `sealed record FileWriteOutcome(bool Created, long BytesWritten)`.

Error codes (contract, used by Task 3): `FileExists` (target exists and `overwrite=false`), `DirectoryNotFound` (parent missing — message names the missing directory), `FileSystemError`.

- [ ] **Step 1: Write the failing integration tests**

Follow the fixture style of the existing FileSystem.ACL integration tests: real temp directory per test class, `using var access = new PowerShellFileSystemAccess();`, dispose deletes the tree.

```csharp
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

public sealed class FileWriteIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-w").FullName;
    private readonly PowerShellFileSystemAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteNewFile_Succeeds_CreatedTrue()
    {
        var path = Path.Combine(_root, "new.txt");
        var r = await _access.WriteFileAsync(path, "hello", overwrite: false);
        Assert.True(r.IsSuccess);
        Assert.True(r.Value!.Created);
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteExisting_WithoutOverwrite_Fails_FileExists()
    {
        var path = Path.Combine(_root, "x.txt");
        await _access.WriteFileAsync(path, "first", overwrite: false);
        var r = await _access.WriteFileAsync(path, "second", overwrite: false);
        Assert.False(r.IsSuccess);
        Assert.Equal("FileExists", r.Error!.Code);
        Assert.Equal("first", File.ReadAllText(path)); // unchanged
    }

    [Fact]
    public async Task WriteExisting_WithOverwrite_ReplacesContent_CreatedFalse()
    {
        var path = Path.Combine(_root, "y.txt");
        await _access.WriteFileAsync(path, "old", overwrite: false);
        var r = await _access.WriteFileAsync(path, "brand new content", overwrite: true);
        Assert.True(r.IsSuccess);
        Assert.False(r.Value!.Created);
        Assert.Equal("brand new content", File.ReadAllText(path));
    }

    [Fact]
    public async Task BytesWritten_ReflectsUtf8ByteCount_NoBom()
    {
        var path = Path.Combine(_root, "bytes.txt");
        var r = await _access.WriteFileAsync(path, "é", overwrite: false); // é = 2 UTF-8 bytes
        Assert.True(r.IsSuccess);
        Assert.Equal(2L, r.Value!.BytesWritten);
        var raw = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, raw[0]); // no BOM
    }

    [Fact]
    public async Task MissingParentDirectory_Fails_DirectoryNotFound()
    {
        var path = Path.Combine(_root, "no", "such", "dir", "f.txt");
        var r = await _access.WriteFileAsync(path, "x", overwrite: false);
        Assert.False(r.IsSuccess);
        Assert.Equal("DirectoryNotFound", r.Error!.Code);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/eThangAgent.FileSystem.ACL.Tests --filter FileWriteIntegrationTests`
Expected: FAIL — compile error, `WriteFileAsync` does not exist.

- [ ] **Step 3: Implement**

Domain types (`src/eThangAgent.Tool.Domain/FileWriteOutcome.cs`):

```csharp
namespace eThangAgent.ToolDomain;

public sealed record FileWriteOutcome(bool Created, long BytesWritten);
```

(`src/eThangAgent.Tool.Domain/IFileWriteAccess.cs`):

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileWriteAccess
{
    /// <summary>Creates or replaces a file as UTF-8 without BOM. Never creates
    /// parent directories. Never overwrites unless <paramref name="overwrite"/> is true.</summary>
    Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default);
}
```

In `PowerShellFileSystemAccess`, add a second script constant beside the existing one:

```powershell
private const string WriteScript = """
    param([string]$Path, [string]$Content, [bool]$Overwrite)
    $exists = [System.IO.File]::Exists($Path)
    if ($exists -and -not $Overwrite) {
        return @{ Ok = $false; ErrorCode = "FileExists";
                  ErrorMessage = "File already exists: $Path (overwrite not requested)." }
    }
    $dir = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [System.IO.Directory]::Exists($dir)) {
        return @{ Ok = $false; ErrorCode = "DirectoryNotFound";
                  ErrorMessage = "Parent directory does not exist: $dir." }
    }
    try {
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
        return @{ Ok = $true; Created = (-not $exists);
                  Bytes = (Get-Item -LiteralPath $Path).Length }
    } catch {
        return @{ Ok = $false; ErrorCode = "FileSystemError";
                  ErrorMessage = $_.Exception.Message }
    }
    """;
```

And the method, reusing `_runspace` and `_gate` exactly like `ReadLinesAsync`:

```csharp
public async Task<Result<FileWriteOutcome>> WriteFileAsync(
    string path, string content, bool overwrite, CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    try
    {
        using var ps = System.Management.Automation.PowerShell.Create(_runspace);
        ps.AddScript(WriteScript)
          .AddParameter("Path", path)
          .AddParameter("Content", content)
          .AddParameter("Overwrite", overwrite);
        var output = ps.Invoke();
        if (ps.HadErrors || output.Count == 0)
            return Result<FileWriteOutcome>.Failure(new Error("FileSystemError",
                ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                    ?? "PowerShell script produced no output."));
        var table = (Hashtable)output[0].BaseObject;
        if (table["Ok"] is not true)
            return Result<FileWriteOutcome>.Failure(new Error(
                table["ErrorCode"]?.ToString() ?? "FileSystemError",
                table["ErrorMessage"]?.ToString() ?? "Unknown filesystem error."));
        return Result<FileWriteOutcome>.Success(new FileWriteOutcome(
            table["Created"] is true, Convert.ToInt64(table["Bytes"]!)));
    }
    finally { _gate.Release(); }
}
```

Update the class declaration: `public sealed class PowerShellFileSystemAccess : IFileSystemAccess, IFileWriteAccess, IDisposable`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/eThangAgent.FileSystem.ACL.Tests --filter FileWriteIntegrationTests`
Expected: PASS (all 5).

- [ ] **Step 5: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/IFileWriteAccess.cs src/eThangAgent.Tool.Domain/FileWriteOutcome.cs src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs tests/eThangAgent.FileSystem.ACL.Tests/FileWriteIntegrationTests.cs
git commit -m "feat(fs-acl): file write with explicit overwrite gate"
```

---

### Task 3: `write` tool — strict input + format contract

**Files:**
- Create: `src/eThangAgent.Tool.Domain/WriteToolInput.cs`
- Create: `src/eThangAgent.Tool.Domain/WriteTool.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/WriteToolTests.cs`

**Interfaces:**
- Consumes: `WorkspacePathResolver.Resolve` (Task 1), `IFileWriteAccess.WriteFileAsync` (Task 2).
- Produces: `ITool` named `write`. JSON args: `path` (string, required, non-empty), `content` (string, required — may be empty for an explicitly empty file), `overwrite` (boolean, required). Output contract, documented verbatim in the description below: `[write <path>] created, N bytes` or `[write <path>] overwritten, N bytes`.

- [ ] **Step 1: Write the failing tests**

```csharp
using eThangAgent.FileSystem.ACL; // only for the fake's interface target? NO — see note below
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

// NOTE: this file must NOT reference any ACL namespace. The using above is wrong —
// delete it. Fakes implement the Tool Domain interface only.

public class WriteToolTests
{
    private const string Resolved = @"C:\ws\a.txt";

    private static WorkspacePathResolver MakeResolver() =>
        new(System.IO.Path.GetTempPath()); // any valid root; fakes bypass real FS

    private static WriteTool MakeTool(Result<FileWriteOutcome> outcome) =>
        new(MakeResolver(), new FakeFileWriteAccess(outcome));

    // ---- Input parsing ----

    [Theory]
    [InlineData("""", "x", true, "path")]
    [InlineData("a.txt", null, true, "content")]
    public async Task MissingRequiredParameter_ReturnsError(string path, string? _, bool ow, string expect)
    {
        var json = path == "" ? """{"overwrite":true}"""
                 : """{"path":"a.txt","overwrite":true}""";
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write", json));
        Assert.True(result.IsError);
        Assert.Contains(expect, result.Content);
    }

    [Fact]
    public async Task Overwrite_MustBeBoolean_StringRejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"path":"a.txt","content":"x","overwrite":"yes"}"""));
        Assert.True(result.IsError);
        Assert.Contains("overwrite", result.Content);
        Assert.Contains("boolean", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"path":"a.txt","content":"x","overwrite":true,"encoding":"utf16"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("encoding", result.Content);
    }

    // ---- Path jail ----

    [Fact]
    public async Task PathOutsideWorkspace_ReturnsResolverError()
    {
        var tool = new WriteTool(
            new WorkspacePathResolver(@"C:\ws"),
            new FakeFileWriteAccess(null!));
        var result = await tool.ExecuteAsync(new RawToolInput("write",
            """{"path":"..\\evil.txt","content":"x","overwrite":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("PathOutsideWorkspace", result.Content);
    }

    // ---- Success formatting ----

    [Fact]
    public async Task Created_FormatsAnnotationLine()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(true, 42)))
            .ExecuteAsync(new RawToolInput("write",
                """{"path":"a.txt","content":"x","overwrite":false}"""));
        Assert.False(result.IsError);
        Assert.Contains($"[write {Resolved}] created, 42 bytes", result.Content);
    }

    [Fact]
    public async Task Overwritten_FormatsAnnotationLine()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(false, 7)))
            .ExecuteAsync(new RawToolInput("write",
                """{"path":"a.txt","content":"x","overwrite":true}"""));
        Assert.False(result.IsError);
        Assert.Contains("overwritten, 7 bytes", result.Content);
    }

    [Fact]
    public async Task EmptyContent_Allowed_ZeroBytes()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(true, 0)))
            .ExecuteAsync(new RawToolInput("write",
                """{"path":"empty.txt","content":"","overwrite":false}"""));
        Assert.False(result.IsError);
        Assert.Contains("created, 0 bytes", result.Content);
    }

    // ---- Backend errors surface verbatim ----

    [Fact]
    public async Task FileExists_SurfacesBackendError()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Failure(
                new Error("FileExists", "File already exists: a.txt")))
            .ExecuteAsync(new RawToolInput("write",
                """{"path":"a.txt","content":"x","overwrite":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [FileExists]", result.Content);
    }

    private sealed class FakeFileWriteAccess(Result<FileWriteOutcome> outcome) : IFileWriteAccess
    {
        public Task<Result<FileWriteOutcome>> WriteFileAsync(
            string path, string content, bool overwrite, CancellationToken ct = default)
            => Task.FromResult(outcome);
    }
}
```

Implementation note for the executor: the `Resolved` constant assumes the resolver maps `"a.txt"` under its root — construct the resolver in tests with a fixed root (e.g. `@"C:\\ws"`) so the expected annotation line is deterministic; adjust the constant to match whatever root you fix. The resolver never touches the filesystem, so a synthetic root is safe in unit tests.

- [ ] **Step 2: Run to verify failure** — compile error: `WriteTool` / `WriteToolInput` do not exist.

- [ ] **Step 3: Implement input record** (copy the exact house pattern from `ReadToolInput.Create`: parse JSON, object check, unknown-parameter rejection with allowed list, per-parameter type checks, `Missing`/`WrongType` helpers):

```csharp
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WriteToolInput(string Path, string Content, bool Overwrite)
{
    public static Result<WriteToolInput> Create(string jsonArguments)
    {
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Fail(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }
        if (json.ValueKind != JsonValueKind.Object)
            return Fail(new Error("InvalidJsonArguments", "Arguments must be a JSON object."));

        var known = new HashSet<string>(["path", "content", "overwrite"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject().Where(p => !known.Contains(p.Name)).Select(p => p.Name).ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, content, overwrite."));

        if (!json.TryGetProperty("path", out var pathEl)) return Missing("path");
        if (pathEl.ValueKind != JsonValueKind.String) return WrongType("path", "string", pathEl.ValueKind);
        var path = pathEl.GetString()!;
        if (path.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'path' must be a non-empty string."));

        if (!json.TryGetProperty("content", out var contentEl)) return Missing("content");
        if (contentEl.ValueKind != JsonValueKind.String) return WrongType("content", "string", contentEl.ValueKind);
        var content = contentEl.GetString()!;

        if (!json.TryGetProperty("overwrite", out var owEl)) return Missing("overwrite");
        if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return WrongType("overwrite", "boolean", owEl.ValueKind);
        var overwrite = owEl.GetBoolean();

        return Result<WriteToolInput>.Success(new(path, content, overwrite));
    }

    private static Result<WriteToolInput> Missing(string n) =>
        Result<WriteToolInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{n}'. This tool requires path, content, and overwrite."));
    private static Result<WriteToolInput> WrongType(string n, string e, JsonValueKind a) =>
        Result<WriteToolInput>.Failure(new Error("InvalidParameterType",
            $"'{n}' must be a {e}, but got {a}."));
    private static Result<WriteToolInput> Fail(Error err) =>
        Result<WriteToolInput>.Failure(err);
}
```

- [ ] **Step 4: Implement the tool**

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteTool : ITool
{
    private readonly WorkspacePathResolver _resolver;
    private readonly IFileWriteAccess _files;

    public ToolDefinition Definition { get; } = new(
        "write",
        "Create or replace a text file. path, content, and overwrite are all mandatory; " +
        "the call fails if the file exists unless overwrite is exactly true — it will never " +
        "silently replace anything. Parent directories are never created automatically; create " +
        "them first if needed. Paths resolve inside the workspace; escapes are rejected. Content " +
        "is written verbatim as UTF-8 without BOM (an empty string writes an empty file). Output " +
        "is a single annotation line in [brackets]: `[write <path>] created|overwritten, N bytes` — " +
        "metadata, not file content. Errors begin with `Error [Code]:`.",
        [
            new ToolParameter("path", ToolParameterType.String,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("content", ToolParameterType.String,
                "Exact file content. May be empty."),
            new ToolParameter("overwrite", ToolParameterType.Boolean,
                "true to replace an existing file, false to refuse."),
        ]);

    public WriteTool(WorkspacePathResolver resolver, IFileWriteAccess files)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = WriteToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Err(parsed.Error!);

        var resolved = _resolver.Resolve(parsed.Value!.Path);
        if (!resolved.IsSuccess)
            return Err(resolved.Error!);

        var written = await _files.WriteFileAsync(resolved.Value!, parsed.Value.Content, parsed.Value.Overwrite, ct);
        if (!written.IsSuccess)
            return Err(written.Error!);

        var o = written.Value!;
        return new ToolResult($"[write {resolved.Value}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes", false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter WriteToolTests`
Expected: PASS (all 9).

- [ ] **Step 6: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/WriteToolInput.cs src/eThangAgent.Tool.Domain/WriteTool.cs tests/eThangAgent.Tool.Domain.Tests/WriteToolTests.cs
git commit -m "feat(tools): write tool with explicit overwrite gate and annotation contract"
```

### Task 4: Wire `write` at the composition root

**Files:**
- Modify: `src/eThangAgent.CLI/Program.cs`

**Interfaces:**
- Consumes: `WorkspacePathResolver`, `WriteTool` (Tasks 1, 3); `IWorkspaceContext.WorkspaceId` (already registered as `CwdWorkspaceContext`).
- Produces: model-visible `write` tool via the existing `AgentToolsProvider` bindings (same path as `read`).

- [ ] **Step 1: Add resolver singleton + binding**

In `Program.cs`, next to the existing `IWorkspaceContext`/`AgentToolsProvider` registrations:

```csharp
.AddSingleton<WorkspacePathResolver>(sp =>
    new WorkspacePathResolver(sp.GetRequiredService<IWorkspaceContext>().WorkspaceId))
```

and extend the `AgentToolsProvider` bindings list:

```csharp
new AgentToolBinding(
    new WriteTool(
        sp.GetRequiredService<WorkspacePathResolver>(),
        sp.GetRequiredService<IFileWriteAccess>()),
    "Create or overwrite a workspace file."),
```

with `IFileWriteAccess` registered on the same singleton as the read access:

```csharp
.AddSingleton<IFileWriteAccess>(sp => sp.GetRequiredService<PowerShellFileSystemAccess>())
```

Note the existing registration pattern: `AddSingleton<IFileSystemAccess, PowerShellFileSystemAccess>()` registers the class as a service; forward the new interfaces to that same instance so one runspace serves all operations.

- [ ] **Step 2: Build + verify registration**

Run: `dotnet build`
Expected: success.

Probe (add to `tests/eThangAgent.CLI.Tests` if a wiring test exists; otherwise a temporary console check is acceptable, deleted before commit): resolve `AgentToolsProvider` from a built service provider and assert its `Actions` contains `write` with 3 parameters.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test`
Expected: green.

- [ ] **Step 4: Commit**

```powershell
git add src/eThangAgent.CLI/Program.cs
git commit -m "feat(cli): expose write tool at composition root"
```

---

### Task 5: `edit` backend — `IFileEditAccess` + PowerShell implementation

**Files:**
- Create: `src/eThangAgent.Tool.Domain/IFileEditAccess.cs`
- Create: `src/eThangAgent.Tool.Domain/ReplaceOutcome.cs`
- Modify: `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs`
- Test: `tests/eThangAgent.FileSystem.ACL.Tests/FileEditIntegrationTests.cs`

**Interfaces:**
- Consumes: runspace/semaphore infrastructure.
- Produces: `Task<Result<ReplaceOutcome>> ReplaceInFileAsync(string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)`; `sealed record ReplaceOutcome(int Replaced, int NewLineCount)`. `occurrences == null` means replace every occurrence.

Error codes: `FileNotFound`, `BinaryFile` (NUL byte detected — message says to use exec/PowerShell for binaries), `AnchorNotFound` (zero occurrences; message includes the searched text length), `OccurrenceMismatch` (actual count differs from requested; message states both numbers), `FileSystemError`.

- [ ] **Step 1: Write the failing integration tests**

```csharp
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

public sealed class FileEditIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-e").FullName;
    private readonly PowerShellFileSystemAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var p = Path.Combine(_root, name);
        await File.WriteAllTextAsync(p, content);
        return p;
    }

    [Fact]
    public async Task SingleOccurrence_Replaced_ReportsNewLineCount()
    {
        var p = await WriteAsync("a.txt", "one\ntwo\nthree");
        var r = await _access.ReplaceInFileAsync(p, "two", "TWO", occurrences: 1);
        Assert.True(r.IsSuccess);
        Assert.Equal(1, r.Value!.Replaced);
        Assert.Equal(3, r.Value.NewLineCount);
        Assert.Equal("one\nTWO\nthree", File.ReadAllText(p));
    }

    [Fact]
    public async Task ReplaceAll_NullOccurrences_ReplacesEveryMatch()
    {
        var p = await WriteAsync("b.txt", "x-x-x");
        var r = await _access.ReplaceInFileAsync(p, "x", "y", occurrences: null);
        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Value!.Replaced);
        Assert.Equal("y-y-y", File.ReadAllText(p));
    }

    [Fact]
    public async Task OccurrenceMismatch_RequestedMoreThanExists_Fails()
    {
        var p = await WriteAsync("c.txt", "only-one");
        var r = await _access.ReplaceInFileAsync(p, "one", "1", occurrences: 2);
        Assert.False(r.IsSuccess);
        Assert.Equal("OccurrenceMismatch", r.Error!.Code);
        Assert.Contains("1", r.Error.Message); // actual count
    }

    [Fact]
    public async Task AnchorMissing_Fails_AnchorNotFound()
    {
        var p = await WriteAsync("d.txt", "nothing here");
        var r = await _access.ReplaceInFileAsync(p, "absent", "z", occurrences: 1);
        Assert.False(r.IsSuccess);
        Assert.Equal("AnchorNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task BinaryFile_Fails_BinaryFile()
    {
        var p = Path.Combine(_root, "bin.dat");
        await File.WriteAllBytesAsync(p, [0x01, 0x00, 0x02, 0x03]);
        var r = await _access.ReplaceInFileAsync(p, "a", "b", occurrences: 1);
        Assert.False(r.IsSuccess);
        Assert.Equal("BinaryFile", r.Error!.Code);
    }

    [Fact]
    public async Task MissingFile_Fails_FileNotFound()
    {
        var r = await _access.ReplaceInFileAsync(
            Path.Combine(_root, "ghost.txt"), "a", "b", occurrences: 1);
        Assert.False(r.IsSuccess);
        Assert.Equal("FileNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task ReplacementWithNewline_IncrementsLineCount()
    {
        var p = await WriteAsync("e.txt", "a\nb");
        var r = await _access.ReplaceInFileAsync(p, "b", "b1\nb2\nb3", occurrences: 1);
        Assert.True(r.IsSuccess);
        Assert.Equal(4, r.Value!.NewLineCount);
    }
}
```

- [ ] **Step 2: Run to verify failure** — compile error, `ReplaceInFileAsync` missing.

- [ ] **Step 3: Implement**

`ReplaceOutcome.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed record ReplaceOutcome(int Replaced, int NewLineCount);
```

`IFileEditAccess.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileEditAccess
{
    /// <summary>Literal (non-regex) replacement. When <paramref name="occurrences"/>
    /// is null every occurrence is replaced; otherwise the actual count must equal it.
    /// Refuses binary files. Never creates files.</summary>
    Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default);
}
```

PowerShell script (add as `ReplaceScript`; count occurrences with `Ordinal` comparison, replace via `StringBuilder` loop so a partial count is impossible):

```powershell
private const string ReplaceScript = """
    param([string]$Path, [string]$Old, [string]$New, [int]$Occurrences, [bool]$All)
    if (-not [System.IO.File]::Exists($Path)) {
        return @{ Ok = $false; ErrorCode = "FileNotFound";
                  ErrorMessage = "File not found: $Path" }
    }
    try {
        $text = [System.IO.File]::ReadAllText($Path)
    } catch {
        return @{ Ok = $false; ErrorCode = "FileSystemError";
                  ErrorMessage = $_.Exception.Message }
    }
    if ($text.IndexOf([char]0) -ge 0) {
        return @{ Ok = $false; ErrorCode = "BinaryFile";
                  ErrorMessage = "File appears to be binary (NUL byte found): $Path. Use shell tools for binary files." }
    }
    $count = 0
    $idx = $text.IndexOf($Old, [System.StringComparison]::Ordinal)
    while ($idx -ge 0) { $count++; $idx = $text.IndexOf($Old, $idx + $Old.Length, [System.StringComparison]::Ordinal) }
    if ($count -eq 0) {
        return @{ Ok = $false; ErrorCode = "AnchorNotFound";
                  ErrorMessage = "Anchor text (length $($Old.Length)) not found in $Path." }
    }
    if (-not $All -and $count -ne $Occurrences) {
        return @{ Ok = $false; ErrorCode = "OccurrenceMismatch";
                  ErrorMessage = "Anchor occurs $count time(s) but $Occurrences replacement(s) were requested." }
    }
    $sb = [System.Text.StringBuilder]::new()
    $pos = 0; $done = 0
    $target = if ($All) { $count } else { $Occurrences }
    while ($done -lt $target) {
        $idx = $text.IndexOf($Old, $pos, [System.StringComparison]::Ordinal)
        [void]$sb.Append($text.Substring($pos, $idx - $pos))
        [void]$sb.Append($New)
        $pos = $idx + $Old.Length
        $done++
    }
    [void]$sb.Append($text.Substring($pos))
    $result = $sb.ToString()
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $result, $utf8NoBom)
    $lines = 0
    if ($result.Length -gt 0) {
        $lines = 1
        foreach ($ch in $result.ToCharArray()) { if ($ch -eq "`n") { $lines++ } }
    }
    return @{ Ok = $true; Replaced = $done; NewLineCount = $lines }
    """;
```

Method (same gate/runspace pattern):

```csharp
public async Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
    string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    try
    {
        using var ps = System.Management.Automation.PowerShell.Create(_runspace);
        ps.AddScript(ReplaceScript)
          .AddParameter("Path", path)
          .AddParameter("Old", oldText)
          .AddParameter("New", newText)
          .AddParameter("Occurrences", occurrences ?? 0)
          .AddParameter("All", occurrences is null);
        var output = ps.Invoke();
        if (ps.HadErrors || output.Count == 0)
            return Result<ReplaceOutcome>.Failure(new Error("FileSystemError",
                ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                    ?? "PowerShell script produced no output."));
        var table = (Hashtable)output[0].BaseObject;
        if (table["Ok"] is not true)
            return Result<ReplaceOutcome>.Failure(new Error(
                table["ErrorCode"]?.ToString() ?? "FileSystemError",
                table["ErrorMessage"]?.ToString() ?? "Unknown filesystem error."));
        return Result<ReplaceOutcome>.Success(new ReplaceOutcome(
            Convert.ToInt32(table["Replaced"]!), Convert.ToInt32(table["NewLineCount"]!)));
    }
    finally { _gate.Release(); }
}
```

Update the class declaration to also implement `IFileEditAccess`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/eThangAgent.FileSystem.ACL.Tests --filter FileEditIntegrationTests`
Expected: PASS (all 7).

- [ ] **Step 5: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/IFileEditAccess.cs src/eThangAgent.Tool.Domain/ReplaceOutcome.cs src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs tests/eThangAgent.FileSystem.ACL.Tests/FileEditIntegrationTests.cs
git commit -m "feat(fs-acl): literal in-file replacement with occurrence gate and binary refusal"
```

---

### Task 6: `edit` tool — strict input + format contract

**Files:**
- Create: `src/eThangAgent.Tool.Domain/EditToolInput.cs`
- Create: `src/eThangAgent.Tool.Domain/EditTool.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/EditToolTests.cs`

**Interfaces:**
- Consumes: `WorkspacePathResolver` (Task 1), `IFileEditAccess` (Task 5).
- Produces: `ITool` named `edit`. JSON args: `path` (string, required), `old` (string, required, non-empty), `new` (string, required, may be empty = deletion), and **exactly one of** `all` (boolean true) or `occurrences` (integer ≥ 1); providing both is a validation error. Output contract: `[edit <path>] replaced N occurrence(s), file now M lines`.

- [ ] **Step 1: Write the failing tests**

Mirror `WriteToolTests` structure (fake `IFileEditAccess`, fixed synthetic resolver root). Required cases:

1. Missing `path` / `old` / `new` → `MissingParameter` naming the field.
2. `old` empty string → `InvalidParameterValue` (anchor must be non-empty).
3. Neither `all` nor `occurrences` → error explaining exactly one is required.
4. Both `all:true` and `occurrences:2` → validation error naming both.
5. `occurrences:0` → `InvalidParameterValue` (must be ≥ 1).
6. `occurrences` as string / `all` as string → `InvalidParameterType`.
7. Unknown parameter rejected.
8. Path outside workspace → `PathOutsideWorkspace` surfaced.
9. Success with `all:true` → `[edit <path>] replaced 3 occurrence(s), file now 5 lines`.
10. Success with `occurrences:1` → same contract, `1 occurrence`.
11. Backend `AnchorNotFound` / `OccurrenceMismatch` errors surfaced verbatim with `Error [Code]:` prefix.

Write real assertions for each (copy the assertion style from Task 3's tests — `Assert.True(result.IsError)` + `Assert.Contains` on code fragments; success cases assert the full annotation line with the resolved path).

- [ ] **Step 2: Run to verify failure** — compile error.

- [ ] **Step 3: Implement `EditToolInput`**

Same skeleton as `WriteToolInput` (JSON parse → object → unknown rejection with allowed list `path, old, new, all, occurrences`), plus the exactly-one rule:

```csharp
var hasAll = json.TryGetProperty("all", out var allEl);
var hasOcc = json.TryGetProperty("occurrences", out var occEl);
if (hasAll == hasOcc)
    return Fail(new Error("InvalidParameterValue",
        "Provide exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
```

Type checks: `all` must be `JsonValueKind.True` (only `true` is meaningful; `false` is rejected with `InvalidParameterValue` explaining the exactly-one rule); `occurrences` must be integer ≥ 1.

- [ ] **Step 4: Implement `EditTool`**

Description (verbatim in the definition):

> "Edit a text file by exact literal replacement. path, old, and new are mandatory; then provide exactly one of all (boolean true — replace every occurrence) or occurrences (integer ≥ 1 — expected match count; the call fails if the actual count differs, naming both numbers). old must appear verbatim — no regex, no whitespace normalization. The file is never created. Binary files are refused. Output is a single annotation line: `[edit <path>] replaced N occurrence(s), file now M lines`. Errors begin with `Error [Code]:` and are safe to retry with corrected arguments."

Execution flow: parse input → resolve path → `ReplaceInFileAsync(resolved, old, new, occurrencesOrNull, ct)` → format `[edit {resolved}] replaced {o.Replaced} occurrence(s), file now {o.NewLineCount} lines` (singularize when 1: `1 occurrence`). Errors as in Task 3.

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter EditToolTests`
Expected: PASS (all 11+).

- [ ] **Step 6: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/EditToolInput.cs src/eThangAgent.Tool.Domain/EditTool.cs tests/eThangAgent.Tool.Domain.Tests/EditToolTests.cs
git commit -m "feat(tools): literal edit tool with exactly-one replacement selector"
```

### Task 7: Wire `edit` at the composition root

**Files:**
- Modify: `src/eThangAgent.CLI/Program.cs`

- [ ] **Step 1: Register interface + binding**

```csharp
.AddSingleton<IFileEditAccess>(sp => sp.GetRequiredService<PowerShellFileSystemAccess>())
```

```csharp
new AgentToolBinding(
    new EditTool(
        sp.GetRequiredService<WorkspacePathResolver>(),
        sp.GetRequiredService<IFileEditAccess>()),
    "Edit a file by exact literal replacement."),
```

- [ ] **Step 2: Build + full suite**

Run: `dotnet build && dotnet test`
Expected: green.

- [ ] **Step 3: Commit**

```powershell
git add src/eThangAgent.CLI/Program.cs
git commit -m "feat(cli): expose edit tool at composition root"
```

---

### Task 8: `search` backend — `ISearchAccess` + PowerShell implementation

**Files:**
- Create: `src/eThangAgent.Tool.Domain/FileSearch.cs`
- Create: `src/eThangAgent.Tool.Domain/ISearchAccess.cs`
- Modify: `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs`
- Test: `tests/eThangAgent.FileSystem.ACL.Tests/FileSearchIntegrationTests.cs`

**Interfaces:**
- Consumes: runspace/semaphore infrastructure.
- Produces:

```csharp
public sealed record SearchMatch(string Path, int LineNumber, IReadOnlyList<string> Lines);
public sealed record FileSearch(IReadOnlyList<SearchMatch> Matches, bool Truncated, int FilesScanned);

public interface ISearchAccess
{
    Task<Result<FileSearch>> SearchFilesAsync(
        string rootPath, string pattern, bool regex, string? glob,
        int maxResults, int contextLines, CancellationToken ct = default);
}
```

`Matches` holds at most `maxResults` entries; `Truncated=true` means at least one further file containing a match existed after the cap was reached (truncation granularity is whole files — a file entered is finished). `FilesScanned` counts every text file examined, including when zero matches.

Error codes: `InvalidPattern` (regex fails to compile, message includes the regex error), `RootNotFound` (rootPath missing), `FileSystemError`.

- [ ] **Step 1: Write the failing integration tests**

```csharp
using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

public sealed class FileSearchIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-s").FullName;
    private readonly PowerShellFileSystemAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<string> WriteAsync(string relative, string content)
    {
        var p = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, content);
        return p;
    }

    [Fact]
    public async Task LiteralMatch_ReportsPathLineAndText()
    {
        await WriteAsync("src\\a.cs", "alpha\nbeta\ngamma");
        var r = await _access.SearchFilesAsync(_root, "beta", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.EndsWith("a.cs", m.Path);
        Assert.Equal(2, m.LineNumber);
        Assert.Equal("beta", m.Lines[0].Trim());
    }

    [Fact]
    public async Task ContextLines_IncludesNeighbors()
    {
        await WriteAsync("b.txt", "one\ntwo\nthree\nfour");
        var r = await _access.SearchFilesAsync(_root, "two", regex: false, glob: null, maxResults: 50, contextLines: 1);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.Equal(3, m.Lines.Count);
        Assert.Equal("one", m.Lines[0].Trim());
        Assert.Equal("three", m.Lines[2].Trim());
    }

    [Fact]
    public async Task RegexMode_MatchesPattern()
    {
        await WriteAsync("c.txt", "foo123\nbar\nfoo456");
        var r = await _access.SearchFilesAsync(_root, "foo\\d+", regex: true, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Equal(2, r.Value!.Matches.Count);
    }

    [Fact]
    public async Task InvalidRegex_Fails_InvalidPattern()
    {
        var r = await _access.SearchFilesAsync(_root, "foo(", regex: true, glob: null, maxResults: 50, contextLines: 0);
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidPattern", r.Error!.Code);
    }

    [Fact]
    public async Task GitDirectory_Skipped()
    {
        await WriteAsync(".git\\tracked.txt", "secret-token");
        await WriteAsync("real.txt", "secret-token");
        var r = await _access.SearchFilesAsync(_root, "secret-token", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.EndsWith("real.txt", m.Path);
    }

    [Fact]
    public async Task BinaryFiles_Skipped()
    {
        await WriteAsync("bin.dat", "x\0y"); // NUL byte
        var r = await _access.SearchFilesAsync(_root, "x", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Matches);
    }

    [Fact]
    public async Task GlobFilter_RestrictsFiles()
    {
        await WriteAsync("keep.cs", "needle");
        await WriteAsync("skip.md", "needle");
        var r = await _access.SearchFilesAsync(_root, "needle", regex: false, glob: "*.cs", maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.EndsWith("keep.cs", m.Path);
    }

    [Fact]
    public async Task MaxResults_TruncatesWithFlag()
    {
        for (var i = 0; i < 5; i++)
            await WriteAsync($"f{i}.txt", "hit");
        var r = await _access.SearchFilesAsync(_root, "hit", regex: false, glob: null, maxResults: 3, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Value!.Matches.Count);
        Assert.True(r.Value.Truncated);
    }

    [Fact]
    public async Task NoMatches_ReportsFilesScanned()
    {
        await WriteAsync("z.txt", "nothing relevant");
        var r = await _access.SearchFilesAsync(_root, "absent-token", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Matches);
        Assert.False(r.Value.Truncated);
        Assert.Equal(1, r.Value.FilesScanned);
    }

    [Fact]
    public async Task MissingRoot_Fails_RootNotFound()
    {
        var r = await _access.SearchFilesAsync(Path.Combine(_root, "ghost"), "x", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.False(r.IsSuccess);
        Assert.Equal("RootNotFound", r.Error!.Code);
    }
}
```

- [ ] **Step 2: Run to verify failure** — compile error.

- [ ] **Step 3: Implement domain types** (`FileSearch.cs` exactly as the interface block above; `ISearchAccess.cs` with the interface + doc comment).

- [ ] **Step 4: Implement the PowerShell script** (`SearchScript`) and method. Key script requirements:

- Enumerate `[System.IO.Directory]::EnumerateFiles($Root, "*", [System.IO.SearchOption]::AllDirectories)` lazily; skip path segments named `.git`.
- Apply glob on the file *name* with `-like $Glob` when `$Glob` is set.
- Read text; if the first 1024 bytes contain a NUL byte, skip as binary.
- Literal mode: `$line.Contains($Pattern, [System.StringComparison]::Ordinal)`. Regex mode: `[System.Text.RegularExpressions.Regex]::new($Pattern, 'None', [TimeSpan]::FromSeconds(2))` — a compile failure must surface as `InvalidPattern` (catch in script, return error hashtable with the regex message); a match timeout surfaces as `FileSystemError`.
- Collect matches as hashtables `@{ Path; Line; Lines }` where `Lines` is the context window clamped to file bounds.
- Stop enumeration once `Matches.Count` reaches `$Max` **after finishing the current file**; then peek one more `MoveNext()` to set `Truncated`.
- Count every text file read into `Files`.
- Return `@{ Ok = $true; Matches = $matches; Truncated = $truncated; Files = $files }`.

C# method maps `Matches` entries into `SearchMatch` records (PSObject-unwrapping identical to the existing `ReadLinesAsync` pattern) and enforces nothing extra — the script owns the cap.

Update the class declaration to also implement `ISearchAccess`.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/eThangAgent.FileSystem.ACL.Tests --filter FileSearchIntegrationTests`
Expected: PASS (all 10).

- [ ] **Step 6: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/FileSearch.cs src/eThangAgent.Tool.Domain/ISearchAccess.cs src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs tests/eThangAgent.FileSystem.ACL.Tests/FileSearchIntegrationTests.cs
git commit -m "feat(fs-acl): bounded workspace search with truncation accounting"
```

### Task 9: `search_files` tool — strict input + format contract

**Files:**
- Create: `src/eThangAgent.Tool.Domain/SearchToolInput.cs`
- Create: `src/eThangAgent.Tool.Domain/SearchTool.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/SearchToolTests.cs`

**Interfaces:**
- Consumes: `WorkspacePathResolver` (Task 1), `ISearchAccess` (Task 8).
- Produces: `ITool` named `search_files`. JSON args: `pattern` (string, required, non-empty), `mode` (string enum, required, exactly `Literal` or `Regex` — ordinal comparison), `path` (string, optional, default workspace root — documented in description, not silent coercion of a missing value into something else), `glob` (string, optional, non-empty when present), `maxResults` (integer, required, ≥ 1; values above the cap clamp to it with a visible warning — the sanctioned leniency), `contextLines` (integer, optional, default 0, ≥ 0).

Cap constant: `SearchToolInput.MaxResultsCap = 200`. Clamping is reported: `Create` returns the clamped value plus `bool Clamped` on the record.

Output contract:

```text
[search 'pattern' under <root>: N match(es) across M file(s), K files scanned]
--- relative/path/file.ext ---
 12→ matched line text
 13→ context line text
[warning] results capped at 200 matches; narrow with pattern/path/glob to see more
```

Empty result set collapses to `[search ...] no matches (K files scanned)`.

- [ ] **Step 1: Write the failing tests**

Cases (fakes over `ISearchAccess`, synthetic resolver root as in Tasks 3/6):

1. Missing `pattern` → `MissingParameter`.
2. Missing `mode` / `mode` not exactly `Literal|Regex` (e.g. `"literal"`, `"bogus"`) → error listing allowed values.
3. Missing `maxResults` / zero / negative → errors.
4. `maxResults: 500` → accepted, clamped to 200, `Clamped=true` (assert via tool output containing `results capped at 200`).
5. `contextLines: -1` → error; absent → defaults 0 (assert backend fake receives 0).
6. Unknown parameter rejected.
7. Path outside workspace → `PathOutsideWorkspace`.
8. Success formatting: two files, one match each, context lines → annotation header counts (`2 match(es) across 2 file(s)`), per-file gutters with `path:line` style shown above, no trailing warning.
9. Truncated result → `[warning] results capped at N` present.
10. Empty result → single `no matches` line with scanned count.
11. Backend `InvalidPattern` surfaced verbatim.

The fake captures constructor-injected arguments for assertion (a small `CapturedCall` record on the fake: RootPath, Pattern, Regex, Glob, MaxResults, ContextLines).

- [ ] **Step 2: Run to verify failure** — compile error.

- [ ] **Step 3: Implement `SearchToolInput`**

Same house skeleton. Mode parsing:

```csharp
if (!json.TryGetProperty("mode", out var modeEl)) return Missing("mode");
if (modeEl.ValueKind != JsonValueKind.String) return WrongType("mode", "string", modeEl.ValueKind);
var modeRaw = modeEl.GetString()!;
var regex = modeRaw switch
{
    "Literal" => false,
    "Regex" => true,
    _ => (bool?)null,
};
if (regex is null)
    return Fail(new Error("InvalidParameterValue",
        $"'mode' must be exactly \"Literal\" or \"Regex\" (got \"{modeRaw}\")."));
```

Clamp logic:

```csharp
if (!json.TryGetProperty("maxResults", out var maxEl)) return Missing("maxResults");
if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out var max))
    return WrongType("maxResults", "integer", maxEl.ValueKind);
if (max < 1)
    return Fail(new Error("InvalidParameterValue", "'maxResults' must be ≥ 1."));
int clampedMax = Math.Min(max, MaxResultsCap);
```

Record shape: `SearchToolInput(string Pattern, bool Regex, string? Path, string? Glob, int MaxResults, int ContextLines, bool Clamped)`.

- [ ] **Step 4: Implement `SearchTool`**

Description (verbatim):

> "Search workspace text files for a pattern. pattern, mode, and maxResults are mandatory; mode is exactly 'Literal' or 'Regex'; path optionally scopes to a subdirectory (defaults to the workspace root); glob optionally filters filenames like '*.cs'. maxResults above 200 clamps to 200 with a visible warning rather than failing. Binary files and .git contents are skipped. Output begins with an annotation line `[search ...]` giving match and scan counts; each matching file follows under a `--- path ---` header with line-numbered, arrow-prefixed lines (numbers and arrows are never part of the content). A trailing `[warning]` means more matches exist beyond the cap. Errors begin with `Error [Code]:`."

Execution flow: parse → resolve `path ?? "."` against resolver → call backend → format header from outcome (`Matches.Count`, distinct file count of matches, `FilesScanned`), per-file blocks (relative-to-root paths using `Path.GetRelativePath(resolver-root, match.Path)`), guttered lines padded like `ReadTool`, optional truncation warning. Empty case as specified. Errors as before.

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter SearchToolTests`
Expected: PASS (all 11+).

- [ ] **Step 6: Commit**

```powershell
git add src/eThangAgent.Tool.Domain/SearchToolInput.cs src/eThangAgent.Tool.Domain/SearchTool.cs tests/eThangAgent.Tool.Domain.Tests/SearchToolTests.cs
git commit -m "feat(tools): bounded search_files tool with visible clamp leniency"
```

---

### Task 10: Wire `search_files`, README, full verification

**Files:**
- Modify: `src/eThangAgent.CLI/Program.cs`
- Modify: `README.md`

- [ ] **Step 1: Register interface + binding**

```csharp
.AddSingleton<ISearchAccess>(sp => sp.GetRequiredService<PowerShellFileSystemAccess>())
```

```csharp
new AgentToolBinding(
    new SearchTool(
        sp.GetRequiredService<WorkspacePathResolver>(),
        sp.GetRequiredService<ISearchAccess>()),
    "Search workspace text files with literal or regex patterns."),
```

- [ ] **Step 2: Update README**

In the "What it can do today" list, after the `read` bullet add:

```markdown
- `write` tool — create/replace files behind an explicit overwrite gate
- `edit` tool — exact literal replacements with occurrence verification
- `search_files` tool — bounded workspace search (literal or regex, glob-filtered)
```

- [ ] **Step 3: Full verification**

Run: `dotnet build && dotnet test`
Expected: entire solution green — all new unit suites (Tasks 1, 3, 6, 9), integration suites (Tasks 2, 5, 8), and every pre-existing suite untouched and passing.

Manual probe (not committed): launch `dotnet run --project src/eThangAgent.CLI` in a scratch directory with a scripted/mock provider if available, or at minimum confirm startup logs expose five agent tools (`read`, `write`, `edit`, `search_files`, `exec`).

- [ ] **Step 4: Commit**

```powershell
git add src/eThangAgent.CLI/Program.cs README.md
git commit -m "feat(cli): expose search_files tool; document file manipulation tools"
```

---

## Plan Self-Review

- **Spec coverage:** SP1 section of the spec requires write (explicit overwrite, UTF-8 no BOM, no parent auto-create, escape rejection) → Tasks 2–4; edit (exactly-one selector, occurrence gate, binary refusal, anchor-must-exist) → Tasks 5–7; search (required mode enum, documented default scope, hard cap with visible clamp, .git/binary skip, continuation accounting, gutter contract) → Tasks 8–10. All spec bullets map to a task; nothing extra invented beyond the ISP decision recorded below.
- **Deliberate deviation from first draft, documented:** the spec said extend `IFileSystemAccess`; the plan introduces three segregated interfaces instead so six unrelated test fakes stay valid and each seam stays individually swappable. This strengthens the spec's seam rationale and touches nothing existing.
- **Placeholder scan:** Task 6 Step 1 enumerates cases precisely but leaves assertion code to the executor by explicit instruction (with exact style source); every other step carries full code. No TBDs.
- **Type consistency:** `FileWriteOutcome(Created, BytesWritten)`, `ReplaceOutcome(Replaced, NewLineCount)`, `SearchMatch(Path, LineNumber, Lines)`, `FileSearch(Matches, Truncated, FilesScanned)`, `IFileWriteAccess.WriteFileAsync`, `IFileEditAccess.ReplaceInFileAsync`, `ISearchAccess.SearchFilesAsync`, `WorkspacePathResolver.Resolve` — used identically across tasks.
