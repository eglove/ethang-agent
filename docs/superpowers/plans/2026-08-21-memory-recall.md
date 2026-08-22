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

### Task 1: New Memory.Domain project — tokenizer + query planner

**Files:**

- Create: `src/eThangAgent.Memory.Domain/eThangAgent.Memory.Domain.csproj` (net10.0; ProjectReferences: SharedKernel, Conversation.Domain)
- Create: `src/eThangAgent.Memory.Domain/LexicalTokenizer.cs`
- Create: `src/eThangAgent.Memory.Domain/MemoryQueryPlan.cs`
- Create: `tests/eThangAgent.Memory.Domain.Tests/eThangAgent.Memory.Domain.Tests.csproj` + `GlobalUsings.cs` (`global using Xunit;`)
- Test: `tests/eThangAgent.Memory.Domain.Tests/LexicalTokenizerTests.cs`, `MemoryQueryPlanTests.cs`
- Modify: solution (dotnet sln add both)

**Interfaces:**

- Produces: `LexicalTokenizer.Tokenize(string text) : IReadOnlyList<string>`; `abstract record MemoryQueryPlan` with nested `Browse`, `Terms(IReadOnlyList<string> Tokens)`, `RegexPattern(string Pattern)`; `MemoryQueryPlan.Plan(string? query, string queryMode = "literal")`.

Semantics ported from pi-fabric `tokenize.ts`: NFKC normalize → matches of `[\p{L}\p{N}_]+` → lowercase (invariant). `Plan`: null/whitespace → `Browse`; `queryMode="literal"` → `Terms` with DISTINCT tokens in first-occurrence order (literal input never compiled as regex — "a.c" yields tokens `[a, c]`); `"regex"` → `RegexPattern(query)` raw, unvalidated here; unknown mode → `ArgumentException` (programmer error — capability layer validates strings before calling).

- [ ] **Step 1:** Failing tests — tokenizer: ASCII words, Unicode letters (Cyrillic), digits+underscore kept, punctuation splits, NFKC ligature `ﬁ`→`fi`, case fold; planner: whitespace/null → Browse; literal metacharacters → terms; distinct-first-order terms; regex passthrough.
- [ ] **Step 2:** RED — projects do not exist.
- [ ] **Step 3:** Implement exactly the semantics above.
- [ ] **Step 4:** GREEN — full new test project passes.
- [ ] **Step 5:** Commit — `feat(memory-domain): lexical tokenizer and query planning`

---

### Task 2: Bounded regex executor

**Files:**

- Create: `src/eThangAgent.Memory.Domain/BoundedRegex.cs`
- Test: `tests/eThangAgent.Memory.Domain.Tests/BoundedRegexTests.cs`

**Interfaces:**

- Consumes: SharedKernel `Result<T>`.
- Produces: `static class BoundedRegex { public const int MaxPatternBytes = 1024; public const int MaxHaystackBytes = 2 * 1024 * 1024; public const int TimeoutMs = 250; public static Result<IReadOnlyList<int>> Execute(string pattern, IReadOnlyList<string> haystacks) }` — returns Ok(matched indices) or Fail with EXACT strings: `Error [regex_pattern_too_large]: Regex pattern exceeds 1024 bytes.` / `Error [invalid_regex]: <message>` / `Error [regex_timeout]: Regex exceeded the 250 ms budget.`

Semantics: UTF-8 pattern byte check BEFORE compile; compile `new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs))` catching `ArgumentException` → invalid_regex; per-haystack content longer than MaxHaystackBytes is TRUNCATED to the cap before testing (documented deviation-for-simplicity: pi-fabric enforces the budget across the batch; ours is per-entry, seam noted for batch accounting); `RegexMatchTimeoutException` → regex_timeout (never propagates). Match = regex.IsMatch per haystack; return indices in order.

- [ ] **Step 1:** Failing tests — oversized ASCII pattern (1100 × "a") → too_large; `(\[)` → invalid_regex whose message contains "Invalid pattern"; catastrophic `(a+)+$` against 5000 `a`s + trailing `b` under default budget → regex_timeout within ~2 s wall clock; `/hello/i`-style case-insensitive match across indices; haystack >2 MiB truncated so a match beyond the cap is NOT found.
- [ ] **Step 2:** RED → **Step 3:** implement → **Step 4:** GREEN.
- [ ] **Step 5:** Commit — `feat(memory-domain): bounded regex executor with typed failures`

---

### Task 3: Scopes, branch resolution, and the search service

**Files:**

- Create: `src/eThangAgent.Memory.Domain/SessionScope.cs`, `BranchMode.cs`, `MemoryEntry.cs`, `SessionCorpus.cs`, `SearchService.cs`
- Test: `tests/eThangAgent.Memory.Domain.Tests/SearchServiceTests.cs`

