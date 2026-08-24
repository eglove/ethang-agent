# Async Agent Execution (Actors/Mesh) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `agent.spawn` asynchronous — children run as independent in-process actors while parents keep working, retrieving outcomes via new `agent.status` / `agent.result` capability actions.

**Architecture:** New domain-owned seams `IAgentRuntime` / `IAgentRunner` (Agent.Domain); a CQRS split (StartSpawn command, status/result queries in Agent.Application); `InProcessAgentRuntime` in a new `eThangAgent.Agent.Infrastructure` project driving the extracted child-loop on one Task per child; `SqliteAgentStore` writes serialized behind a single-writer gate.

**Tech Stack:** .NET 10, C#, xUnit, SQLite (existing AppDatabase), OpenRouter ACL (unchanged).

**Spec:** docs/skills/specs/2026-08-21-async-agents-mesh-design.md — the plan argues from the spec; executors read both.

## Global Constraints

- Windows-only; PowerShell is the only shell; no `.sh`/`.cmd`/`.bat`.
- Strict correctness at boundaries: required settings required, unknown input rejected, nothing silently coerced or clamped.
- Expected domain failures flow through `Result<T>` with `Error [Code]: message` strings — never exceptions; exceptions are programmer/infra error only.
- Domains depend only on domain-owned interfaces; zero DI container references outside the composition root (`Program.cs`).
- Unit tests use fakes only — no PowerShell, HTTP, SQLite, or OpenRouter knowledge in domain/application tests.
- Every task ends green: full solution builds and the touched project's tests pass before commit.
- Commit after every task, conventional-commit style.
- After any test run that spawns the CLI: sweep with `taskkill //F //IM testhost.exe 2>/dev/null; taskkill //F //IM eThangAgent.CLI.exe 2>/dev/null`.
- Breaking change is intentional: P4's synchronous `agent.spawn` contract is removed; all affected tests are updated within this plan.

## File Structure

| File | Responsibility |
| --- | --- |
| `src/eThangAgent.Agent.Domain/IAgentRuntime.cs` | NEW — runtime seam: `Start(AgentRecord) → Result<AgentId>` |
| `src/eThangAgent.Agent.Domain/IAgentRunner.cs` | NEW — child-execution seam: `RunAsync(AgentRecord) → AgentRunOutcome` |
| `src/eThangAgent.Agent.Domain/RuntimeErrors.cs` | NEW — canonical error-string constants for runtime/query failures |
| `src/eThangAgent.Agent.Application/StartSpawnHandler.cs` | NEW — validates, persists `Running`, hands to runtime |
| `src/eThangAgent.Agent.Application/AgentQueries.cs` | NEW — `GetStatus`, `GetResult` read paths |
| `src/eThangAgent.Agent.Infrastructure/` (new project) | `InProcessAgentRuntime` — cap enforcement, Task-per-child |
| `src/eThangAgent.Agent.Domain/SubAgentSpawner.cs` | MODIFY — extract loop body into `RunAsync`; delete sync `SpawnAsync` |
| `src/eThangAgent.Storage.ACL/SqliteAgentStore.cs` | MODIFY — single-writer gate around writes |
| `src/eThangAgent.CLI/SubAgentConfiguration.cs` | MODIFY — add required `MaxConcurrentAgents` |
| `src/eThangAgent.Agent.Domain/SubAgentOptions.cs` | MODIFY — carry `MaxConcurrentAgents` |
| `src/eThangAgent.Agent.Domain/AgentCapabilityProvider.cs` | MODIFY — three actions dispatching to handler/queries |
| `src/eThangAgent.CLI/Program.cs` | MODIFY — wiring for handler, queries, runtime |
| `src/eThangAgent.CLI/ExecGuidePromptProvider.cs` | MODIFY — delegation section rewrite, guide v1.4 |
| `tests/…` mirrors each source file above | unit/integration/E2E as assigned per task |

---

### Task 1: Domain seams — `IAgentRuntime`, `IAgentRunner`, error constants

**Files:**

