# Nested Sub-Agent Execution Implementation Plan (P4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A running agent can synchronously spawn child agents that run their own full loop autonomously and report back, depth-limited to 3, persisted in the app DB.

**Architecture:** Recursive in-process spawn via the existing `Agent` aggregate. A `SubAgentSpawner` domain service validates spawn requests (specifications), enforces `MaxDepth = 3`, constructs a child `Agent` (fresh conversation, per-spawn model via `IModelProviderFactory`, shared registry), runs it, and returns its final report. `agent.spawn` is a capability-registry action invoked from exec scripts; children persist through `SqliteAgentStore` in Storage.ACL.

**Tech Stack:** .NET 10, C#, xUnit, SQLite (existing AppDatabase), Microsoft.PowerShell.SDK runspace hosting, OpenRouter ACL.

**Spec:** docs/superpowers/specs/2026-08-21-nested-subagents-design.md

## Global Constraints

- Windows-only; PowerShell is the only shell; all scripts .ps1.
- Domain namespaces omit the dot (`eThangAgent.AgentDomain`); ACL namespaces keep it (`eThangAgent.Storage.ACL`).
- Domains depend only on domain-owned interfaces; DI wiring only at the CLI composition root; domains contain zero DI references.
- Expected failures flow through `Result<T>`; exceptions only for programmer/infrastructure error.
- Strict input validation: required parameters required, unknown input rejected, nothing silently coerced or defaulted. The one sanctioned leniency pattern is a clamp with a visible warning.
- Model-facing output uses explicit format contracts (annotation lines + gutters) documented verbatim in the tool description.
- Every change leaves the build green; a task is not done unless the solution builds and all tests pass.
- Unit tests use fakes only (no PowerShell/HTTP/SQLite knowledge in domain tests); integration tests exercise real ACLs; E2E drives the full CLI against the local mock OpenRouter server.
- `MaxDepth = 3` is a domain constant. Root agent depth = 0; children run at parent.Depth + 1.
- Per-child timeout default 300 s; iterations inherit `MaxToolIterations = 10`; reports > 50 KB overflow to the exec artifact store with a visible annotation line.
- P4 persists spawned children only; the root REPL conversation stays in-memory.

---

### Task 1: Agent Domain core — identity, status, spawn contract, store seam

**Files:**

- Create: `src/eThangAgent.Agent.Domain/AgentId.cs`
- Create: `src/eThangAgent.Agent.Domain/AgentStatus.cs`
- Create: `src/eThangAgent.Agent.Domain/SpawnRequest.cs`
- Create: `src/eThangAgent.Agent.Domain/AgentRecord.cs`
- Create: `src/eThangAgent.Agent.Domain/AgentDomainEvent.cs`
- Create: `src/eThangAgent.Agent.Domain/IAgentStore.cs`
- Create: `src/eThangAgent.Agent.Domain/Specifications/SpawnRequestSpecifications.cs`
- Create: `tests/eThangAgent.Agent.Domain.Tests/eThangAgent.Agent.Domain.Tests.csproj`
- Create: `tests/eThangAgent.Agent.Domain.Tests/GlobalUsings.cs`
- Create: `tests/eThangAgent.Agent.Domain.Tests/SpawnRequestValidationTests.cs`
- Modify: `eThangAgent.sln` (add test project)

**Interfaces:**

