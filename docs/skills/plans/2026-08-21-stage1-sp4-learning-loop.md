# Stage 1 / SP4 — Learning Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the learning loop: agent-curated persistent memories (searchable SQLite + FTS, categorized/tagged, versioned), periodic nudges prompting curation at natural boundaries, and post-complex-task skill creation through the EXISTING `skill_manage` surface — per spec `docs/skills/specs/2026-08-21-stage-1-methodology-port-design.md` (SP4).

**Architecture:** New `CuratedMemory` aggregate in Memory.Domain — deliberately NOT named MemoryEntry, which is already the transcript-recall hit type. Persistence via Storage ACL V4 migration: `curated_memories` table (CAS row version) + an FTS5 index kept in sync by triggers. Model surface: a new `CuratedMemoryCapabilityProvider` following the State/Memory capability-action house style (actions ride `exec`, like recall/sessions/state.*). Nudges: `Conversation` gains `AddSystemMessage` (Role.System already maps on the wire); `Agent` gains `LastTurnToolCalls`; `SendMessageCommandHandler` evaluates an injected `INudgePolicy` after each completed turn and appends the returned reminder as a System message. Self-improvement reuses SP2's `skill_manage` unchanged — this plan adds only the prompt guidance that invites proposals.

**Tech Stack:** C# / .NET 10, xUnit, SQLite FTS5 (bundled engine supports it — verified via a migration smoke test), System.Text.Json.

**Spec:** `docs/skills/specs/2026-08-21-stage-1-methodology-port-design.md`

## Global Constraints

- Strict boundaries: unknown parameters rejected; enums exact (`category` ∈ convention/preference/insight/failure/reference, `scope` ∈ workspace/global — lowercase ordinal); errors as typed `Error [Code]:` lines; nothing coerced.
- Content bounded: required non-empty after trim, ≤ 4000 chars (violation names limit+actual). Tags: 0–12 tags, each trimmed non-empty ≤ 32 chars matching ^[a-z0-9][a-z0-9-_]*$, deduped ordinally (duplicate input tags collapse silently — they are labels, not assertions).
- Updates are CAS: stale `expected_version` rejected with `VersionConflict` naming the current version. Removal requires `confirm: true`.
- System prompt carries USAGE GUIDANCE only; never bulk memory content.
- Nudges are append-only System messages at turn boundaries — event-driven, no polling, no background threads; never more than one per turn.
- No hidden background writer for skills: creation happens when the MODEL chooses to call skill_manage, prompted by guidance.
- Every task ends green; DI only in Program.cs; README in final task.

## File Structure

```text
src/eThangAgent.Memory.Domain/
  CuratedMemory.cs               # NEW — aggregate + Category/Scope enums + specifications
  ICuratedMemoryStore.cs         # NEW — persistence seam (CAS + search)
  CuratedMemoryCapabilityProvider.cs  # NEW — search/add/update/remove actions
tests/eThangAgent.Memory.Domain.Tests/
  CuratedMemoryTests.cs          # NEW
  CuratedMemoryCapabilityProviderTests.cs  # NEW
src/eThangAgent.Storage.ACL/
  AppDatabase.cs                 # MODIFY — ApplyV4 (table + FTS5 + triggers)
  SqliteCuratedMemoryStore.cs    # NEW
tests/eThangAgent.Storage.ACL.Tests/
  SqliteCuratedMemoryStoreTests.cs  # NEW
src/eThangAgent.Conversation.Domain/
  Conversation.cs                # MODIFY — AddSystemMessage
src/eThangAgent.Agent.Domain/
  Agent.cs                       # MODIFY — LastTurnToolCalls
tests/eThangAgent.Conversation.Domain.Tests/, tests/eThangAgent.Agent.Domain.Tests/
                                 # MODIFY — coverage for the two additions
src/eThangAgent.Agent.Application/
  Nudges/NudgeContext.cs, INudgePolicy.cs, DefaultNudgePolicy.cs   # NEW
  SendMessageCommandHandler.cs   # MODIFY — post-turn nudge evaluation
tests/eThangAgent.Agent.Application.Tests/
  NudgeTests.cs                  # NEW (+ handler integration test)
src/eThangAgent.CLI/
  CuratedMemoryGuidePromptProvider.cs  # NEW — usage-guidance prompt segment
  Program.cs                     # MODIFY — all wiring
README.md                        # MODIFY
```

---

### Task 1: `CuratedMemory` aggregate + store seam