- Create: `src/eThangAgent.Agent.Domain/IAgentRuntime.cs`
- Create: `src/eThangAgent.Agent.Domain/IAgentRunner.cs`
- Create: `src/eThangAgent.Agent.Domain/RuntimeErrors.cs`
- Test: `tests/eThangAgent.Agent.Domain.Tests/RuntimeSeamTests.cs`

**Interfaces:**

- Consumes: existing `AgentRecord`, `AgentId`, `AgentRunOutcome`, `Result<T>` (SharedKernel).
- Produces: `IAgentRuntime.Start(AgentRecord record, CancellationToken ct = default) → Task<Result<AgentId>>`; `IAgentRunner.RunAsync(AgentRecord child, CancellationToken ct = default) → Task<AgentRunOutcome>`; `RuntimeErrors.CapReached`, `.NotFound(Guid)`, `.NotComplete(Guid)` — exact strings later tasks assert.

- [ ] **Step 1: Write the failing test**

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class RuntimeSeamTests
{
    private sealed class FakeRuntime : IAgentRuntime
    {
        public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<AgentId>.Ok(record.Id));
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
            => Task.FromResult(new AgentRunOutcome(AgentStatus.Completed, null, "report"));
    }

    [Fact]
    public void Error_Constants_AreAnnotated_AndDistinct()
    {
        var id = Guid.NewGuid();
        var errors = new[]
        {
            RuntimeErrors.CapReached,
            RuntimeErrors.NotFound(id),
            RuntimeErrors.NotComplete(id),
        };
        Assert.All(errors, e => Assert.StartsWith("Error [", e));
        Assert.Equal(3, errors.Distinct().Count());
        Assert.Contains(id.ToString(), RuntimeErrors.NotFound(id));
        Assert.Contains(id.ToString(), RuntimeErrors.NotComplete(id));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/eThangAgent.Agent.Domain.Tests --nologo --filter FullyQualifiedName~RuntimeSeamTests`
Expected: FAIL — `IAgentRuntime`, `IAgentRunner`, `RuntimeErrors` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// IAgentRuntime.cs
namespace eThangAgent.AgentDomain;

/// <summary>Seam for starting persisted children as independent actors. Implemented in infrastructure; faked in tests.</summary>
public interface IAgentRuntime
{
    /// <summary>Begins background execution of an already-persisted Running record. Fails with CapReached when at capacity.</summary>
    Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default);
}

// IAgentRunner.cs
namespace eThangAgent.AgentDomain;

/// <summary>Runs a child's full conversation loop to completion. No validation, no persistence — the caller owns those.</summary>
public interface IAgentRunner
{
    Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default);
}

// RuntimeErrors.cs
namespace eThangAgent.AgentDomain;

public static class RuntimeErrors
{
    public const string CapReached =
        "Error [ConcurrencyCapReached]: The agent runtime is at its concurrent-agent limit. Retrieve pending results (agent.result) or wait, then retry.";

    public static string NotFound(Guid id) => $"Error [NotFound]: No agent exists with id '{id}'.";

    public static string NotComplete(Guid id) => $"Error [NotComplete]: Agent '{id}' has not finished running. Check agent.status later.";
}
```

- [ ] **Step 4: Run test to verify it passes** — same filter, expect PASS.
- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(agent-domain): runtime and runner seams with typed error constants"`

---

### Task 2: CQRS — `StartSpawnHandler` command + `AgentQueries`

**Files:**

- Create: `src/eThangAgent.Agent.Application/StartSpawnHandler.cs`
- Create: `src/eThangAgent.Agent.Application/AgentQueries.cs`
- Test: `tests/eThangAgent.Agent.Application.Tests/StartSpawnHandlerTests.cs`
- Test: `tests/eThangAgent.Agent.Application.Tests/AgentQueriesTests.cs`

**Interfaces:**

- Consumes: Task 1 seams; `ISubAgentSpawner`-owned specifications (`NonEmptyTaskPromptSpecification`, `ValidModelReferenceSpecification`); `IAgentStore` (exact methods from `src/eThangAgent.Agent.Domain/IAgentStore.cs`); `SubAgentOptions` (`DefaultModel`, `MaxDepth`).
- Produces: `StartSpawnHandler.Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default) → Task<Result<AgentId>>`; `AgentQueries.GetStatus(AgentId id, CancellationToken ct = default) → Task<Result<AgentRecord>>`; `AgentQueries.GetResult(AgentId id, …) → Task<Result<string>>`. Task 7 dispatches these; Task 3's fake-runtime tests assume this call order: validate → resolve model → `AgentRecord.Spawned(...)` → `store.SaveAsync` → `runtime.Start`.

Model resolution rule (moved out of the spawner, verbatim semantics): explicit `request.Model` wins; else `options.DefaultModel`; else fail `Error [MissingModel]: Provide a model reference or configure SubAgent:DefaultModel.` Depth guard: `parent.Depth >= options.MaxDepth` fails with the P4 depth-exceeded error text (copy from current `SubAgentSpawner.cs` — keep byte-identical so existing assertions hold).

- [ ] **Step 1: Write failing handler tests** — fake `IAgentStore` (in-memory dictionary + call log) and fake `IAgentRuntime`. Cover: happy path persists `Running` record then starts runtime with that record and returns its id; empty taskPrompt rejected with specification message and nothing persisted; whitespace model rejected; depth at `MaxDepth` rejected with the depth error; missing model with unconfigured default yields `Error [MissingModel]`; runtime cap failure propagates after the record was persisted.

- [ ] **Step 2: Run to verify FAIL** — types do not exist yet.
- [ ] **Step 3: Implement `StartSpawnHandler`** per the rule above; constructor `(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options)`.
- [ ] **Step 4: Handler tests PASS** — `dotnet test tests/eThangAgent.Agent.Application.Tests --filter FullyQualifiedName~StartSpawnHandler`.
- [ ] **Step 5: Write failing query tests** — fake store seeded with records: `GetStatus` returns the record; unknown id surfaces the store's failure verbatim (assert it equals what the fake returns for misses, and that `RuntimeErrors.NotFound` shape matches the store's not-found convention); `GetResult` on Running → `Error [NotComplete]`; on Completed → `Ok(FinalReport)`; on Failed → `Ok(FinalReport)` when partial report exists, else the failure-reason annotation line.
- [ ] **Step 6: Query tests FAIL**, then **Step 7: implement `AgentQueries`** `(IAgentStore store)`.
- [ ] **Step 8: Both PASS**, full project green: `dotnet test tests/eThangAgent.Agent.Application.Tests`.
- [ ] **Step 9: Commit** — `git commit -am "feat(agent-application): start-spawn command and status/result queries"`