- Produces: `readonly record struct AgentId(Guid Value)` with `AgentId.NewId()` and `ToString()` returning the bare Guid string.
- Produces: `enum AgentStatus { Running, Completed, Failed }` and `enum AgentFailureReason { MaxIterations, Timeout, ProviderError }`.
- Produces: `sealed record SpawnRequest(string TaskPrompt, string? Model = null, string? Label = null)`.
- Produces: `sealed record AgentRecord(AgentId Id, AgentId? ParentId, int Depth, AgentStatus Status, AgentFailureReason? FailureReason, string ModelUsed, string? Label, string TaskPrompt, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? FinalReport)`.
- Produces: `abstract record AgentDomainEvent(AgentId AgentId, DateTimeOffset OccurredAt)` with `sealed record AgentSpawned(...)` and `sealed record AgentCompleted(AgentId AgentId, DateTimeOffset OccurredAt, AgentStatus Status, AgentFailureReason? Reason)`.
- Produces: `interface IAgentStore { Task<Result> SaveAsync(AgentRecord record, CancellationToken ct = default); Task<Result> UpdateAsync(AgentRecord record, CancellationToken ct = default); Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default); Task<Result> AppendMessageAsync(AgentId id, ConversationMessage message, CancellationToken ct = default); Task<Result<IReadOnlyList<ConversationMessage>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default); Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default); }`
- Produces: specifications `NonEmptyTaskPromptSpecification`, `ValidModelReferenceSpecification` — each `Specification<SpawnRequest>` per the existing Specification pattern in SharedKernel, with failure messages naming the violated field.

**Steps:**

- [ ] Write failing tests: empty/whitespace `TaskPrompt` fails `NonEmptyTaskPromptSpecification` with message naming `TaskPrompt`; non-empty passes. Null/absent `Model` passes `ValidModelReferenceSpecification`; whitespace model fails with message naming `Model`; `"provider/model"` passes. `AgentId.NewId()` yields distinct Guids; `ToString()` round-trips.
- [ ] Run `dotnet test tests/eThangAgent.Agent.Domain.Tests` — confirm compile-then-fail (red).
- [ ] Implement the seven domain files minimally; no behavior beyond the tests.
- [ ] Run the test project — green.
- [ ] Commit: `feat(agent-domain): agent identity, spawn contract, store seam`

### Task 2: Depth guard + SubAgentSpawner

**Files:**

- Modify: `src/eThangAgent.Agent.Domain/Agent.cs` (add `AgentId Id`, `int Depth`; constructor parameter `depth = 0`; existing callers default to root)
- Create: `src/eThangAgent.Agent.Domain/SubAgentSpawner.cs`
- Create: `src/eThangAgent.Agent.Domain/SubAgentOptions.cs`
- Create: `tests/eThangAgent.Agent.Domain.Tests/Fakes.cs` (FakeModelProviderFactory, FakeAgentStore, FakeProvider)
- Create: `tests/eThangAgent.Agent.Domain.Tests/SubAgentSpawnerTests.cs`

**Interfaces:**

- Consumes: Task 1 types; existing `Agent` aggregate, `IModelProvider`, `IToolRegistry`, `ISystemPromptProvider`, `Conversation`.
- Produces: `sealed class SubAgentOptions(string? DefaultModel, TimeSpan ChildTimeout, int MaxDepth)` with default `MaxDepth = 3`, `ChildTimeout = 300s`.
- Produces: `sealed class SubAgentSpawner(IModelProviderFactory factory, IAgentStore store, IToolRegistry tools, ISystemPromptProvider systemPrompt, SubAgentOptions options)` with `Task<Result<AgentRunOutcome>> SpawnAsync(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)`.
- Produces: `sealed record AgentRunOutcome(AgentId ChildId, AgentStatus Status, AgentFailureReason? Reason, string Report, string ModelUsed, int Depth)`.
- Model resolution precedence: `request.Model` > `options.DefaultModel` > `Result` failure `Error [MissingModel]: supply model or configure SubAgent:DefaultModel`.
- Depth rule: `parent.Depth >= options.MaxDepth` ⇒ `Error [DepthExceeded]: agent depth N is at the limit (3); children cannot spawn further` — nothing persisted, no factory call.
- Child flow: build `AgentRecord` (Running) → `SaveAsync` → construct `Agent` at `Depth = parent.Depth + 1` with fresh `Conversation` seeded by `TaskPrompt` → run loop under linked `CancellationTokenSource(options.ChildTimeout)` → on completion persist `Completed` + `AgentCompleted` event + transcript; on timeout persist `Failed(Timeout)`; provider exception persists `Failed(ProviderError)` and returns failure Result. Report > 50 KB overflows via the existing artifact store seam with annotation text appended.