**Files:**

- Create: `src/eThangAgent.Memory.Domain/CuratedMemory.cs`, `src/eThangAgent.Memory.Domain/ICuratedMemoryStore.cs`
- Test: `tests/eThangAgent.Memory.Domain.Tests/CuratedMemoryTests.cs`

**Interfaces:**

```csharp
public enum MemoryCategory { Convention, Preference, Insight, Failure, Reference }
public enum MemoryScope { Workspace, Global }

public sealed record CuratedMemory(
    Guid Id,
    string WorkspaceId,          // empty string ⇒ Global scope row
    MemoryCategory Category,
    IReadOnlyList<string> Tags,
    string Content,
    string? UsageHint,
    MemoryScope Scope,
    string? ProvenanceSession,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

`CuratedMemorySpecifications` (GeneratedRegex where applicable): `ValidTag` (^[a-z0-9][a-z0-9-_]{0,31}$); static helpers `NormalizeTags(IEnumerable<string>)` returning deduped validated list (throws ArgumentException on invalid tag — caller validates first); content limit constant `MaxContentChars = 4000`.

`ICuratedMemoryStore`:

```csharp
public interface ICuratedMemoryStore
{
    Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default);
    Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Ranked: query matches via FTS when non-empty, else newest-updated first.
    /// Rows visible: scope Global always; scope Workspace only when workspaceId matches.</summary>
    Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
        string? workspaceId, string? query, MemoryCategory? category,
        IReadOnlyList<string>? tags, int limit, CancellationToken ct = default);
    /// <summary>CAS: fails VersionConflict unless memory.Version equals stored version.</summary>
    Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default);
    Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
