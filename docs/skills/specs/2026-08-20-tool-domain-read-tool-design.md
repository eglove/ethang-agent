# Tool Domain + `read` Tool — Design Specification

**Date:** 2026-08-20
**Status:** Draft
**Milestone:** 2 — Tool Use (end-to-end)

## Goal

Introduce the Tool Domain and its first built-in tool, `read`, and wire tool calling end-to-end: tool definitions flow to OpenRouter, the model's tool calls are validated strictly, executed, and their results fed back into the conversation until the model produces a final answer.

**Core philosophy — strictly correct input.** `read` never allows omitted parameters, never applies defaults, never coerces types, and never accepts unknown parameters. The single deliberate exception is `endLine` overshooting the file length, which clamps with a visible warning. Every rejection returns a model-actionable error so the LLM can self-correct on the next attempt.

## Design Decisions

| Decision | Choice |
| --- | --- |
| Scope | End-to-end: tool domain + OpenRouter tool-calling loop in one milestone |
| Invalid-parameter policy | Strict reject with actionable errors; **only** `endLine` > file length clamps (with warning) |
| Output format | Line-numbered gutter (`N→`) with bracketed annotation lines; format contract lives in the tool description |
| Range cap | 1000 lines per call; larger ranges rejected with chunking advice |
| File I/O hosting | In-process PowerShell runspace (`System.Management.Automation`, PowerShell 7.x) — no process spawn |
| Line numbering | 1-based, inclusive |

## Project Structure

```text
src/
├── eThangAgent.SharedKernel/            # (existing) Result<T>, Error
├── eThangAgent.Tool.Domain/             # NEW: ITool, ToolDefinition, ToolResult, ReadTool, IFileSystemAccess, IToolRegistry
├── eThangAgent.FileSystem.ACL/          # NEW: PowerShell-runspace IFileSystemAccess implementation
├── eThangAgent.Conversation.Domain/     # MODIFIED: ToolCall, Role.Tool, tool-result messages
├── eThangAgent.Model.Domain/            # MODIFIED: tool-aware IModelProvider contract
├── eThangAgent.Agent.Domain/            # MODIFIED: agent tool loop
├── eThangAgent.Agent.Application/       # MODIFIED: handler passes through to new loop
├── eThangAgent.OpenRouter.ACL/          # MODIFIED: tools/tool-calls wire format
└── eThangAgent.CLI/                     # MODIFIED: composition root wiring
```

### Dependency graph (top depends on below)

```text
CLI → Agent.Application → Agent.Domain → { Conversation.Domain, Model.Domain, Tool.Domain }
CLI → OpenRouter.ACL → Model.Domain
CLI → FileSystem.ACL → Tool.Domain
CLI → Tool.Domain
Model.Domain → Tool.Domain (for ToolDefinition)
{ Tool.Domain, Conversation.Domain, Model.Domain } → SharedKernel
```

`Model.Domain` references `ToolDefinition` from `Tool.Domain` — a published contract type, the same direction as the existing `IModelProvider` seam. The domain never sees PowerShell, JSON wire formats, or HTTP.

Note: this is the **File System ACL** (file I/O) *implemented with* PowerShell hosting technology. The **PowerShell ACL** for arbitrary shell execution remains a future, separate project.

## Core Types

### Tool.Domain

```csharp
public enum ToolParameterType { String, Integer }

public sealed record ToolParameter(string Name, ToolParameterType Type, string Description, int? Minimum = null);

public sealed record ToolDefinition(string Name, string Description, IReadOnlyList<ToolParameter> Parameters);

public sealed record RawToolInput(string Name, string JsonArguments);

public sealed record ToolResult(string Content, bool IsError);

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default);
}

public interface IToolRegistry
{
    ITool? Find(string name);
    IReadOnlyList<ToolDefinition> Definitions { get; }
}
```

`ToolRegistry` is an in-memory implementation constructed from `IEnumerable<ITool>`.

The File System seam (owned by Tool.Domain, implemented by the ACL):

```csharp
public sealed record FileRead(IReadOnlyList<string> Lines, int LastLineRead, int TotalLines);
// TotalLines is always populated: enumeration drains to EOF so the line count is exact.

public interface IFileSystemAccess
{
    Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default);
}
```

### ReadTool contract

Parameters — all three mandatory, no defaults:

| Parameter | Type | Rules |
| --- | --- | --- |
| `path` | string | non-empty; resolved relative to the agent working directory |
| `startLine` | integer | ≥ 1 |
| `endLine` | integer | ≥ 1, ≥ `startLine` |

Validation order (each failure → `ToolResult` with `IsError = true`):