**Steps:**

- [ ] Write failing tests: depth-2 parent spawns child at depth 3? NO — depth-2 parent ⇒ child would be depth 3 > MaxDepth ⇒ rejected; depth-1 parent ⇒ child depth 2 succeeds; model precedence (explicit beats default; default used when omitted; MissingModel error when neither); child record persisted Running then Completed with report; timeout ⇒ Failed(Timeout); rejected spawn ⇒ no store writes, no factory calls.
- [ ] Run — red. Implement spawner minimally. Run — green.
- [ ] Commit: `feat(agent-domain): sub-agent spawner with depth guard and model resolution`
### Task 3: Model provider factory seam

**Files:**

- Create: `src/eThangAgent.Model.Domain/IModelProviderFactory.cs`
- Create: `src/eThangAgent.OpenRouter.ACL/OpenRouterModelProviderFactory.cs`
- Modify: `tests/eThangAgent.OpenRouter.ACL.Tests/OpenRouterModelProviderFactoryTests.cs`

**Interfaces:**

- Produces: `interface IModelProviderFactory { IModelProvider Create(ModelConfig config); }` (Model Domain).
- Produces: `OpenRouterModelProviderFactory(OpenRouterConfiguration baseConfig)` — reuses the ACL's existing credential/transport wiring; `Create` returns an `OpenRouterModelProvider` whose requests carry `config.ModelId` (model is a request parameter; one credential set serves all models).

**Steps:**

- [ ] Write failing test: factory returns provider; captured request body (existing FakeHttpMessageHandler pattern) carries the per-call `ModelConfig.ModelId` while auth headers come from the base configuration.
- [ ] Run — red. Implement. Run — green.
- [ ] Commit: `feat(openrouter-acl): per-spawn model provider factory`

### Task 4: SqliteAgentStore

**Files:**

- Modify: `src/eThangAgent.Storage.ACL/AppDatabase.cs` (add `agents` and `agent_messages` tables to the existing `CREATE TABLE IF NOT EXISTS` migration block)
- Create: `src/eThangAgent.Storage.ACL/SqliteAgentStore.cs`
- Create: `tests/eThangAgent.Storage.ACL.Tests/SqliteAgentStoreTests.cs`

**Interfaces:**

- Consumes: Task 1 `IAgentStore`, `AgentRecord`, `AgentId`; existing `AppDatabase` connection discipline.
- Tables: `agents(id TEXT PRIMARY KEY, parent_id TEXT NULL, depth INTEGER, status INTEGER, failure_reason INTEGER NULL, model_used TEXT, label TEXT NULL, task_prompt TEXT, created_at TEXT, completed_at TEXT NULL, final_report TEXT NULL)`; `agent_messages(agent_id TEXT, seq INTEGER, role TEXT, content TEXT, meta_json TEXT NULL, PRIMARY KEY(agent_id, seq))`. Events persist as rows in the existing `state_events`-style pattern: reuse a new `agent_events` table `(agent_id, occurred_at, type, payload_json)`.
- Messages serialize via the existing ConversationDomain JSON conventions (same serializer settings as SqliteStateStore).

**Steps:**

- [ ] Write failing tests against a temp SQLite file: save→get round-trips every `AgentRecord` field; update transitions Running→Completed with `FinalReport` and `CompletedAt`; append/get-transcript preserves order and content; list-children filters by parent and orders by `CreatedAt`; missing id returns typed `NotFound` failure; events persist and reload.
- [ ] Run — red. Implement store + tables. Run — green.
- [ ] Commit: `feat(storage-acl): sqlite agent store with transcripts and events`

### Task 5: agent.spawn capability surface + guide v1.3

**Files:**

