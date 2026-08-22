
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