**Interfaces:**

- Produces: `abstract record SessionScope` with `Global`, `Session(AgentId)` and `static Result<SessionScope> Parse(string? raw)` (null/"global" → Global; "session:<guid>" → Session; anything else → `Error [InvalidScope]: Unknown scope '<raw>'. Valid scopes: global | session:<agentId>.`); `enum BranchMode { ActivePath, AllBranches }`; `record MemoryEntry(AgentId Session, int Seq, string Role, string Content, DateTimeOffset Timestamp)`; `record SessionCorpus(AgentId Id, AgentId? ParentId, int Depth, IReadOnlyList<MemoryEntry> Entries)`; `SearchService.Search(IReadOnlyList<SessionCorpus> sessions, MemoryQueryPlan plan, SessionScope scope, BranchMode branches, string? role, int page, int pageSize) : SearchResult` where `SearchResult(Hits, TotalMatched, Page, Pages)`, `Hit(MemoryEntry Entry)`.

Semantics: scope filters to one session or keeps all; ActivePath keeps only sessions whose ParentId walk terminates at a root (orphan chains whose ancestor row is absent are EXCLUDED — that is the observable branch difference); role filter drops non-matching entries before search; Browse returns entries newest-first (Timestamp desc, then Seq desc, then Session ordinal); Terms requires EVERY token present in the entry's token set (AND); Regex delegates to `BoundedRegex.Execute` over candidate contents, dropping entries on regex failure surfaces the FIRST failure as the service's own `Result`-style failure (Search returns a result wrapper: `SearchOutcome` Ok(SearchResult) | Fail(string)). Ordering identical across modes. Paging slices the flat ordered hit list; `Pages` = ceiling(Total/pageSize), minimum 1.

- [ ] **Step 1:** Failing tests — scope parse happy/sad paths (exact error strings); orphan exclusion under ActivePath vs inclusion under AllBranches; AND-token matching incl. token not present anywhere → zero hits; regex mode finding matches by index; role filter; browse order; paging math (total 25, size 10 → 3 pages, last page 5 hits); empty corpus → 0 hits, 1 page.
- [ ] **Step 2:** RED → **Step 3:** implement → **Step 4:** GREEN.
- [ ] **Step 5:** Commit — `feat(memory-domain): scoped branch-aware search service`

---

### Task 4: Root conversation persistence

**Files:**

- Modify: `src/eThangAgent.Agent.Domain/AgentRecord.cs` — add `public static AgentRecord Root(AgentId id, DateTimeOffset createdAt)` → depth 0, `ParentId null`, status Running, `ModelUsed "unassigned"`, label `"root"`, task prompt `"conversation root"` (doc comment explains the sentinel).
- Modify: `src/eThangAgent.CLI/Program.cs` — REPL startup creates `rootId = AgentId.NewId()`, saves the Root record; after each completed exchange appends the user message then assistant message via `store.AppendMessageAsync(rootId, …)`; on `/exit` updates the record to `Completed` with `CompletedAt`.
- Test: `tests/eThangAgent.Storage.ACL.Tests/RootSessionRoundTripTests.cs` — real `SqliteAgentStore` on temp file.

**Interfaces:**

- Produces: root sessions are ordinary rows — every later task reads them uniformly. No new interfaces.

- [ ] **Step 1:** Failing round-trip test — save Root record; assert depth 0/label/task prompt sentinels; append two messages; `GetTranscriptAsync` returns them in order; update to Completed persists status+timestamp.
- [ ] **Step 2:** RED → **Step 3:** implement factory + CLI wiring (append points: immediately after `SendMessageAsync` resolves, write user+assistant pair; wrap `/exit` path) → **Step 4:** GREEN plus full solution build.
- [ ] **Step 5:** Commit — `feat(cli): persist root conversation as depth-0 agent session`

---

### Task 5: Recall and sessions query handlers

**Files:**

- Modify: `src/eThangAgent.Agent.Domain/IAgentStore.cs` — add `Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)`; implement in `SqliteAgentStore` and every fake.
- Create: `src/eThangAgent.Agent.Application/Memory/RecallQueryHandler.cs`, `SessionsQueryHandler.cs`
- Test: `tests/eThangAgent.Agent.Application.Tests/Memory/RecallQueryHandlerTests.cs`, `SessionsQueryHandlerTests.cs`

**Interfaces:**

- Consumes: Tasks 1–4 (`LexicalTokenizer`, `MemoryQueryPlan.Plan`, `BoundedRegex`, `SessionScope.Parse`, `SearchService`, `IAgentStore`).
- Produces: `RecallQueryHandler.Execute(string? query, string queryMode, string? scope, string branches, string? role, int page, int pageSize) : Task<Result<RecallPage>>`; `RecallPage(Hits, TotalMatched, Page, Pages)` with `Hit(AgentId Session, int Seq, string Role, string Content, DateTimeOffset Timestamp)`; `SessionsQueryHandler.Execute(string? scope, string branches, int limit) : Task<Result<IReadOnlyList<SessionSummary>>>`; `SessionSummary(AgentId Id, string Label, int Depth, int EntryCount, string Status, string Tier)` where Tier is always `"hot"`.