- Create: `src/eThangAgent.Agent.Domain/AgentCapabilityProvider.cs`
- Modify: `src/eThangAgent.Tool.Domain/ExecGuide.cs` (bump to v1.3, add "Delegating subtasks" section)
- Create: `tests/eThangAgent.Agent.Domain.Tests/AgentCapabilityProviderTests.cs`
- Modify: `tests/eThangAgent.Tool.Domain.Tests/ExecGuideTests.cs` (v1.3 assertions)

**Interfaces:**

- Consumes: `ICapabilityProvider` pattern from `StateCapabilityProvider` — `ProviderId = "agent"`, `Actions` descriptors, `InvokeAsync(actionName, jsonArguments, ct)` switch.
- Produces: `AgentCapabilityProvider(SubAgentSpawner spawner, IAgentStore store)` exposing action `spawn` with parameters `taskPrompt` (String, required), `model` (String, optional), `label` (String, optional). Unknown parameters ⇒ typed validation error; missing taskPrompt ⇒ typed error.
- Output contract, verbatim in the action description:

  ```text
  [agent] id=<id> status=completed depth=1 model=<model> label=<label>
  --- report ---
  <child's final report text>
  --- end report ---
  ```

  Failures: same shape, `status=failed reason=max-iterations|timeout|provider-error|depth-exceeded|missing-model`, plus partial report when present. Overflow annotation line appended before `--- end report ---` when the 50 KB rule fired.
- Engine note: `PowerShellExecEngine.CreateSetupScript` already mints bare + composite wrapper names for every registry action (P3 wave-3 fix) — `spawn`/`agent.spawn` wrappers appear automatically once the provider is registered. Verify in Task 6; no engine change expected.
- Guide v1.3: new "### Delegating subtasks" section teaching `agent.spawn @{ taskPrompt = ...; model = ... }`, self-contained task framing, clear report expectation, cheap-model guidance, and the depth limit (3). Capability reference renders `spawn(...)` automatically from the registry.

**Steps:**

- [ ] Write failing provider tests: valid JSON arguments invoke spawner and render the gutter contract exactly (id/status/depth/model/label + fenced report); spawner failure renders failed-gutter with reason; unknown/missing fields render typed validation errors; overflow annotation appears.
- [ ] Write failing guide tests: v1.3 contains "Delegating subtasks", `agent.spawn`, depth-limit sentence.
- [ ] Run — red. Implement provider + guide text. Run — green.
- [ ] Commit: `feat(agent-domain): agent.spawn capability with report gutters; guide v1.3`
### Task 6: Integration stack fact — spawn through real engine + registry

**Files:**

- Create: `tests/eThangAgent.PowerShell.ACL.Tests/AgentStackIntegrationTests.cs`

**Interfaces:**

- Consumes: Task 2 spawner, Task 4 store, Task 5 provider; the P3 stack-fact pattern (`StateStackIntegrationTests`) — real `AppDatabase` on a temp file, real engine, in-proc fake `IModelProvider` scripted to make the child call one tool then produce a final report.
- Program shape (PowerShell inside `exec`): parent script calls `agent.spawn @{ taskPrompt = 'summarize'; label = 'child-a' }` and emits the result.

**Steps:**

- [ ] Write the fact: child runs to completion through the real runspace + broker + registry; output contains `[agent] id=` gutter with `status=completed`, fenced report text from the fake provider's scripted finish; `SqliteAgentStore` (real, temp file) holds the child row `Completed` at depth 1 with transcript messages.
- [ ] Add nesting fact: child's scripted tool calls include `agent.spawn` for a grandchild (fake provider scripts two levels); grandchild completes at depth 2; both rows persisted with correct `ParentId` chain.
- [ ] Add rejection fact: at depth 2, spawning again returns the failed-gutter with `reason=depth-exceeded` as a well-formed tool result — run status stays Completed, no depth-3 row exists.
- [ ] Run all three facts — green. Confirm dual-name wrappers: program uses composite `agent.spawn`; add one assertion invoking bare `spawn` successfully.
- [ ] Commit: `test(powerShell-acl): nested spawn facts through real engine and store`

### Task 7: Configuration + composition-root wiring

**Files:**

