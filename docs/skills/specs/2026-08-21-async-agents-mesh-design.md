# Async Agent Execution (Actors/Mesh) — Design (P5)

Date: 2026-08-21 · Status: Approved design, pending implementation plan

## Motivation

P4 made delegation real but synchronous: the parent blocks inside one tool call until the child finishes its entire loop. Long children waste the parent's turn budget, siblings never overlap, and a slow child stalls the whole tree. P5 is the actor payoff: spawn returns immediately, children run as independent in-process actors, and parents retrieve outcomes on demand. Roadmap position: P1 exec → P2 registry → P3 state → P4 agents → **P5 actors/mesh**.

## Decision ledger (user-approved)

| Concern | Decision |
| --- | --- |
| Spawn semantics | **Breaking change**: async — `agent.spawn` returns `{id, status=running}` immediately |
| Result retrieval | Poll-based queries `agent.status` / `agent.result`; no push into the parent conversation |
| Runtime | In-process only (one Task per child); no cross-process or background agents |
| Inter-agent channels | Out of scope; agents interact via spawn + reports only, seams kept open |
| Supervision | Watchdog/restart deferred; faults recorded as `Failed(reason)` rows |
| Concurrency cap | `MaxConcurrentAgents` config setting, strictly validated; typed error at the cap |
| Store concurrency | `SqliteAgentStore` writes serialized behind a single-writer gate |
| Guide | v1.4 retaught for non-blocking delegation (spawn → work → status → result) |

## 1. Domain model & rules

- New domain-owned seam `IAgentRuntime` (Agent.Domain): `Start(SpawnRequest) → Result<AgentId>`. The domain knows actors exist; nothing about Tasks, threads, or processes crosses the seam.
- Concurrency cap is a domain rule expressed as a typed failure `ConcurrencyCapReached` returned by `Start` when the runtime is at capacity. No queuing, no clamping — the model sees the error and self-corrects (waits, retrieves pending results, or gives up).
- Status transitions unchanged from P4 (`Running → Completed(report) | Failed(reason)`), still persisted through `IAgentStore`.
- `agent.result` on a non-completed agent is a typed error (`NotComplete`), never a block, never a partial guess. `agent.status` / `agent.result` on an unknown id is a typed `NotFound` error.
- Validation reuse: `NonEmptyTaskPromptSpecification`, `ValidModelReferenceSpecification`, `MaxDepth = 3` all unchanged. Rejected spawns create nothing, as in P4.
- No new domain events; `AgentSpawned` / `AgentCompleted` already cover the lifecycle.

## 2. Application layer (CQRS)

- **Command** `StartSpawn`: runs the validation specifications and depth guard, calls `IAgentRuntime.Start`, persists the `Running` record, returns `AgentId`. Mutates; returns no domain data beyond the id.
- **Queries** `GetAgentStatus(AgentId)` (record snapshot) and `GetAgentResult(AgentId)` (`Result<string>` report). Side-effect free, read-model straight from `IAgentStore`.
- `AgentCapabilityProvider` stays thin: three actions dispatched to the command and two queries.

## 3. Capability surface & output contracts

- `agent.spawn @{ taskPrompt = '...'; model = 'provider/model'; label = 'research' }` → `id=<guid> status=running` (annotation-style lines consistent with P4 gutters).
- `agent.status @{ id = '<guid>' }` → `id=<guid> status=running|completed|failed reason=...`.
- `agent.result @{ id = '<guid>' }` → the final report verbatim, or `Error [NotComplete]: agent <id> is still running.` / `Error [NotFound]: ...`.
- Ids are strictly parsed Guids; malformed input is a typed tool error, never coerced.
- Guide v1.4 replaces the delegation section: spawn is non-blocking; check `status`; fetch `result`; fan out siblings for parallel work; depth limit unchanged.

## 4. Infrastructure, wiring & configuration

- `InProcessAgentRuntime : IAgentRuntime` (Agent.Infrastructure): tracks in-flight children, enforces the cap with a counter, starts each child on the same loop machinery `SubAgentSpawner` uses today (reused, not duplicated), observes faults and persists `Failed(ProviderError)`.
- `SubAgentSpawner` gains an execution-mode split: the loop body is shared; P4's synchronous path is replaced by the runtime-driven path.
- `SqliteAgentStore` (Storage.ACL): writes move behind a `SemaphoreSlim(1,1)` single-writer gate — children now write concurrently; reads stay direct.
- Configuration (strict): `SubAgent:MaxConcurrentAgents` required, positive integer, bound alongside `SubAgent:DefaultModel` in `SubAgentConfiguration`; missing or non-positive ⇒ startup error naming the key.
- Composition root wires `IAgentRuntime` → `InProcessAgentRuntime`; no domain DI references.

## 5. Testing strategy

- **Unit** (fakes only): command validation, cap rejection, `status`/`result` transitions including `NotComplete` and `NotFound`; the domain never sees HTTP, shell, or SQLite.
- **Integration**: `SqliteAgentStore` under concurrent writers through the gate; `InProcessAgentRuntime` driving real children against fake providers to completion and failure.
- **E2E**: mock OpenRouter scripts a parent doing spawn → status → result with a child on a distinct model; asserts the spawn tool result returns immediately, the child model id reaches the wire, and the report arrives via `agent.result`.

## Known limitations (documented, accepted)

- Process exit kills in-flight children; their rows stay `Running` forever. Reconciliation belongs to the grand-plan maintenance process, not P5.
- No push notification when a child finishes — the model must poll. Deliberate: keeps the surface free of inter-agent messaging.

## Out of scope

Supervision/restart policy, inter-agent mailboxes/channels, cross-process agents, kanban/tracking, root-conversation persistence (still future work per P4's scope line).