Validation (exact strings): pageSize outside 1..200 → `Error [InvalidArgument]: pageSize must be between 1 and 200.`; page < 1 → `Error [InvalidArgument]: page must be at least 1.`; scope → pass `SessionScope.Parse` failure through untouched; queryMode not literal/regex → `Error [InvalidArgument]: queryMode must be 'literal' or 'regex'.`; role present-but-invalid → `Error [InvalidArgument]: role must be 'user', 'assistant', or 'tool'.`; limit outside 1..500 → `Error [InvalidArgument]: limit must be between 1 and 500.` Handler builds `SessionCorpus` list from `ListAllAsync` (scope=global) or `GetAsync`+transcript (session scope), resolves branch mode, plans the query (tokenizer inside `Plan`), calls `SearchService`, maps to DTOs.

- [ ] **Step 1:** Extend store interface + Sqlite implementation + test fakes; failing handler tests cover every validation string above, browse ordering, literal AND, regex timeout surfacing as failure result, active-vs-all branch counts with an orphan fixture, sessions summary shape incl. entry counts and tier constant.
- [ ] **Step 2:** RED → **Step 3:** implement → **Step 4:** GREEN (both memory handler suites + Storage suite still green).
- [ ] **Step 5:** Commit — `feat(agent-application): recall and sessions query handlers`

---

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

### Task 7: Guide v1.5 — "Recalling earlier work"

**Files:**

- Modify: `src/eThangAgent.Tool.Domain/ExecGuide.cs` — bump version constant `1.4` → `1.5` (`rg -n '1\.4' src/eThangAgent.Tool.Domain/ExecGuide.cs`); add the new section after the delegation section
- Test: locate assertions with `rg -l 'v1\.4|Delegating' tests` and extend

**Interfaces:**

- Produces: section teaching, in order: (1) `memory.sessions` lists what conversations exist — run it when resuming work or before duplicating effort; (2) `memory.recall` searches transcripts — literal default (tokens ANDed), `queryMode='regex'` optional with budget errors (`regex_pattern_too_large` / `invalid_regex` / `regex_timeout`) that mean *simplify the pattern or use literal mode*; (3) scopes `global` or `session:<id>`; branches `active` vs `all`; (4) paging for long result sets; (5) memory is READ-ONLY — nothing to save yet.

- [ ] **Step 1:** Failing test updates (version + new teaching lines) → RED.
- [ ] **Step 2:** Rewrite section → GREEN on CLI.Tests non-E2E suite + full build.
- [ ] **Step 3:** Commit — `docs(guide): v1.5 recalling earlier work`

---

### Task 8: E2E seed-and-recall, full gate, outcome docs

**Files:**

- Test: `tests/eThangAgent.CLI.Tests/E2ETests.cs` — new `Repl_MemoryRecall_AgainstMockServer`
- Modify: `docs/superpowers/plans/2026-08-21-memory-recall.md` (this file — checkboxes + Outcome)

**Interfaces:** none new — verification task.

- [ ] **Step 1:** New E2E against the mock server: parent script turn 1 answers an exchange containing the distinctive phrase `xylophone harvest`; turn 2 runs `memory.sessions @{ limit = 50 }`; turn 3 runs `memory.recall @{ query = 'xylophone'; scope = 'global' }`; final text `recalled.`. Assert decoded tool messages: a `session=` line whose depth is 0 (the persisted root); a `[mem]` hit whose content contains `xylophone harvest`; the footer regex `--- memory: \d+ hits, page 1/\d+ ---`. Run this E2E alone first; SWEEP (`taskkill //F //IM testhost.exe 2>/dev/null; taskkill //F //IM eThangAgent.CLI.exe 2>/dev/null`) after EVERY dotnet test invocation.
- [ ] **Step 2:** Full CLI.Tests green (incl. E2Es); full solution `dotnet test` green — record totals.
- [ ] **Step 3:** Coverage probe: `dotnet test tests/eThangAgent.Memory.Domain.Tests --collect:'XPlat Code Coverage'`; per-class line-rate for `eThangAgent.MemoryDomain.*` classes ≥ 0.80 — add targeted tokenizer/regex/search tests if short, noting additions.
- [ ] **Step 4:** Mark this task's checkboxes `- [x]`, append `## Outcome` with suite totals, coverage actuals, deviations (if any), and the commit list; commit `git commit -am "docs(plan): P6 memory recall complete"`.

---
