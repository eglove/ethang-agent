# P1 Exec Core — Design Specification

**Date:** 2026-08-21 · **Status:** Approved in review (sections 1–4) · **Phase:** 1 of 7 (pi-fabric native port)

## Context & Goals

pi-fabric is a programmable tool and agent runtime for the Pi coding agent (~212 source files). Its core idea: one model-facing tool (`fabric_exec`) where the model writes a type-checked program that routes calls through a single capability registry, plus orchestration layers (agents, actors, mesh, state, schema, memory) built on that core.

We are porting the **concepts**, not the code, into eThang Agent natively. The decisive translation: pi-fabric's security story is QuickJS isolation plus a JSON host bridge; eThang Agent runs **trusted PowerShell + .NET in-process** — equivalent to pi-fabric's "unsafe node-process" mode, not its sandbox mode. The type-checker analog is PowerShell's own AST parser (syntax + validation before execution) — weaker than static TS checking, a named trade-off.

### Approved roadmap (each phase = its own spec → plan → implementation cycle)

| Phase | Scope |
| ----- | ----- |
| **P1 (this spec)** | Exec Core: one `exec` tool, PowerShell program execution, tool wrappers, guide injection |
| P2 | Capability Registry: domain-owned action registry + provider seams (tools first, MCP later) |
| P3 | Durable State + Schema: CAS state store, evidence-gated file mutation |
| P4 | Agent Runtime: one-shot sub-agents, budget ledger, handoff |
| P5 | Actors + Mesh: persistent actors, mailboxes, durable topics |
| P6 | Memory + Compaction: recall/search, context economy |
| P7 | Activity UI: dashboard over Terminal.ACL |

Build order P1 → P2 → P3/P4 → P5 → P6/P7. P1 is the foundation; P2 is the seam the rest routes through.

## Approved Decisions

- **D1 — Execution surface (A):** In-process runspace, **fresh per execution**, pipeline stop on timeout/cancel. Matches the project convention ("hot paths … in-process hosting; avoid per-call process spawns"). Engine behind a domain-owned `IExecEngine` seam so an out-of-process implementation can replace it without touching domain code.
- **D2 — In-script tool exposure (C):** Named wrappers generated from `IToolRegistry` (function named exactly after the tool, taking one hashtable), plus a generic `Invoke-AgentTool` dispatcher, plus `Get-AgentTool` introspection. All three feed the same registry path. Hashtable→JSON conversion happens **in .NET** (recursive walker in the broker), never `ConvertTo-Json`; non-JSON-able values are rejected. No PowerShell parameter binding on wrappers — silent coercion stays out of the path; validation remains inside the tools.
- **D3 — Guide injection at session start (user requirement):** The exec usage guide ships in the system prompt on every request, so the model writes correct programs immediately. (pi-fabric loads its reference skill lazily; we deliberately do the opposite.)
- **D4 — Trusted execution posture (named decision):** P1 runs fully trusted — no ConstrainedLanguage, no filesystem/network restriction inside scripts, no approval gates. Interactive cmdlets would hang; the timeout is the guard (known limitation, accepted). Mirrors pi-fabric's documented "unsafe" mode. `IExecEngine` is where a constrained mode slots in later.

## Architecture

```text
eThangAgent.Tool.Domain            eThangAgent.PowerShell.ACL (NEW)
  ExecTool : ITool                    PowerShellExecEngine : IExecEngine
  ExecProgram (value object)            ├─ fresh Runspace per execution
  IExecEngine ──────── seam ───────────►├─ AST parse validation
  IExecOutputStore ── seam ──┐          ├─ ToolBroker bridge (wrappers,
  ExecOptions (limits)       │          │  dispatcher, introspection)
  budget/format logic        │          └─ pipeline stop on timeout/cancel
                             ▼
                   eThangAgent.FileSystem.ACL
                   (file-backed artifact store impl)
```

- **New project `eThangAgent.PowerShell.ACL`** (namespace `eThangAgent.PowerShell.ACL`): owns everything `System.Management.Automation` — runspace lifecycle, initial session state, AST parse (`Parser::ParseInput`), the `ToolBroker` bridging script calls into `IToolRegistry`, hard-stop mechanics. Implements `IExecEngine`. The domain never references a PowerShell type.
- **Tool Domain gains the exec concepts:** `ExecTool` (ordinary `ITool` — **zero agent-loop changes**), `ExecProgram` value object with strict input validation, `IExecEngine` + `IExecOutputStore` seams, `ExecOptions`, and result-budgeting/format logic as pure domain code (unit-testable with fakes).
- **Composition root (CLI) wiring only:** `IExecEngine → PowerShellExecEngine`, `IExecOutputStore → file-backed impl`, `ExecTool` registered beside `ReadTool`.
- **Named follow-up:** migrate `PowerShellFileSystemAccess`'s runspace lifecycle onto the new ACL's hosting primitives (small, mechanical) — exactly one place knows how PowerShells are born in this process.
- **Activity stub:** `IExecActivitySink` with a no-op default; P7's dashboard attaches later without touching exec code.

