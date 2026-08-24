# Memory Recall Over Persisted Sessions — Design (P6)

Date: 2026-08-21 · Status: Approved design, pending implementation plan

## Motivation

Agents need to recall what happened in earlier conversations. This phase ports the essence of pi-fabric's memory provider (`memory.recall`, `memory.sessions`) onto eThangAgent's persisted transcripts. The P2 capability-registry design pre-declared "memory with P6" as a registry provider, so this lands as a `memory` provider invoked inside exec programs — the same integration shape as state in P3. As a prerequisite, the root REPL conversation finally becomes persistent, closing the deferral recorded in the P4 spec.

## Decision ledger (user-approved)

| Concern | Decision |
| --- | --- |
| Source of truth | Port pi-fabric `memory-provider.ts` semantics faithfully; subset per leanings below |
| Session corpus | Root REPL conversation persisted as a depth-0 `AgentRecord` **plus** every spawned agent's transcript (existing `IAgentStore` rows) |
| Tiering | Hot-only: every persisted session fully indexed; cold digests and `expand` hydration deferred behind the tier seam |
| Search mechanics | Canonical-token literal matching (literal input is **never** compiled as regex) plus bounded regex with hard guardrails; typed failures `invalid_regex` / `regex_pattern_too_large` / `regex_timeout` |
| Lineage | `branches=active\|all` filter resolved by walking `AgentRecord.ParentId` (active = root-to-node parent-linked paths) |
| Integrity pointers | Deferred: `expectedSourceHash` / lineage fingerprints guard pi-fabric's multi-branch session files; ours is single-writer SQLite |
| Scopes | `session:<agentId>` and `global`; pi-fabric's `project` scope dropped (single app) |
| Surface | Provider id `memory`, actions `recall` + `sessions`, both read-only; guide v1.5 section |

## 1. Domain model & rules

New bounded context `eThangAgent.MemoryDomain`:

- `LexicalTokenizer`: NFKC-normalize, extract `[\p{L}\p{N}_]+` matches, lowercase — Unicode-aware canonical tokens, ported verbatim from `tokenize.ts`. Deterministic ordering via ordinal comparison.
- `MemoryQueryPlan`: `Browse` (empty or whitespace query — recent entries, newest first), `Terms(distinct lexical tokens)` for literal mode, `Regex(pattern)` for regex mode. Literal input is never compiled as a regex; planning happens once, in the domain.
- `BoundedRegex`: validates UTF-8 pattern byte length against `MaxPatternBytes` (default 1024), compiles with `RegexOptions.IgnoreCase \| CultureInvariant` and a `TimeSpan` match timeout (default 250 ms — `DEFAULT_REGEX_TIMEOUT_MS`, pi-fabric `search.ts:16`), then tests haystacks capped at `MaxHaystackBytes` = 2 MiB per entry and `MaxHaystackTerms` = 20,000 tokens per query — defaults ported verbatim from pi-fabric `search.ts:13-16`. Typed failures: `invalid_regex`, `regex_pattern_too_large`, `regex_timeout`. A timeout can never hang the caller — `RegexMatchTimeoutException` is caught and translated.
- Scopes: `SessionScope(AgentId)` and `GlobalScope`. Unknown scope spellings are **rejected** with an error naming the two valid forms — deviation from pi-fabric's alias fall-through, justified by strict-boundary philosophy.
- `BranchMode`: `ActivePath` (default; only root-to-session parent chains) vs `AllBranches`; resolved by walking `ParentId` links already stored on `AgentRecord`.
- Root persistence: new `AgentRecord.Root(...)` factory (depth 0, label "root", non-empty task prompt "conversation root"); the CLI saves it at startup, appends each user/assistant exchange through the existing `AppendMessageAsync`, and marks it `Completed` on graceful exit.
- No new tables: the corpus is read through `IAgentStore` (`ListChildrenAsync`, `GetTranscriptAsync`). The hot index is rebuilt per invocation from the store — small scale, correctness first; a persistent index is an open optimization seam (the tier hook pi-fabric fills with digests).

## 2. Application layer (queries only — memory is read-only this phase)

- `RecallQueryHandler.Execute(RecallQuery)` : resolves scope and branch mode against the store, builds the session corpus, plans the query, searches, and returns one page (default 25 entries, max 200; `page >= 1`). Filters: `role` (user/assistant/tool). Browse returns newest-first across the resolved scope.
- `SessionsQueryHandler.Execute(...)`: lists sessions in scope — id, label, depth, entry count, status, `tier=hot` — newest first, `limit` capped at 500 (pi-fabric's session ceiling).
- Validation is strict everywhere: malformed `session:` scope, unknown `queryMode`, `pageSize > 200`, non-positive `page` — all typed `Error [Code]: message` failures that surface as tool results.

## 3. Capability surface & output contracts

- `memory.recall @{ query = '...'; queryMode = 'literal'; scope = 'global'; branches = 'active'; role = 'user'; page = 1; pageSize = 25 }` — all arguments optional except none; empty query browses. Hits render annotation-style, one per line: `[mem] session=<id> seq=<n> role=<r> <snippet≤120 chars>`, followed by a `--- memory: <total> hits, page <p>/<pages> ---` footer. Regex failures render their typed error line.
- `memory.sessions @{ scope = 'global'; branches = 'active'; limit = 50 }` — one line per session: `session=<id> label=<label> depth=<d> entries=<n> status=<s> tier=hot`.
- Guide v1.5 adds "Recalling earlier work": when to recall (resuming tasks, checking what a child did), literal vs regex modes and the guardrails, scopes, and that memory is read-only — nothing to save yet.

## 4. Infrastructure & wiring

- No Storage.ACL changes required (corpus via `IAgentStore`). If `AppendMessageAsync` proves awkward for bulk root bootstrapping, extend `IAgentStore` minimally rather than adding a parallel seam.
- Program.cs: register the `MemoryCapabilityProvider` (new, Domain) into the `MergedCapabilityProvider` grouping alongside `agent`/state providers; bootstrap the root `AgentRecord` at startup; append exchanges after each completed turn; mark `Completed` on `/exit`.
- All wiring stays in the composition root; Memory.Domain references only SharedKernel and ConversationDomain message shapes.

## 5. Testing strategy

- **Unit** (fakes): tokenizer NFKC/case/punctuation cases; query-plan branching incl. literal-with-regex-metacharacters treated as terms; bounded-regex limits (oversized pattern, catastrophic backtracking under short timeout → `regex_timeout`, invalid pattern); paging math and caps; scope/branch/role validation errors; lineage walk over a fake store with branched children.
- **Integration**: `SqliteAgentStore` round-trip — root session + branched children seeded, recalled through the real handlers; active-path vs all-branch counts.
- **E2E**: scripted OpenRouter conversation seeds exchanges; a later exec program calls `memory.sessions` then `memory.recall` and asserts decoded hits contain the seeded phrase.

## Known limitations (accepted)

- Per-invocation index rebuild: O(total transcript size) per recall — fine at current scale, documented seam for a persistent/tiered index later.
- No write path: agents cannot create memories yet; the corpus is strictly conversational history.
- Ungraceful exits leave root rows `Running` forever (same reconciliation posture as P5).

## Out of scope

Cold digests, `expand` hydration, embeddings/vector similarity, integrity hash pointers, `project` scope, compliance-mode gating of memory writes, cross-process sharing.
