
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