1. Malformed JSON arguments → exact parse problem.
2. Missing parameter → names the missing one and the full required set. Never defaults.
3. Wrong type (`"12"`, `12.5`, `true`) → names the parameter, expected vs actual type. **No coercion.**
4. Unknown extra parameters → lists them and the allowed set.
5. `startLine < 1`, or `startLine > endLine` → shows both values.
6. Range > 1000 lines → `range spans N lines; maximum is 1000. Read in chunks (e.g. 1-1000, 1001-2000).`
7. File not found → `File not found: {path}`.
8. `startLine` beyond EOF (including empty files) → `startLine {n} exceeds file length ({total} lines).`
9. `endLine` beyond EOF → **clamp** to file length, succeed, append `[warning] endLine {requested} exceeded file length ({total}); clamped`.

Output format:

```text
[read src/Program.cs lines 10-12 of 213 total]
  10→ using System;
  11→
  12→ namespace Demo;
[warning] endLine 5000 exceeded file length (213); clamped
```

- Annotation lines are bracketed; the gutter is right-aligned to the width of the last line number, separator `→`.
- The warning line, when present, is the last line.

Tool description (verbatim — this is the format contract the model sees):

> Read a range of lines from a text file. path, startLine, and endLine are all mandatory; line numbers are 1-based and inclusive. Output begins with an annotation line in [brackets] — it is metadata, not file content. Each content line is prefixed with its line number and →; the number and arrow are never part of the file. Never reproduce line numbers or arrows when creating or editing files. Cite line numbers as shown when referencing locations. If endLine exceeds the file length it is clamped and a [warning] is appended. Maximum range: 1000 lines per call.

Input parsing uses `ReadToolInput.Create(raw)` returning `Result<ReadToolInput>` — the same factory-validation pattern as `ModelConfig`.

### Conversation.Domain (modifications)

```csharp
public enum Role { User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, string Arguments);

public sealed record Message(
    Role Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);
```

- `Conversation.AddAssistantMessage(string content, IReadOnlyList<ToolCall>? toolCalls = null)`
- `Conversation.AddToolResult(string toolCallId, string content)` — appends a `Role.Tool` message.

### Model.Domain (modifications)

```csharp
public sealed record ModelRequest(IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition>? Tools = null);

public sealed record ToolCallRequest(string Id, string Name, string Arguments);

public sealed record ModelResponse(string? Content, IReadOnlyList<ToolCallRequest> ToolCalls);

public interface IModelProvider
{
    Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default);
}
```

Breaking change to the existing single-prompt signature; all callers updated.

### FileSystem.ACL

`PowerShellFileSystemAccess : IFileSystemAccess`:

- `System.Management.Automation` NuGet package (PowerShell 7.x assemblies; Windows PowerShell 5.1 assemblies do not load in .NET 10).
- One `Runspace` created at construction, reused for every call; guarded by `SemaphoreSlim(1,1)` (runspaces are not thread-safe).
- One streaming pass per read — skip to `startLine`, collect to `endLine`, then drain to EOF to count total lines. Only the requested lines cross the runspace boundary.
- Script (parameterized scriptblock, .NET APIs invoked through PowerShell):

```powershell
param([string]$Path, [int]$Start, [int]$End)
if (-not [System.IO.File]::Exists($Path)) { return @{ Found = $false } }
$reader = [System.IO.File]::OpenText($Path)
try {
    $lines = [System.Collections.Generic.List[string]]::new()
    $i = 0; $last = 0
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { break }
        $i++
        if ($i -ge $Start) { [void]$lines.Add($line); $last = $i }
        if ($i -ge $End) {
            while ($null -ne $reader.ReadLine()) { $i++ }
            break
        }
    }
    return @{
        Found      = $true
        Lines      = $lines
        LastLine   = $last
        TotalLines = $i
    }
} finally { $reader.Dispose() }
```

- Encoding: `[System.IO.File]::OpenText` — UTF-8 with BOM detection (UTF-8/UTF-16 LE/BE BOMs honored; no BOM → UTF-8).
- Line endings normalized by `ReadLine` (CRLF/LF/CR all handled).
- Missing file returns `Found = $false`; the ACL maps it to `Result<FileRead>.Failure` with code `FileNotFound`.
- The domain derives everything else from `FileRead`: `LastLineRead == 0` → startLine-exceeds error (with `TotalLines`); `TotalLines < requestedEnd` → clamp warning. `TotalLines` is always exact.

### OpenRouter.ACL (modifications)

- Request: `ModelRequest.Tools` → OpenRouter `tools` array. Each definition becomes:

```json
{
  "type": "function",
  "function": {
    "name": "read",
    "description": "...",
    "parameters": {
      "type": "object",
      "properties": {
        "path":      { "type": "string",  "description": "..." },
        "startLine": { "type": "integer", "minimum": 1, "description": "..." },
        "endLine":   { "type": "integer", "minimum": 1, "description": "..." }
      },
      "required": ["path", "startLine", "endLine"],
      "additionalProperties": false
    }
  }
}
```

  Strictness is enforced structurally: every parameter is listed in `required`, `additionalProperties` is `false`.

