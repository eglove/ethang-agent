# Replace PowerShell with Direct .NET I/O and C# Scripting

**Date:** 2026-08-22
**Status:** Draft
**Branch:** (to be created)

## Summary

Remove all `System.Management.Automation` dependencies from the agent. Two independent work streams compose to eliminate PowerShell entirely:

1. **File I/O & Git ACLs** — replace `PowerShellFileSystemAccess` and `PowerShellGitAccess` with direct BCL implementations. Eliminates the single-file publish crash (null PSHOME probe in `RunspaceHost.CreateOpen()`) at the file-I/O layer.

2. **Exec engine** — replace `PowerShellExecEngine` with a Roslyn C# scripting engine. The `exec` tool accepts C# programs instead of PowerShell programs. `PsEvidenceRunner` becomes `CSharpEvidenceRunner`. The `PowerShell.ACL` project is deleted.

These are independent: (1) can ship before (2), and (1) alone fixes the crash because the runspace host is never constructed for file operations. (2) completes the untangling by removing the last PowerShell dependency.

## Architecture

### Current dependency graph

```
Tool.Domain (IExecEngine, IExecActivitySink, ExecTool, ExecOptions, ExecGuide)
    ^
PowerShell.ACL (PowerShellExecEngine, PsEvidenceRunner, ToolBroker, ...)
    ^                    ^
FileSystem.ACL        CLI (DI wiring, prompt)
(PowerShellFileSystemAccess,
 PowerShellGitAccess)
```

### After

```
Tool.Domain (unchanged — IExecEngine, IExecActivitySink, ExecTool, ExecOptions, ExecGuide rewritten)
    ^
Roslyn.ACL (CSharpScriptExecEngine, CSharpEvidenceRunner, ScriptGlobals)
    ^
CLI (DI wiring, prompt updated)

FileSystem.ACL (DirectFileSystemAccess, DirectGitAccess — pure BCL, zero shell)
```

No domain contracts change. `IExecEngine`, `IEvidenceRunner`, `IFileSystemAccess`, `IGitQueryAccess`, `IGitCommitAccess`, `IExecActivitySink`, `ExecOptions`, `ExecToolInput`, `ExecProgram`, `ExecRunResult`, `ExecRunStatus`, `ExecParseError`, `ExecActivity`, `ExecResultFormatter` — all stay exactly as they are.

## Work Stream 1: Direct File I/O and Git

### Overview

Two new classes in `FileSystem.ACL` implement the same domain interfaces using only `System.IO`, `System.Text`, and `System.Text.RegularExpressions`. The old `PowerShellFileSystemAccess` and `PowerShellGitAccess` are deleted. The `FileSystem.ACL` project reference to `PowerShell.ACL` is removed.

### DirectFileSystemAccess

Implements: `IFileSystemAccess`, `IFileWriteAccess`, `IFileEditAccess`, `ISearchAccess`, `IDisposable`

Each method is a direct, linear translation of the PowerShell script it replaces:

- **ReadLinesAsync(path, startLine, endLine):** Open a `StreamReader`, read line by line, collect lines in range into a `List<string>`, count total lines. Return `FileRead`. No runspace, no gate semaphore, no Hashtable parsing.

- **WriteFileAsync(path, content, overwrite):** If file exists and overwrite is false, return `FileExists` error. If parent directory doesn't exist, return `DirectoryNotFound` error. Otherwise `File.WriteAllTextAsync(path, content, new UTF8Encoding(false))`. Return `FileWriteOutcome`.

- **ReplaceInFileAsync(path, oldText, newText, occurrences):** Read all text, check for NUL bytes (binary rejection), count occurrences, validate vs expected count, build replacement via `StringBuilder`, write result. Return `ReplaceOutcome`.

- **SearchFilesAsync(root, pattern, regex, glob, max, context):** `Directory.EnumerateFiles` loop, skip binary files, match lines via literal or `Regex` with 2s timeout, collect matches with context window. Return `FileSearch`.

**Error mapping:** The error codes stay identical — `FileNotFound`, `FileExists`, `AnchorNotFound`, `OccurrenceMismatch`, `BinaryFile`, `InvalidPattern`, `RootNotFound`, `FileSystemError`. Same codes, same messages, same `Result<T>` shape.

### DirectGitAccess

Implements: `IGitQueryAccess`, `IGitCommitAccess`, `IDisposable`

Same translation pattern. Git commands become `Process.Start` calls capturing stdout/stderr with a timeout. Same `git.exe`, same args — just no PowerShell intermediary. No LibGit2Sharp.

### Testing

- Unit tests using temp directories. Assert correct error codes, content, line counts.
- Existing `PowerShell.ACL.Tests` and `FileSystem.ACL.Tests` coverage ported or removed.

### Files

| Action | Path |
| -------- | ------ |
| Create | `src/eThangAgent.FileSystem.ACL/DirectFileSystemAccess.cs` |
| Create | `src/eThangAgent.FileSystem.ACL/DirectGitAccess.cs` |
| Delete | `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs` |
| Delete | `src/eThangAgent.FileSystem.ACL/PowerShellGitAccess.cs` |
| Modify | `src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj` |
| Keep | `src/eThangAgent.FileSystem.ACL/ExecArtifactStore.cs` |
| Modify | `src/eThangAgent.CLI/Program.cs` (DI registrations) |

## Work Stream 2: Roslyn C# Scripting Engine

### Overview

A new `eThangAgent.Roslyn.ACL` project replaces `eThangAgent.PowerShell.ACL` entirely. It implements `IExecEngine` and `IEvidenceRunner` using `Microsoft.CodeAnalysis.CSharp.Scripting`.

