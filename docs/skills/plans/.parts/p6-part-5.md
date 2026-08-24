
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
