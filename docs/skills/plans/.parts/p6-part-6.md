
### Task 6: `memory` capability provider and wiring

**Files:**

- Create: `src/eThangAgent.Memory.Domain/RecallPage.cs` (with `Hit`), `SessionSummary.cs`
- Create: `src/eThangAgent.Agent.Domain/IMemoryRecallQuery.cs`, `IMemorySessionsQuery.cs` — signatures exactly mirroring Task 5 handler methods, returning `Task<Result<RecallPage>>` / `Task<Result<IReadOnlyList<SessionSummary>>>`
- Modify: `RecallQueryHandler` / `SessionsQueryHandler` — declare interface implementations
- Create: `src/eThangAgent.Agent.Domain/MemoryCapabilityProvider.cs` — Id `"memory"`, Actions `recall` + `sessions` (reuse existing ActionDescriptor construction style; descriptions document the output contracts verbatim)
- Modify: `Program.cs` — register both queries as singletons; add the provider to `CapabilityRegistry.Create`'s provider list as a TOP-LEVEL entry (id `memory` is its own group — do NOT fold into the `agent` MergedCapabilityProvider)
- Test: `tests/eThangAgent.Agent.Domain.Tests/MemoryCapabilityProviderTests.cs`

**Interfaces:**

- Consumes: Task 5 handlers.
- Produces (verbatim contracts): hit line `[mem] session=<id> seq=<n> role=<r> <snippet>` where snippet is Content with newlines collapsed to spaces truncated to 120 chars; success footer `--- memory: <total> hits, page <p>/<pages> ---`; zero-hit result renders footer only. Sessions render one line per summary: `session=<id> label=<label> depth=<d> entries=<n> status=<s> tier=hot`. Argument parsing is strict: unknown key → `Error [InvalidArgument]: unknown argument '<key>'.`; wrong JSON type → `Error [InvalidArgument]: argument '<key>' must be a number.` (or `a string.`); everything else passes through to the queries untouched.

- [ ] **Step 1:** Failing provider tests — fake queries: recall happy path renders hits + correct footer arithmetic; empty result footer-only; sessions rendering incl. tier constant; unknown-key and wrong-type rejections; malformed `session:` scope surfaces InvalidScope untouched; unknown action standard error.
- [ ] **Step 2:** RED → **Step 3:** implement (interfaces, DTO homes, provider, registration) → **Step 4:** GREEN on Domain tests plus full solution build.
- [ ] **Step 5:** Commit — `feat(agent): memory recall and sessions capability actions`

---