---

### Task 3: `InProcessAgentRuntime` + new Agent.Infrastructure project

**Files:**

- Create: `src/eThangAgent.Agent.Infrastructure/eThangAgent.Agent.Infrastructure.csproj`
- Create: `src/eThangAgent.Agent.Infrastructure/InProcessAgentRuntime.cs`
- Create: `tests/eThangAgent.Agent.Infrastructure.Tests/eThangAgent.Agent.Infrastructure.Tests.csproj`
- Test: `tests/eThangAgent.Agent.Infrastructure.Tests/InProcessAgentRuntimeTests.cs`
- Modify: `eThangAgent.sln` (add both projects — `dotnet sln add`)

**Interfaces:**

- Consumes: Task 1's `IAgentRuntime`, `IAgentRunner`, `RuntimeErrors`; `IAgentStore.UpdateAsync`.
- Produces: `InProcessAgentRuntime(IAgentRunner runner, IAgentStore store, int maxConcurrentAgents)` implementing `IAgentRuntime`. Task 7 wires it; csproj mirrors `eThangAgent.Agent.Application.csproj` (net10.0, ProjectReferences: Agent.Domain, SharedKernel).

Constructor throws `ArgumentOutOfRangeException` when `maxConcurrentAgents < 1`. Cap uses `SemaphoreSlim(max, max)`; `Start` does a zero-timeout `Wait` — at capacity it returns `Result<AgentId>.Fail(RuntimeErrors.CapReached)` without side effects. Each started child runs on `Task.Run`: awaits the runner, persists `record with { Status, FailureReason, CompletedAt, FinalReport }` via `UpdateAsync`, releases the slot in `finally`. A thrown exception from the runner persists `Failed(ProviderError)` with `FinalReport = "Error [ProviderError]: " + ex.Message` — never an unobserved fault.