- Messages: `Role.User/Assistant` → `user`/`assistant`; assistant `ToolCalls` → `tool_calls` array; `Role.Tool` → `role: "tool"` with `tool_call_id`.
- Response: parse `choices[0].message.tool_calls[]` (`id`, `function.name`, `function.arguments`) into `ToolCallRequest`; `content` maps to `ModelResponse.Content`.
- Existing error mapping (`ProviderTimeout`, `RateLimited`, `ProviderError`) unchanged.

### Agent loop (Agent.Domain)

`Agent.SendMessage(text)` becomes the tool loop:

1. `Conversation.AddUserMessage(text)`
2. Loop, guarded by `MaxToolIterations = 10`:
   - `SendAsync(config, request)` with all messages + registry definitions
   - Provider failure → propagate `Result.Failure`
   - No tool calls → `AddAssistantMessage(content)`, return success
   - Otherwise → `AddAssistantMessage(content ?? "", toolCalls)`; for each call:
     - Registry lookup; unknown name → error `ToolResult`: `Unknown tool: {name}.`
     - Known → `tool.ExecuteAsync(new RawToolInput(name, arguments))`
     - `AddToolResult(id, result.Content)` — errors are fed back as tool results, never thrown
3. Loop exhausted → `Result.Failure(new Error("MaxToolIterations", ...))`

Tool calls execute sequentially (no parallelism in this milestone).

### CLI

Composition root wires: `PowerShellFileSystemAccess` → `ReadTool` → `ToolRegistry` → `Agent` → `SendMessageCommandHandler` → REPL. The REPL interaction itself is unchanged.

## Flow (one turn with a tool call)

```text
1. CLI → SendMessageCommandHandler → Agent.SendMessage(text)
2. Conversation.AddUserMessage(text)
3. IModelProvider.SendAsync(config, messages + tools)
4. [OpenRouter ACL → HTTP POST]
5. ModelResponse with ToolCalls
6. Conversation.AddAssistantMessage(content, toolCalls)
7. For each call: registry lookup → ReadTool.ExecuteAsync
   → IFileSystemAccess.ReadLinesAsync → [PowerShell runspace reads file]
   → ToolResult → Conversation.AddToolResult
8. Loop: SendAsync again with tool results
9. ModelResponse without tool calls → AddAssistantMessage → Result.Success
```

## Error Handling

- All expected failures flow as `Result<T>` / `ToolResult(IsError)` — never exceptions.
- Tool errors are **always** returned to the model as tool-result messages so it can self-correct; they are terminal only for that call, never for the turn.
- Provider failures (`ProviderTimeout`, etc.) remain turn-terminal, as today.
- Exceptions reserved for programmer errors (DI misconfig, null refs) → crash.

## Testing Strategy

| Layer | What | How |
| --- | --- | --- |
| Tool.Domain | ReadTool validation matrix: missing param, wrong type, unknown param, start < 1, start > end, range cap, not-found, start beyond EOF, empty file, endLine clamp | Unit tests — fake `IFileSystemAccess` |
| Tool.Domain | Gutter formatting, annotation header, warning placement, `ToolRegistry` lookup | Unit tests |
| Conversation.Domain | AddToolResult ordering, assistant message with tool calls | Unit tests |
| Model.Domain | ModelRequest/ModelResponse record validation | Unit tests |
| Agent.Domain | Loop with fake provider: tool call → result → final answer; unknown tool; MaxToolIterations exhaustion | Unit tests |
| FileSystem.ACL | Real temp files: middle range, exact EOF, clamp, start beyond EOF, missing file, empty file, CRLF, UTF-8 BOM, large-file smoke | Integration tests |
| OpenRouter.ACL | Wire format round-trips with fake HttpMessageHandler: tools in request, tool_calls in response, role:tool in request | Integration tests |
| CLI | Full loop against in-process mock OpenRouter server returning scripted tool_call responses | E2E test |
| Live | Real OpenRouter run with `read` | Manual smoke |

Key invariant: Tool.Domain and Agent.Domain tests never know PowerShell or OpenRouter exist — fakes only. The FileSystem.ACL is tested against real files on disk.

## What's deliberately excluded (future milestones)

- `write` / `edit` / `grep` / `list` tools
- PowerShell ACL for arbitrary shell execution
- Parallel tool execution, tool-choice configuration, per-tool enablement
- Token-window management (the 1000-line cap is a stopgap, not a strategy)
- Persistence, streaming responses, conversation history across sessions
