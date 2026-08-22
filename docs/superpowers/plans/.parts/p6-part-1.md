# Memory Recall Over Persisted Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port pi-fabric's `memory.recall` / `memory.sessions` essence onto eThangAgent: canonical-token and bounded-regex search over all persisted transcripts, with root-conversation persistence landing as the prerequisite.

**Architecture:** New `eThangAgent.MemoryDomain` bounded context (tokenizer, query planner, bounded regex, scope/branch resolution, search service) reading the corpus through the existing `IAgentStore`; two read-only capability actions (`memory.recall`, `memory.sessions`) registered beside `agent`/state; root REPL conversation persisted as a depth-0 `AgentRecord`.

**Tech Stack:** .NET 10, C#, xUnit, existing SQLite AppDatabase (no schema changes).

**Spec:** docs/superpowers/specs/2026-08-21-memory-recall-design.md — the plan argues from the spec; executors read both.

## Global Constraints

- Windows-only; PowerShell only shell; every task ends green; commit per task (conventional style).
- Strict boundaries: unknown scopes/modes/filters are typed errors naming valid values — never silent fallbacks.
- Literal query input is NEVER compiled as regex; planning happens once in the domain.
- Ported guardrail values verbatim from pi-fabric `search.ts`: MaxPatternBytes 1024, MaxHaystackTerms 20000, MaxHaystackBytes 2 MiB, TimeoutMs 250.
- Read-only phase: no memory-write actions; corpus strictly conversational history via `IAgentStore`.
- Unit tests fakes-only; sweep `testhost.exe`/`eThangAgent.CLI.exe` after any run that spawns them.
- Guide changes land in `Tool.Domain/ExecGuide.cs` (NOT CLI/ExecGuidePromptProvider.cs — corrected P5 deviation).

## File Structure

| File | Responsibility |
| --- | --- |
| `src/eThangAgent.Memory.Domain/` (new project) | LexicalTokenizer, MemoryQueryPlan, BoundedRegex, Scopes, SearchService |
| `src/eThangAgent.Agent.Domain/AgentRecord.cs` | MODIFY — `Root(...)` factory |
| `src/eThangAgent.Agent.Application/Memory/RecallQueryHandler.cs` | NEW — scope/branch resolution, corpus load, search, paging |
| `src/eThangAgent.Agent.Application/Memory/SessionsQueryHandler.cs` | NEW — session listing with tier labels |
| `src/eThangAgent.Agent.Domain/MemoryCapabilityProvider.cs` | NEW — `recall` / `sessions` dispatch, output contracts |
| `src/eThangAgent.CLI/Program.cs` | MODIFY — root bootstrap, exchange append, provider registration |
| `src/eThangAgent.Tool.Domain/ExecGuide.cs` | MODIFY — guide v1.5 "Recalling earlier work" |
| `tests/eThangAgent.Memory.Domain.Tests/`, `…Application.Tests/…Memory*`, `…Domain.Tests/…Memory*`, CLI E2E | mirrors each source file |

---