```

Error codes: `VersionConflict` (names current version), `MemoryNotFound`, `StorageError`.

- [ ] **Step 1: Failing tests** (~12): tag normalization (dedup ordinal, preserves first-seen order, rejects invalid charset/length via ArgumentException), ValidTag boundaries (1-char ok, leading `-`/`_` reject, 32-char ok, 33 reject), category/scope enums parse from exact lowercase strings via helper `Parse(string)` with typed error listing allowed values, MaxContentChars constant value, record immutability (with-expression yields new instance). Full assertions each.

- [ ] **Step 2: Red**, implement, green. Commit: `feat(memory-domain): curated memory aggregate and store seam`

### Task 2: V4 migration + `SqliteCuratedMemoryStore`

**Files:**

- Modify: `src/eThangAgent.Storage.ACL/AppDatabase.cs` (ApplyV4 + gate)
- Create: `src/eThangAgent.Storage.ACL/SqliteCuratedMemoryStore.cs`
- Test: `tests/eThangAgent.Storage.ACL.Tests/SqliteCuratedMemoryStoreTests.cs`

**Interfaces:**

- Consumes: AppDatabase migration pattern (ApplyV1–V3), `ICuratedMemoryStore` from Task 1. Storage.ACL gains a project reference to Memory.Domain (Memory.Domain does NOT reference Storage — no cycle).

V4 SQL:

```sql
CREATE TABLE IF NOT EXISTS curated_memories (
    id            TEXT PRIMARY KEY,
    workspace_id  TEXT NOT NULL,
    category      TEXT NOT NULL,
    tags          TEXT NOT NULL,
    content       TEXT NOT NULL,
    usage_hint    TEXT NULL,
    scope         TEXT NOT NULL,
    provenance    TEXT NULL,
    version       INTEGER NOT NULL,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_curated_ws ON curated_memories (workspace_id, scope);
CREATE VIRTUAL TABLE IF NOT EXISTS curated_memories_fts USING fts5(
    content, tags, usage_hint, content='curated_memories', content_rowid='rowid'
);
CREATE TRIGGER IF NOT EXISTS curated_ai AFTER INSERT ON curated_memories BEGIN
    INSERT INTO curated_memories_fts(rowid, content, tags, usage_hint)
    VALUES (new.rowid, new.content, new.tags, new.usage_hint);
END;
CREATE TRIGGER IF NOT EXISTS curated_ad AFTER DELETE ON curated_memories BEGIN
    INSERT INTO curated_memories_fts(curated_memories_fts, rowid, content, tags, usage_hint)
    VALUES ('delete', old.rowid, old.content, old.tags, old.usage_hint);
END;
CREATE TRIGGER IF NOT EXISTS curated_au AFTER UPDATE ON curated_memories BEGIN
    INSERT INTO curated_memories_fts(curated_memories_fts, rowid, content, tags, usage_hint)
    VALUES ('delete', old.rowid, old.content, old.tags, old.usage_hint);
    INSERT INTO curated_memories_fts(rowid, content, tags, usage_hint)
    VALUES (new.rowid, new.content, new.tags, new.usage_hint);
END;
```

tags stored as JSON array string; enums as lowercase names; ISO-8601 timestamps.

Search semantics: when `query` non-empty → FTS MATCH over the external-content index joined back to the source table, ordered by bm25(); tokens whitespace-split ANDed (quote each token with double quotes to defuse FTS syntax). When empty → all visible rows ordered updated_at DESC. Category/tags/scope filters apply as WHERE predicates on the source table in BOTH modes. Limit clamped 1..100 by caller (store trusts limit ≥ 1).

Update CAS: `UPDATE … WHERE id=@id AND version=@expected`; rows-affected 0 → distinguish VersionConflict vs MemoryNotFound by a prior SELECT.

- [ ] **Step 1: Failing integration tests** (~15 cases, real temp DB fixture per class):

1. Add→Get round-trips every field incl. provenance null and set.
2. Global row visible via ANY workspaceId; workspace row visible only to matching id (search both ways).
3. Search query FTS hit ranks matching row first; non-matching excluded.
4. Multi-token AND semantics: both tokens required.
5. Query with FTS special chars (`test; DROP`, `a"b`) executes safely and matches nothing rather than erroring (quoting defense).
6. Category filter exact; unknown category impossible at type level (enum) — filter test uses valid values only.
7. Tags filter: row with [api, sql] found by tags=[sql] and by [api,sql]; not found by [nope].
8. Update happy path bumps version, updates updated_at, history-free (no history table here).
9. Update stale version → VersionConflict naming current.
10. Delete removes row AND its FTS entry (subsequent search finds nothing); Delete unknown → false.
11. Migration smoke: `SELECT count(*) FROM curated_memories_fts` succeeds post-init (FTS5 available).
12. Empty-query search returns newest-updated first ordering.
13. Limit respected.
14. Content ≤4000 boundary round-trips.
15. Reopen same DB file → all rows still searchable (persistence across connections).

- [ ] **Step 2: Red**, implement, green. FULL Storage.ACL.Tests green.

- [ ] **Step 3: Commit** — `feat(storage): curated memory store with FTS5 search and CAS updates`

### Task 3: `CuratedMemoryCapabilityProvider`

**Files:**

- Create: `src/eThangAgent.Memory.Domain/CuratedMemoryCapabilityProvider.cs`
- Test: `tests/eThangAgent.Memory.Domain.Tests/CuratedMemoryCapabilityProviderTests.cs`
- Modify: `src/eThangAgent.Memory.Domain/eThangAgent.Memory.Domain.csproj` — explicit project reference to Capability.Domain (transitive today; make it deliberate)

**Interfaces:**

- Consumes: `ICuratedMemoryStore`, `Func<string?> provenanceAccessor` (ambient session id, may return null).
- Produces: `ICapabilityProvider`, `ProviderId = "memories"`, four actions. Argument parsing mirrors `StateCapabilityProvider`'s helpers (read it first; copy the ParseArgs/Allowed/ReqString/OptString/ToInt pattern verbatim, adapting names).

Actions (JSON args):

- **search**: all optional — `query`, `category`, `tags` (string array), `scope` (exactly `workspace | global`; filters to that scope), `limit` (default 20, clamped 1..100 with visible `[warning] limit clamped to 100` line when overshot). Output:

```text
[memories] N hit(s)
[mem] id=<first8> v<n> cat=<category> scope=<scope> tags=t1,t2 :: <content ≤120 chars>
     hint: <usage_hint ≤80 chars>          (only when present)
```

Zero hits → `[memories] 0 hit(s)`. Overshoot clamp warning appended after the list.

- **add**: `content` required (trim, non-empty → MissingContent; >4000 → ContentTooLong naming limit+actual), `category` required exact lowercase (`MissingCategory` / `InvalidCategory` listing five), `tags` optional array (invalid tag element → InvalidTag quoting the rule; >12 tags → TooManyTags), `usage_hint` optional ≤200 (`HintTooLong`), `scope` required exact (`workspace|global`) → workspace rows keyed by the service's injected workspace id, `session` NOT accepted from the model (provenance is ambient). Output: `[memories] added <first8> v1 (cat=<c> scope=<s>)`. Increments the injected write-counter.

- **update**: `id` required (parse as Guid → InvalidId), `expected_version` required int ≥ 1 (MissingVersion), at least one of `content` / `category` / `tags` / `usage_hint` required (NothingToUpdate); semantics per Global Constraints; fetch → apply deltas on fresh copy with Version=stored Version+1, UpdatedAt=clock → store.UpdateAsync CAS → VersionConflict surfaces naming current version. Output: `[memories] updated <first8> v<n>`.

- **remove**: `id` required, `confirm` required exactly boolean true (RemoveNotConfirmed). Output: `[memories] removed <first8>`; unknown id → MemoryNotFound.

Constructor: `(ICuratedMemoryStore store, Func<string> workspaceId, Func<string?> provenance, Func<int> bumpWrites, Func<DateTimeOffset> clock)` — clock injectable for tests.

- [ ] **Step 1: Failing tests** over a fake store (~24 cases): every rule above incl. exact output lines, clamp warning line, ambient provenance captured into stored record (fake asserts accessor invoked), write-counter bumped exactly once per successful add and NEVER on failed validation, VersionConflict passthrough text, confirm gate matrix, NothingToUpdate, tag-rule errors, category/scope enum matrices, unknown-parameter rejection per action, malformed JSON → typed error.

- [ ] **Step 2: Red**, implement, green. FULL Memory.Domain.Tests green.

- [ ] **Step 3: Commit** — `feat(memory-domain): curated memory capability surface with strict contracts`

---

### Task 4: Nudges + conversation/loop additions

**Files:**

- Modify: `src/eThangAgent.Conversation.Domain/Conversation.cs` (+AddSystemMessage)
- Modify: `src/eThangAgent.Agent.Domain/Agent.cs` (+LastTurnToolCalls)
- Create: `src/eThangAgent.Agent.Application/Nudges/NudgeContext.cs`, `INudgePolicy.cs`, `DefaultNudgePolicy.cs`
- Modify: `src/eThangAgent.Agent.Application/SendMessageCommandHandler.cs`
- Tests: additions in Conversation/Agent domain test projects + new `tests/eThangAgent.Agent.Application.Tests/NudgeTests.cs`

**Interfaces:**

```csharp
// ConversationDomain
public void AddSystemMessage(string text);   // Role.System; null/whitespace → ArgumentException

// AgentDomain — set during SendMessage; 0 when the turn ended without tool calls
public int LastTurnToolCalls { get; private set; }

// Agent.Application.Nudges
public sealed record NudgeContext(int TurnNumber, int LastToolCalls, int MemoriesWrittenTotal);
public interface INudgePolicy
{
    /// <returns>The reminder line to append as a System message, or null when silent.</returns>
    string? Evaluate(NudgeContext context);
}
```

`DefaultNudgePolicy(Func<DateTimeOffset> clock)` — fires when ALL hold: `TurnNumber % 5 == 0`, `LastToolCalls >= 3`, `MemoriesWrittenTotal == 0`. Line (verbatim):

```text
[nudge] This turn involved several tools and nothing has been saved to curated memories yet. If any durable convention, preference, insight, failure, or reference emerged, consider memories.add — otherwise continue.
```

Handler change: after a SUCCESSFUL `_agent.SendMessage`, evaluate policy with turn number = handler invocation count, agent.LastTurnToolCalls, tracker count; non-null → `_conversation.AddSystemMessage(line)`. Failures of SendMessage never nudge. Constructor grows: `(Ag agent, Conversation conversation, INudgePolicy? policy = null, Func<int>? memoriesWritten = null)` — both optional so existing construction sites compile; policy applied only when both supplied.

- [ ] **Step 1: Failing tests**: Conversation AddSystemMessage (append order + whitespace reject); Agent.LastTurnToolCalls zero-turn vs multi-call turns via scripted fake IModelProvider responses (existing fake style); DefaultNudgePolicy truth table (fires only when all three conditions hold; cooldown via TurnNumber modulo); handler integration: scripted provider + counting fake policy + fake counter → system message appended exactly when policy returns line, not appended on provider failure, turn counter increments across calls.

- [ ] **Step 2: Red**, implement, green across the four touched test projects.

- [ ] **Step 3: Commit** — `feat(agent): turn-boundary nudges via system messages with pluggable policy`

### Task 5: Guidance prompt, wiring, README, verification

**Files:**

- Create: `src/eThangAgent.CLI/CuratedMemoryGuidePromptProvider.cs`
- Modify: `src/eThangAgent.CLI/Program.cs`, `README.md`

**Step 1: Guide provider.** `CuratedMemoryGuidePromptProvider : ISystemPromptProvider` returning (verbatim):

```text
Persistent curated memories: you maintain a searchable knowledge base of durable facts —
conventions, preferences, insights, failures, references — via the memories actions
(memories.search / memories.add / memories.update / memories.remove through exec).
Search when context feels missing before assuming; write when the user states a durable
preference/convention or a task reveals a non-obvious insight or failure worth remembering.
Keep entries atomic (one fact each), tagged, and scoped honestly (global only for facts true
everywhere). Never store secrets, transient task state, or anything derivable from the repo.
After completing a genuinely complex multi-step effort, consider proposing a reusable skill
via skill_manage (source learned) capturing what generalizes beyond this workspace.
```

Insert into the composite AFTER ExecGuidePromptProvider.

**Step 2: Wiring (Program.cs).**

```csharp
.AddSingleton<SqliteCuratedMemoryStore>()
.AddSingleton<ICuratedMemoryStore>(sp => sp.GetRequiredService<SqliteCuratedMemoryStore>())
.AddSingleton<Func<int>>(_ => { int n = 0; return () => Interlocked.Increment(ref n); })   // write counter
.AddSingleton<INudgePolicy>(_ => new DefaultNudgePolicy(() => DateTimeOffset.UtcNow))
```

Provider registration inside the CapabilityRegistry.Create array:

```csharp
new CuratedMemoryCapabilityProvider(
    sp.GetRequiredService<ICuratedMemoryStore>(),
    () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
    () => SubAgentSpawner.RunningChild?.Id.ToString(),
    sp.GetRequiredService<Func<int>>(),
    () => DateTimeOffset.UtcNow),
```

Handler registration becomes:

```csharp
.AddSingleton<SendMessageCommandHandler>(sp => new SendMessageCommandHandler(
    sp.GetRequiredService<Ag>(),
    sp.GetRequiredService<Conversation>(),
    sp.GetRequiredService<INudgePolicy>(),
    sp.GetRequiredService<Func<int>>()))
```

(Adapt to actual Task 4 constructor.) Conversation must resolve as the SAME singleton the Agent holds — verify no duplicate registrations.

**Step 3: README.** After the git bullets add:

```markdown
- Curated memory loop — `memories.search/add/update/remove` over a categorized, full-text,
  versioned knowledge base, with turn-boundary nudges prompting curation
```

**Step 4: Verification.** Build 0 errors; FULL suite green with exact totals in your report. Manual gate stays with the human (same as SP2): live-session check that guidance appears and a memories.add round-trips.

**Step 5: Commit** — `feat(cli): wire curated memory loop, guidance prompt, and nudges`

---

## Plan Self-Review

- **Spec coverage:** MemoryEntry-shaped aggregate ✓ (named CuratedMemory — Ruling below); FTS + category/tag/scope filters + CAS updates + confirm-gated removal ✓ (Tasks 1–3); usage-guidance-only prompt ✓ (Task 5); periodic nudges event-driven at turn boundaries, one per turn max, zero polling ✓ (Task 4); autonomous skill creation invited via existing skill_manage, provenance ambient, no background writer ✓ (Tasks 3+5); security machinery still deferred per ledger.
- **Ruling 0 (pre-flight):** spec's aggregate name `MemoryEntry` COLLIDES with the existing transcript-recall record (`src/eThangAgent.Memory.Domain/MemoryEntry.cs`). Aggregate named `CuratedMemory`; spec text updated by reference here. Cost if wrong: pure naming — grep-rename later if the human prefers otherwise.
- **Placeholder scan:** all production code paths carry complete rules/SQL/contracts; large test suites are enumerated case-by-case with required assertions (house style accepted by prior reviewers); Task 5 adapts constructor shapes to implemented reality with explicit read-first instructions. No TBDs.
- **Type consistency:** CuratedMemory field names consistent Tasks 1→2→3→5; NudgeContext fields match handler usage; provider ctor order fixed in Task 3 and consumed verbatim in Task 5.
- **Known risks flagged:** FTS5 availability asserted by migration smoke test (Task 2 case 11) rather than assumed; transitive Capability.Domain reference made explicit in Task 3; handler's optional-parameter growth keeps existing call sites compiling.