- [ ] **Step 1: Write failing tests** — fake runner driven by `TaskCompletionSource<AgentRunOutcome>`, fake store capturing updates. Cover: (a) Start returns `Ok(id)` immediately while the child is still running (no update yet); (b) completing the runner's task lands a `Completed` update carrying the report; (c) throwing runner lands `Failed(ProviderError)` update; (d) filling all slots blocks further starts with `CapReached`; (e) releasing one slot makes the next `Start` succeed.
- [ ] **Step 2: RED** — `dotnet test tests/eThangAgent.Agent.Infrastructure.Tests` fails to build (project missing).
- [ ] **Step 3: Create projects + implementation** exactly as specified above.
- [ ] **Step 4: GREEN** — same command passes.
- [ ] **Step 5: Commit** — `git commit -am "feat(agent-infrastructure): in-process agent runtime with concurrency cap"`

---

### Task 4: Spawner refactor — extract `RunAsync`, delete the synchronous path

**Files:**

- Modify: `src/eThangAgent.Agent.Domain/SubAgentSpawner.cs`
- Delete: `src/eThangAgent.Agent.Domain/ISubAgentSpawner.cs`
- Modify: every test referencing `SpawnAsync(` — locate with `rg -l 'SpawnAsync' tests src`

**Interfaces:**

- Consumes: Task 1's `IAgentRunner`.
- Produces: `SubAgentSpawner : IAgentRunner` — `RunAsync(AgentRecord child, CancellationToken ct = default) → Task<AgentRunOutcome>` containing everything after validation/persistence (model creation from `IModelProviderFactory`, transcript bootstrap, tool loop, budget enforcement, report rendering, `RunningChildCurrent` bookkeeping). `ISubAgentSpawner.SpawnAsync` is **deleted** along with its inline validation, depth guard, model resolution, and `SaveAsync` — that logic now lives solely in Task 2's handler. Constants (`MaxReportBytes`, `ChildMaxTokens`, `ChildTemperature`, `ReportOverflowAnnotation`) stay on the class.

- [ ] **Step 1: Mechanical extraction** — rename the post-validation body of `SpawnAsync(AgentRecord parent, SpawnRequest request, …)` into `RunAsync(AgentRecord child, …)`, switching parent-derived locals to the `child` parameter; change the class declaration to implement `IAgentRunner`; delete `ISubAgentSpawner.cs`.
- [ ] **Step 2: Fix the domain build** until `dotnet build src/eThangAgent.Agent.Domain` is green.
- [ ] **Step 3: Retarget tests** — loop-behavior tests construct a child record directly and call `RunAsync`; validation/depth/model-resolution tests are deleted here (they moved to `StartSpawnHandlerTests` in Task 2 — verify equivalent coverage exists before deleting). Integration tests that used the spawner end-to-end switch to handler + `InProcessAgentRuntime` (or a fake runtime where they assert loop internals).
- [ ] **Step 4: Full-domain GREEN** — `dotnet test tests/eThangAgent.Agent.Domain.Tests` and `rg -l 'SpawnAsync' tests src` returns no hits.
- [ ] **Step 5: Commit** — `git commit -am "refactor(agent-domain): extract child-loop runner, remove synchronous spawn path"`

---

### Task 5: `SqliteAgentStore` single-writer gate

**Files:**

- Modify: `src/eThangAgent.Storage.ACL/SqliteAgentStore.cs`
- Test: `tests/eThangAgent.Storage.ACL.Tests/SqliteAgentStoreConcurrencyTests.cs`