### CSharpScriptExecEngine

Implements: `IExecEngine`

1. Create a `ScriptGlobals` instance holding registry, options, workspace path.
2. Build a `CSharpScript.Create(program.Text)` with `ScriptOptions.Default` importing `System`, `System.IO`, `System.Linq`, `System.Collections.Generic`, `System.Diagnostics`, `System.Text`, `System.Text.RegularExpressions`.
3. **ValidateAsync:** compile (not run), return `Diagnostic` errors as `ExecParseError`s.
4. **ExecuteAsync:** run with timeout. Capture return value: `null`/`void` = empty output, `string` = verbatim, other = JSON. Exceptions become error lines.

**Tool injection:** `ScriptGlobals` is the Roslyn host object — its public members are top-level identifiers in the script:

```csharp
var content = Tools.read(new { path = "src/main.cs", startLine = 1, endLine = 50 });
return content;
```

### ScriptGlobals (model-facing API)

| Member | Type | Description |
| -------- | ------ | ------------- |
| `Workspace` | `string` | Workspace root path |
| `Temp` | `string` | Temp directory for artifacts |
| `Shell(string exe, params string[] args)` | `ShellResult` | Run external process |
| `Output(object? value)` | `void` | Emit a line to output without ending |
| `Tools` | `ScriptTools` | Tool-calling surface |
| `State` | `ScriptState` | Key-value state |

### ScriptTools

| Method | Description |
| -------- | ------------- |
| `List()` | Tool names as string |
| `Describe(string name)` | Full tool description |
| `Invoke(string name, object args)` | Raw tool call, returns string |
| Per-tool methods | Generated: `read(object args)`, `write(object args)`, etc. |

### ExecGuide rewrite

C# replaces PowerShell. Same structure, C# idioms:

- Tool calling: `Tools.read(new { path = "src/main.cs", startLine = 1, endLine = 50 })`
- Shell commands: `Shell("dotnet", "build")`
- LINQ replaces piping: `Directory.EnumerateFiles(Workspace, "*.cs", SearchOption.AllDirectories).Count()`
- Error handling: `try { ... } catch (Exception ex) { Output(ex.Message); return; }`
- Same artifact truncation, timeout rules, no-nested-exec rule

### Prompt guidance

`SkillsBootstrapPromptProvider.ToolMapping`: "exec (PowerShell only)" becomes "exec (C# scripting)".
`ExecTool.Definition.Description`: rewritten for C#.

### Testing

- Unit tests with real Roslyn: valid programs, compile errors, runtime errors, timeout, fake `Shell()`/`Tools`.
- Integration: `Roslyn.ACL.Tests` against fake capability registry.
- E2E: CLI tests through full pipeline.

### Files

| Action | Path |
| -------- | ------ |
| Create | `src/eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj` |
| Create | `src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs` |
| Create | `src/eThangAgent.Roslyn.ACL/ScriptGlobals.cs` |
| Create | `src/eThangAgent.Roslyn.ACL/CSharpEvidenceRunner.cs` |
| Delete | `src/eThangAgent.PowerShell.ACL/` (entire project) |
| Modify | `src/eThangAgent.Tool.Domain/ExecGuide.cs` |
| Modify | `src/eThangAgent.Tool.Domain/ExecTool.cs` |
| Modify | `src/eThangAgent.CLI/Program.cs` |
| Modify | `src/eThangAgent.CLI/SkillsBootstrapPromptProvider.cs` |
| Modify | `src/eThangAgent.Capability.Domain/CapabilityNameRules.cs` |
| Modify | `eThangAgent.slnx` |
| Create | `tests/eThangAgent.Roslyn.ACL.Tests/` |
| Delete | `tests/eThangAgent.PowerShell.ACL.Tests/` |

## Cross-Cutting Concerns

### Single-file publish

After both work streams: zero dependency on `System.Management.Automation`. No runspace created. No PSHOME probe. Single-file exe works immediately.

### Memory and startup

`DirectFileSystemAccess` and `DirectGitAccess` are lightweight. `CSharpScriptExecEngine` carries Roslyn scripting (~15 MB) vs PowerShell SDK (~50 MB). Net publish size decreases.

### Model fluency risk

Primary risk: C# scripting is more verbose than PowerShell for ad-hoc queries. Mitigations: tool injection provides short aliases, common imports pre-included, `Shell()` escape hatch, worked examples in `ExecGuide`, iterative `ScriptGlobals` helper additions without contract changes.

## Acceptance Criteria

1. `dotnet build` succeeds; `dotnet test` passes all test projects.
2. Published single-file exe starts without crash.
3. `exec` tool accepts and runs C# programs.
4. `Shell("dotnet", "--version")` returns the dotnet version.
5. `Tools.read(...)` returns file contents.
6. File I/O tools produce identical results to before.
7. Git tools produce identical results to before.
8. Model prompt includes C# `ExecGuide` — no PowerShell guidance remains.
9. No `System.Management.Automation` reference exists in the solution.

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
| ------ | ----------- | -------- | ------------ |
| Model writes invalid C# more often than invalid PowerShell | Medium | Medium | Pre-import namespaces; ExecGuide with examples; iterative globals |
| Roslyn scripting startup slower than PowerShell runspace | Low | Low | Delegates cached; PowerShell runspace also heavyweight |
| Git process spawning differs from PowerShell-wrapped git | Low | Medium | Same git.exe, same args, identical capture |
| Evidence runner change breaks state transitions | Low | Medium | Same IEvidenceRunner interface; boolean C# = boolean PowerShell |
