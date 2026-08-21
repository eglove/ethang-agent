# Nested Sub-Agent Execution — Design (P4)

Date: 2026-08-21 · Status: Approved design, pending implementation plan

## Motivation

Agents need to delegate self-contained subtasks to child agents that run their own full loop autonomously and report back. This is the P4 slice of the roadmap (P1 exec → P2 registry → P3 state → **P4 agents** → P5 actors/mesh). Supervisor processes, kanban tracking, and inter-agent channels are grand-plan future work; this design only keeps compatibility seams open for them.

## Decision ledger (user-approved)

| Concern | Decision |
| --- | --- |
| Scope | Nested sub-agent execution only; supervisor/kanban/tracking deferred |
| Lifecycle | Autonomous spawn-and-run-to-completion; parent receives final report |
| Child surface | Full capability registry, including `agent.spawn` (recursion real) |
| Depth limit | 3, enforced by the Agent Domain |
| Model selection | Per-spawn model string; falls back to configured default; typed error when neither |
| Persistence | Spawned children + transcripts persisted in app DB (existing SQLite seam) |
| Call semantics | Synchronous spawn — blocks until child completes, report returned as action result |

## 1. Domain model & rules

- `AgentId` value object (Guid-based) on every agent; existing `Agent` aggregate gains `Id` and `Depth` (root = 0). Children run the same `SendMessage` loop machinery.
- `AgentStatus`: `Running`, `Completed`, `Failed(reason)` with reasons `MaxIterations | Timeout | ProviderError`. (`DepthRejected` never persists — rejection happens before creation.)
- `SpawnRequest` record: `TaskPrompt` required non-empty; `Model` optional (non-whitespace when present); `Label` optional free text.
- `SubAgentSpawner` domain service validates via specifications (`NonEmptyTaskPromptSpecification`, `ValidModelReferenceSpecification`) and enforces `MaxDepth = 3` (domain constant): spawn at `parent.Depth >= 3` returns a typed `Result` error that surfaces as the tool result so the model self-corrects. Rejected spawns create nothing.
- Domain events `AgentSpawned`, `AgentCompleted(status)` recorded on the aggregate and persisted. No dispatcher in P4; future consumers read rows instead of wiring callbacks.

## 2. Capability surface & output contracts

- Registry provider `agent` exposes action `spawn`: `agent.spawn @{ taskPrompt = '...'; model = 'provider/model'; label = 'research' }`.
- Strict input validation: unknown parameters rejected; optional strings must be non-whitespace when present.
- Engine setup script mints both bare `spawn` and composite `agent.spawn` wrapper names (same dual-name projection as P3 wave 3).
- Output contract (documented verbatim in tool description):

```
[agent] id=<id> status=completed depth=1 model=<model> label=<label>
--- report ---
<child's final report text>
--- end report ---
```

Failures keep the gutter shape with `status=failed reason=max-iterations|timeout|provider-error` plus any partial report — always a well-formed tool result, never a turn-ending crash.

- Children receive the same system prompt (exec guide + capability reference) and therefore know the full surface including `agent.spawn`.
- Guide v1.3 adds "Delegating subtasks": when/how to spawn (self-contained task, clear report expectation, cheap model for grunt work), depth-limit note. Reference rendering is automatic from the registry; `Get-AgentAction`/`DescribeAction` cover `spawn` via P2 machinery.

## 3. Storage & persistence

- `IAgentStore` interface owned by Agent Domain: save/update `AgentRecord`, append messages, fetch agent + transcript, list children by parent.
- `AgentRecord`: `AgentId`, nullable `ParentId` (null = root), `Depth`, `Status`, `ModelUsed`, `Label`, `TaskPrompt`, timestamps, `FinalReport`. Transcripts reuse ConversationDomain message shapes serialized to JSON.
- `SqliteAgentStore : IAgentStore` in Storage.ACL, same `AppDatabase` file; new tables `agents` (typed columns) and `agent_messages` (`agent_id`, `seq`, `role`, `content`, `meta_json`); migration follows the P3 versioning pattern. Append-mostly writes, synchronous single-process access.
- Explicit scope line: P4 persists spawned children only; the root REPL conversation stays in-memory. Root-conversation persistence is separate future work.

## 4. Wiring, budgets & composition

- `IModelProviderFactory.Create(ModelConfig) : IModelProvider` (Model Domain); OpenRouter ACL implements thinly — one credential set, model id is a request parameter.
- `SubAgentSpawner` deps: factory, `IAgentStore`, shared `IToolRegistry`, shared `ISystemPromptProvider`, `SubAgentOptions`.
- Configuration (strict): `SubAgent:DefaultModel`. Omitted model + unconfigured default ⇒ typed error telling the model to supply one or configure a default. No silent fallbacks.
- Budgets: per-child timeout 300 s (token chained from parent's script execution; Ctrl+C records `Failed(timeout)`); iterations inherit `MaxToolIterations = 10`; reports over 50 KB overflow to the exec artifact store with a visible annotation line.
- Composition root registers: `SqliteAgentStore`, `OpenRouterModelProviderFactory`, `SubAgentSpawner`, `agent` provider, `SubAgentOptions` binding. Domains stay DI-free.

## 5. Testing strategy

- Unit (fakes only): depth specification boundaries (0–2 accept, 3 reject), spawn-request validation, status transitions, events, report-overflow rule; spawner model-resolution precedence (**explicit > configured default > typed error**), depth propagation, failure mapping.
- Integration (real ACLs): store round-trip/transcript/list-by-parent/migration against real SQLite; engine stack fact — registry + real engine + in-proc fake provider scripting child behavior, grandchild nesting at depth 2, depth-3 rejection as well-formed tool result.
- E2E (CLI vs mock OpenRouter): parent+child scripted turns; assert per-spawn model id reaches the wire, decoded tool message contains report gutter, child row persisted.
- Regression: all 14 existing suites green throughout; coverage ≥ 80 % floor, high on domain.

## Future compatibility (not built now)

Inter-agent channels become additional actions on the same aggregate/seam (children already have identity + persisted transcripts). Async fan-out lands in P5 actors/mesh behind the same spawner seam. Supervisor reads `agents` rows + events. Configurable depth and root-conversation persistence are small follow-ups.