**Interfaces:**

- Consumes: existing `IAgentStore` methods (unchanged signatures — callers unaffected).
- Produces: same public surface; writes (`SaveAsync`, `UpdateAsync`, `AppendMessageAsync`) serialized internally via `SemaphoreSlim(1, 1)` with `WaitAsync(ct)` / `finally Release`. Reads (`GetAsync`, `GetTranscriptAsync`, `ListChildrenAsync`) stay direct.

- [ ] **Step 1: Write the failing concurrency test** — real `SqliteAgentStore` against a temp-file `AppDatabase` (mirror the connection setup from existing `SqliteAgentStoreTests.cs`): fire 20 `SaveAsync` calls for distinct records concurrently plus 10 concurrent `AppendMessageAsync` calls to one shared record; assert every save reports success, every id is retrievable via `GetAsync`, and the shared transcript contains all 10 messages. Run it against the CURRENT store first — if it already passes, keep the test as a regression guard and note that in the task report (SQLite may already serialize); the gate still becomes load-bearing the moment multiple processes/threads hit the same connection pattern.
- [ ] **Step 2: RED or noted-green**, then implement the gate exactly as specified.
- [ ] **Step 3: GREEN** — `dotnet test tests/eThangAgent.Storage.ACL.Tests` fully green (existing tests unaffected).
- [ ] **Step 4: Commit** — `git commit -am "feat(storage-acl): serialize agent store writes behind single-writer gate"`

---

### Task 6: `MaxConcurrentAgents` configuration (strict)

**Files:**

- Modify: `src/eThangAgent.Agent.Domain/SubAgentOptions.cs`
- Modify: `src/eThangAgent.CLI/SubAgentConfiguration.cs`
- Modify: `src/eThangAgent.CLI/Program.cs` (Bind call site — copy the adjacent `SubAgent:DefaultModel` retrieval pattern for `SubAgent:MaxConcurrentAgents`)
- Test: `tests/eThangAgent.Agent.Domain.Tests/SubAgentOptionsTests.cs` (extend)
- Test: `tests/eThangAgent.CLI.Tests/SubAgentConfigurationTests.cs` (extend)

**Interfaces:**

- Produces: `SubAgentOptions(string? DefaultModel, TimeSpan? ChildTimeout = null, int MaxConcurrentAgents = 1, int MaxDepth = 3)` — constructor throws `ArgumentOutOfRangeException` when `MaxConcurrentAgents < 1`; `SubAgentConfiguration.Bind(string? defaultModel, string? childTimeoutSeconds, string? maxConcurrentAgents)` where the new key is **required**: null → `InvalidOperationException("SubAgent:MaxConcurrentAgents is required. Set it to a positive integer.")`; non-integer → `InvalidOperationException($"SubAgent:MaxConcurrentAgents must be a positive integer, got '{maxConcurrentAgents}'.")`; `< 1` → `InvalidOperationException($"SubAgent:MaxConcurrentAgents must be at least 1, got '{maxConcurrentAgents}'.")`. Task 7 wires this value into the runtime.

- [ ] **Step 1: Write failing tests** — options: zero/negative rejected; config: missing key, `"abc"`, `"0"`, `"-2"` rejected, `"4"` accepted and flows into options; update every existing Bind call site with a valid value so the file compiles.
- [ ] **Step 2: RED**, **Step 3: implement** exactly as specified, **Step 4: GREEN** — both test projects fully green.
- [ ] **Step 5: Commit** — `git commit -am "feat(cli): required MaxConcurrentAgents configuration with strict validation"`

---

### Task 7: Capability surface — `spawn`/`status`/`result` + composition wiring

**Files:**