- Modify: `src/eThangAgent.Configuration.Domain/...` (add `SubAgentOptions` binding: `SubAgent:DefaultModel`, `SubAgent:ChildTimeoutSeconds`)
- Modify: `src/eThangAgent.CLI/Program.cs` (register `IAgentStore` → `SqliteAgentStore`, `IModelProviderFactory` → `OpenRouterModelProviderFactory`, `SubAgentSpawner`, `AgentCapabilityProvider`, options binding)
- Modify: `tests/eThangAgent.CLI.Tests/...` (configuration binding test)

**Interfaces:**

- Consumes: all prior tasks. The root agent registers in the registry context at depth 0 with its own `AgentId` (root rows are NOT persisted in P4 — children only).
- Strict config: absent `SubAgent:DefaultModel` is legal (spawns must then pass `model` explicitly); present-but-empty ⇒ configuration validation error at startup. `ChildTimeoutSeconds` defaults to 300 when absent; zero/negative ⇒ startup error.

**Steps:**

- [ ] Write failing config-binding tests: defaults when section missing; explicit values bind; empty model string rejected; non-positive timeout rejected.
- [ ] Run — red. Implement binding + registrations. Run — green; full CLI build clean.
- [ ] Verify no domain project gained DI references (grep `Microsoft.Extensions` under `src/eThangAgent.Agent.*`).
- [ ] Commit: `feat(cli): wire sub-agent spawner, agent capability, and configuration`
### Task 8: E2E — parent spawns child through mock OpenRouter

**Files:**

- Modify: `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs` (script multi-agent turns keyed by request `model` field)
- Modify: `tests/eThangAgent.CLI.Tests/E2ETests.cs` (new E2E)

**Steps:**

- [ ] Extend mock server: when a request body carries `"model":"<sub-model>"`, serve the scripted child conversation (one tool-call turn, then final report turn); parent turns served as today.
- [ ] Write failing E2E: REPL session where the parent's exec program calls `agent.spawn @{ taskPrompt = '...'; model = 'mock/sub-model'; label = 'e2e' }`.
- [ ] Assert via decoded JSON (parse request body → `messages[n].content`): per-spawn model id reached the wire (`"model":"mock/sub-model"` in child request bodies); the tool message contains the completed-gutter (`status=completed`, fenced report text); the rendered transcript shows the delegation.
- [ ] Assert persistence against a temp app DB path passed to the CLI: child row exists, `Completed`, transcript non-empty.
- [ ] Run — red where it drives implementation gaps, then green. Sweep leaked processes after the run (`taskkill //F //IM testhost.exe; taskkill //F //IM eThangAgent.CLI.exe`) — leaked children poison subsequent runs.
- [ ] Commit: `test(cli): e2e nested spawn through mock openrouter`

### Task 9: Full-solution gate + docs

**Files:**

- Modify: `docs/superpowers/plans/2026-08-21-nested-subagents.md` (header: completion status)

**Steps:**

- [ ] `dotnet build` clean; full `dotnet test` — all suites green including the 14 pre-existing ones (regression proof).
- [ ] Coverage check on new projects ≥ 80 % floor; note actuals in the plan header.
- [ ] Process sweep after the final E2E-bearing run; verify with `procs` that no `eThangAgent.CLI.exe`/`testhost.exe` remain.
- [ ] Update plan header checkboxes; commit `docs(plan): P4 complete`.

## Verification checklist (final acceptance)

- [ ] `agent.spawn @{taskPrompt=...}` from an exec script returns the gutter-contract report of an autonomously-run child.
- [ ] Grandchildren work to depth 2; depth-3 attempt returns typed `DepthExceeded` as a tool result; nothing persists for rejected spawns.
- [ ] Model precedence: explicit > configured default > `MissingModel` typed error.
- [ ] Child rows + transcripts persist across process restarts (store integration fact re-reads a pre-populated file).
- [ ] Per-spawn model id observable on the wire (E2E request-body assert).
- [ ] All suites green; no leaked processes after E2E sweeps.