**Data flow per call:** `exec{program}` → strict input validation → AST validate (syntax errors → actionable tool result; model self-corrects) → fresh runspace + injected tool functions → execute under timeout/cancellation → capture streams → budget result → `ToolResult` to conversation.

## Model-Facing Contract

**Tool definition** — minimal and strict:

```csharp
Name: "exec"
Parameters: [ program: String, required ]   // nothing else; timeout/limits are config, never model-controlled
```

**In-script surface** (injected into every fresh runspace):

```powershell
# 1. Named wrapper per registered tool — name matches the tool list verbatim:
read @{ path = "src/X.cs"; startLine = 1; endLine = 50 }

# 2. Generic dispatcher — same broker path, for dynamic calls:
Invoke-AgentTool -Name read -Input @{ path = "src/X.cs"; startLine = 1 }

# 3. Introspection — discover instead of guess:
Get-AgentTool   # → name, description, parameters of every registered tool (exec excluded)
```

Mechanics: wrappers call a `ToolBroker` .NET object bridging into `IToolRegistry`; async tools block on the pipeline thread (safe — no synchronization context there). Tool errors surface as **terminating PowerShell errors carrying the error code**, so scripts `try/catch` naturally.

**Result contract** (documented verbatim in the tool description): the script's output stream *is* the result — each object rendered deterministically (strings as-is, complex objects as one-line JSON). Non-terminating errors get gutter lines; any terminating error sets `IsError`.

**Guide injection mechanics:**

- `Role.System` added to ConversationDomain; `ModelRequest` gains `string? SystemPrompt` — per-request assembly, not conversation state, keeping history clean.
- Guide text lives in **Tool Domain next to `ExecTool`** (single source of truth, versioned constant, ~40 lines: call patterns, hashtable args, try/catch, output-as-result, artifact contract).
- `ISystemPromptProvider` seam in Model Domain; CLI composes segments (identity + exec guide); OpenRouter ACL maps `SystemPrompt` to the system message.

## Errors, Guardrails & Budgets

**Validation pipeline** (strict, ordered; every failure is a tool result):

1. **Input** (`ExecProgram`): required, non-empty, ≤ 64KB text, unknown parameters rejected. No coercion, no defaults.
2. **Parse** (AST): bounded list (max 10) of `line <n>, col <m>: <message>` under `exec error [ExecParseError]:`.
3. **Execution:** fresh runspace, disposed after. Timeout (default 120s, config-only) stops the pipeline → `ExecTimeout` with bounded partial output. `CancellationToken` flows to pipeline stop → `ExecCancelled`.

**Output budgeting:** success cap 50KB, both ends preserved with explicit omission markers; overflow → full output to an artifact file (`%TEMP%\eThangAgent\exec-artifacts\`), visible result carries `[exec:artifact <path>]`. Error results capped at 20KB. Program text never echoed back (already in the tool-call arguments).

**Recursion:** `exec` excluded from wrapper injection and from `Get-AgentTool` — no nested exec in P1. Recursion depth/budgets are P4 territory.

**Error taxonomy:** `ExecProgramRequired`, `ExecProgramTooLarge`, `ExecParseError`, `ExecTimeout`, `ExecCancelled`, `ExecEngineFailure` (infrastructure only — a bug, never model feedback). All model-facing failures use the `exec error [<Code>]:` gutter documented in the tool description.

## Testing Strategy

**Unit — Tool Domain / Model Domain (fakes only, zero PowerShell knowledge):** `ExecProgram` validation matrix → exact error codes; result budgeting (passthrough, both-ends truncation + markers, artifact line, 20KB error ceiling); parse-list bounding (max 10) and gutter shape; `ExecTool` orchestration against fake `IExecEngine` (parse failure never executes, validation order, cancellation); tool description mechanically asserted to contain the format-contract lines; composite `ISystemPromptProvider` (ordered join, null-safe).

**Integration — PowerShell.ACL + FileSystem.ACL (real runspaces, no model):** parse validation line/col; output capture (strings as-is, objects as one-line JSON); nested hashtable conversion; non-JSON-able rejection; real `ReadTool` invoked from a script (temp files); `Invoke-AgentTool` / `Get-AgentTool` (`exec` not listed); tool failure → catchable terminating error with code; `Start-Sleep 300` → `ExecTimeout` with partial output; mid-run cancellation → `ExecCancelled`; state isolation across calls; `exec` function absent from runspace; artifact overflow writes file + path in result.

**E2E — full CLI against the local mock provider:** mock emits `exec` whose script reads a known file → tool result in conversation, final turn reflects it; self-correction path (broken syntax → `ExecParseError` → corrected program → both results in history); first request payload contains the system prompt with exec-guide markers.

**Definition of done:** `dotnet build` green, full suite green, exec domain logic ~100% coverage, ACL ≥ 80%.

## Out of Scope (P1)

Nested/recursive exec (P4), MCP and provider routing (P2), durable state and schema enforcement (P3), sub-agents and budgets (P4), actors/mesh (P5), memory/compaction (P6), dashboard UI (P7), ConstrainedLanguage mode (post-P1 revisit via `IExecEngine`), migration of `PowerShellFileSystemAccess` onto shared runspace hosting (planned follow-up task within P1's plan, kept explicit).