- Create: `src/eThangAgent.Agent.Domain/IAgentSpawnCommand.cs` — `Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)`
- Create: `src/eThangAgent.Agent.Domain/IAgentQueries.cs` — `Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)`; `Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)`
- Modify: `src/eThangAgent.Agent.Application/StartSpawnHandler.cs` (implement `IAgentSpawnCommand`), `src/eThangAgent.Agent.Application/AgentQueries.cs` (implement `IAgentQueries`)
- Modify: `src/eThangAgent.Agent.Domain/AgentCapabilityProvider.cs` — constructor becomes `(IAgentSpawnCommand spawn, IAgentQueries queries, Func<AgentRecord> parentContext)`; keep the P4 spawn-argument parsing block verbatim; add strict id parsing (`Guid.TryParseExact(value, "D", …)` else `Error [InvalidArgument]: 'id' must be a GUID string.`)
- Modify: `src/eThangAgent.CLI/Program.cs` — register handler, queries, and `InProcessAgentRuntime` (`new InProcessAgentRuntime(spawner, store, options.MaxConcurrentAgents)`); provider registration updated to the new constructor
- Test: `tests/eThangAgent.Agent.Domain.Tests/AgentCapabilityProviderTests.cs` (rewrite)

**Interfaces:**

- Consumes: Tasks 1–6 in full.
- Produces (output contracts, asserted verbatim in tests):
  - spawn success → `id=<guid> status=running`
  - status → `id=<guid> status=running` | `… status=completed` | `… status=failed reason=max-iterations|timeout|provider-error`
  - result completed → report verbatim; else `RuntimeErrors.NotComplete(id)` / `RuntimeErrors.NotFound(id)` / invalid-guid / `Error [UnknownAction]: …`

- [ ] **Step 1: Write failing provider tests** — fake command/queries: spawn happy path renders `status=running` line; spawn failure passes the handler's error string through untouched; status renders all three states incl. reason suffix; result returns report verbatim; `NotComplete` and `NotFound` pass through; malformed guid → `Error [InvalidArgument]…`; unknown action → `Error [UnknownAction]…`.
- [ ] **Step 2: RED**, **Step 3: implement** (interfaces, handler/queries declarations, provider dispatch, Program wiring), **Step 4: GREEN** — `dotnet test tests/eThangAgent.Agent.Domain.Tests tests/eThangAgent.Agent.Application.Tests` green; `dotnet build` solution green.
- [ ] **Step 5: Commit** — `git commit -am "feat(agent): async spawn, status, and result capability actions"`

---

### Task 8: Guide v1.4 — non-blocking delegation

**Files:**

- Modify: `src/eThangAgent.CLI/ExecGuidePromptProvider.cs` — rewrite the "Delegating subtasks" section; bump the guide version constant `1.3` → `1.4` (locate with `rg -n "1\.3" src/eThangAgent.CLI/ExecGuidePromptProvider.cs`)
- Test: `tests/eThangAgent.CLI.Tests` — update the guide-content assertions (locate with `rg -l 'Delegating|v1\.3' tests`)

**Interfaces:**

- Consumes: Task 7's contracts.
- Produces: guide section teaching, in order: (1) `agent.spawn` returns immediately with `id=<guid> status=running` — never wait on it inside the spawn call; (2) continue useful work or fan out siblings for parallel independent subtasks; (3) poll `agent.status` between turns; (4) fetch `agent.result` for the final report — `NotComplete` means try again later, `NotFound` means the id is wrong; (5) `ConcurrencyCapReached` means retrieve pending results before spawning more; (6) depth limit 3 unchanged.

- [ ] **Step 1: Rewrite failing first** — update guide tests to assert the new teaching lines and version `1.4`; run RED.
- [ ] **Step 2: Rewrite the section** in `ExecGuidePromptProvider.cs` to satisfy them; **Step 3: GREEN** — CLI test project green.
- [ ] **Step 4: Commit** — `git commit -am "docs(guide): v1.4 non-blocking delegation teaching"`

---

### Task 9: E2E — async nested spawn through mock OpenRouter

**Files:**

- Modify: `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs`
- Modify: `tests/eThangAgent.CLI.Tests/E2ETests.cs` — rewrite `Repl_NestedSpawn_ChildRunsAndReports` for the async contract

**Interfaces:**

- Consumes: Task 7 output contracts; existing model-keyed scripting and decoded-JSON assertion patterns in these files.
- Produces: mock feature `{{child_id}}` substitution — before serving a scripted response, the server scans the request body for the most recent tool-role message matching `\[agent\] id=([0-9a-fA-F-]{36})` and replaces every `{{child_id}}` occurrence in the scripted program with that guid. This is required because child ids are runtime Guids no static script can predict.

Parent script (model-keyed, e.g. `mock/sub-parent`): turn 1 → one ToolCallRequest whose program runs `agent.spawn @{ taskPrompt = 'Say child report done and nothing else.'; model = 'mock/sub-model'; label = 'e2e' }`; turn 2 → program running `agent.status @{ id = '{{child_id}}' }`; turn 3 → program running `agent.result @{ id = '{{child_id}}' }`; turn 4 → final text `done: child reported`. Child script (`mock/sub-model`): first call → Write-Output `child report done` via exec; second call → final text.

- [ ] **Step 1: Failing substitution unit check** — feed a canned request body containing a tool message with `id=12345678-1234-1234-1234-123456789abc` and assert the served program contains that guid (RED first).
- [ ] **Step 2: Implement substitution**, GREEN.
- [ ] **Step 3: Rewrite the E2E** asserting: (a) the spawn action result reaches the transcript as `status=running` and contains **no** report text (non-blocking proof); (b) at least one wire request carries top-level `"model":"mock/sub-model"`; (c) a later decoded tool message contains `child report done` (the result fetched by the parent); (d) the final assistant reply contains `done:`. Run the single E2E, then the whole CLI project.
- [ ] **Step 4: SWEEP** — `taskkill //F //IM testhost.exe 2>/dev/null; taskkill //F //IM eThangAgent.CLI.exe 2>/dev/null`.
- [ ] **Step 5: Commit** — `git commit -am "test(cli): e2e async nested spawn through mock openrouter"`

---

### Task 10: Full-solution gate, coverage, outcome docs

**Files:**

- Modify: `docs/skills/plans/2026-08-21-async-agents-mesh.md` (this file — checkbox marks + Outcome section)

**Interfaces:** none — verification-only task.

- [x] **Step 1:** `dotnet build --nologo -v q` — zero errors.
- [x] **Step 2:** `dotnet test --nologo` — all suites green; record totals.
- [x] **Step 3:** Coverage — `dotnet test tests/eThangAgent.Agent.Application.Tests --collect:'XPlat Code Coverage'` and same for `tests/eThangAgent.Agent.Infrastructure.Tests`; parse each `coverage.cobertura.xml` line-rate for `eThangAgent.AgentApplication*` / `eThangAgent.AgentInfrastructure*` classes; floor 80% — add targeted tests if short, noting what was added.
- [x] **Step 4:** Process sweep — no leaked testhost/CLI processes.
- [x] **Step 5:** Mark this task's checkboxes `- [x]`, append `## Outcome` recording suite totals, coverage actuals, deviations (if any), and the commit list; commit `git commit -am "docs(plan): P5 async agents complete"`.

## Outcome — 2026-08-21 (P5 complete)

- Full solution: 15/15 suites, 389 tests, zero failures. CLI.Tests 40/40 incl. rewritten
  nested-spawn E2E.
- Coverage (own-code classes): StartSpawnHandler 1.0, AgentQueries 0.92–0.94,
  InProcessAgentRuntime 1.0 — above the 80% floor. Whole-run aggregates (19%/9%) are
  diluted by referenced assemblies and reported for context only.
- Deviation from plan text: the guide lives in `Tool.Domain/ExecGuide.cs` (+
  ExecGuideTests.cs), not `CLI/ExecGuidePromptProvider.cs`; version bump landed there.
  Plan text stands corrected for future re-reads.
- Commits: 4030d8f, 325cb2a, b9fd356, ec9463f, 1ffa2c1, b9b12e6, 0f3df8b, 0f3bd51,
  a836d67, + this docs commit.

---
