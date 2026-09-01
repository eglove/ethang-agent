# Subagent System — Remaining Work

Status: DELIVERED. Every item below shipped on branch `subagent-system` (commits
3c835b8 R1, 4d7b63c R4, 091169c R3, 6fa65d5 R2, d7899dd R5 + defect wave). This file
remains as the delivery record; the design record lives at state key
`specs/2026-07-19-subagent-system` and the binding truth is the code + its tests.
Scope notes from delivery: R2.3 decided (links in-memory, final for v1); R2's
notify-subtree/notify-ancestors premise was stale (never shipped per the T4b ruling) —
agent.route and agent.escalate deliver the actual gaps; R3's accept-loop re-attach
replaced the single-connection host model, and the fail-fast-child E2E asserts exact
declare semantics rather than a still-running child.

---

## R1. Dispatch-time capability enforcement (completes FR-P2, Task 8 remainder)

Priority: HIGH. This is the only ledgered remainder from the 10-step adoption path.

### Problem

Capability grants are enforced at SPAWN VALIDATION only. `StartSpawnHandler` rejects any spawn
whose `tool.allow` names tools outside the parent's effective set (widening, per D9/A5), and the
grants persist on the record's `SpawnContract`. But `SubAgentSpawner.RunAsync` constructs the
child's `Agent` with `SubAgentServices.Tools` — the shared, unfiltered session registry. A child
granted `tool.allow: "web_fetch; read"` still physically holds every tool in the child surface,
and nothing returns the spec's `Error [GrantViolation]` at dispatch.

### Requirements

- R1.1 `FilteredToolRegistry : IToolRegistry` in `eThangAgent.Agent.Domain`: wraps an inner
  `IToolRegistry`, exposes only tools whose action ids are in an injected effective set.
  `Definitions` is filtered; `Find` returns null for filtered-out names (the loop then produces
  its standard UnknownTool error path — acceptable, but see R1.3).
- R1.2 `SubAgentSpawner.RunAsync` builds the child's registry: when `child.Contract` carries
  `CapabilityGrants`, effective set = `ToolGrantPolicy.EffectiveTools(parentEffective)` where
  parentEffective flows from `SpawnOptions.ChildToolSurface` (already the validation source).
  When grants are absent, the shared registry passes through unchanged (zero behavior delta
  for the default case).
- R1.3 Dispatch refusal is a STRUCTURED tool error the child can act on, verbatim contract:
  `Error [GrantViolation]: tool '<name>' is not granted to this agent.` — implemented as a
  grant check at the registry boundary (a filtered Find + explicit marker), NOT a raw
  UnknownTool, so the model can distinguish policy from typo.
- R1.4 Every dispatch-time refusal writes an audit row to `watchdog_events`
  (kind `GrantViolation`): agent id, tool name, timestamp. Audit is a record of decisions,
  never a state source (P2).
- R1.5 Inheritance already holds by construction (validation forbids widening); assert in a
  test that a grandchild's effective set is a subset of the grandparent's when spawned through
  two narrowing grants.
- R1.6 Mid-run revocation stays OUT of scope (source spec Section 13 open question 2; the
  ledgered ruling stands: if built later, it takes effect at dispatch time and is audited).

### Tests

- FilteredToolRegistry: Definitions filtered; Find honors the set; unknown vs granted-vs-denied
  produce distinguishable results (R1.3).
- Spawner integration: a child whose contract grants `web_fetch; read` (against a parent surface
  of `web_fetch; read; exec`) executes a `web_fetch` call and receives `Error [GrantViolation]`
  for an `exec` call, with an audit row present.
- Default path unchanged: no grants in contract -> registry instance is the shared one
  (reference equality is acceptable to assert).

---

## R2. Cross-container link resolution and hop receipts (completes FR-C7/C8/C10, T5 follow-up)

Priority: MEDIUM. The primitives shipped; the long-distance paths are partial.

### What exists

- `AgentLinkRegistry` (domain): named, consent-required, revocable links; `Resolve` fails
  `NotLinked` for unknown names.
- `notify-subtree` / `notify-ancestors` broadcast within the local tree, with per-target
  receipts (`Delivered | MailboxFull | NotRunning`) in the tool result.
- `InterruptSubtree` deepest-first.

### Gaps

- R2.1 `route(name, text)` tool action: resolves `name` through the registry, then delivers
  cross-container via the linked agent's transport (today: same-process only). Unresolved name
  -> `NotLinked` tool result (error contract already pinned by tests).
- R2.2 Multi-hop `ancestor.send(hops)`: today `parent.send` covers one hop. Generalize with a
  hop-count parameter; every hop emits `MessageDeliveredEvent` with a hop-direction label, and
  the tool result lists per-hop receipts (FR-C7's per-hop visibility).
- R2.3 Registry persistence decision: links are currently per-session in-memory. Either (a)
  persist to a new `agent_links` table (migration V13) so links survive restarts, or (b)
  document the in-memory choice as final for v1 in AGENTS.md. Pick one; do not leave it
  implicit.
- R2.4 Trust model: resolve MUST NOT reveal anything about the linker beyond the address
  (source spec open question 6). Add a test asserting `Resolve` returns only
  `LinkAddress(Name, Container, AgentAddress)` and nothing else.

---

## R3. Restart re-attach (completes FR-L7 and the operational half of FR-L8)

Priority: MEDIUM. Process independence is real but not yet survivable across app restarts.

### What exists

- `ChildHost` process serving Start/Interrupt over the named pipe, running the real spawner
  stack against the shared DB.
- `RemoteAgentRuntime` with a settle pump, declared-failure on connection loss, and
  `OwnedChildren` (exact ownership facts for repair).
- Orphan repair on the in-process side is heuristic-by-heartbeat still; the remote side has the
  data to make it exact.

### Requirements

- R3.1 App restart re-attach: on session start with remote mode on, the app reconnects to the
  host's pipe (host process is independent and still running). Verify children spawned before
  the restart are still owned and settle normally after re-attach.
- R3.2 Exact orphan resolution (FR-L8): at startup, the app marks a record `Running` only if
  the id is live in the in-process runtime's active map OR the host's declared live set (a new
  `declare` envelope). Everything else -> `Failed(Interrupted)` + one audit row. The
  heartbeat-presence heuristic in the watchdog retires with this.
- R3.3 Host supervision: the app launches `eThangAgent.ChildHost` (pipe name = derived from
  workspace id + machine hash; settings JSON + DB path written to the scratch dir) when remote
  mode is on, and detects host death (process exit) to surface a host-health notice in the
  session.
- R3.4 Composition: remote mode is opt-in configuration (`SubAgent:RemoteHost` = true); default
  remains in-process. AGENTS.md + README config tables updated in the same change.

### Tests

- E2E (existing headless rig + real host exe): spawn remote -> kill APP process (not host) ->
  relaunch app stack -> re-attach -> child settles and its outcome is retrievable.
- Orphan exactness: Running record whose id is in neither owner -> marked Failed(Interrupted)
  with audit row; Running record in the host's declared set -> untouched.

---

## R4. Documentation debt (same-change obligations that landed only in the ledger)

Priority: MEDIUM, and cheap.

- R4.1 AGENTS.md: watchdog section still describes sweep-based discovery; rewrite to the
  supervisor-event model (registry tick + policy), the deleted ChildTimeout, and the new
  cancellation sources (interrupt / watchdog terminal / budget hard ceiling).
- R4.2 AGENTS.md + README: new projects (`eThangAgent.Transport.ACL`, `eThangAgent.ChildHost`),
  the transport seam + pump IO model, `agent.wait`/`agent.send` tool surface, mailbox
  durability, grants, structured results, spawn graphs, link registry, and the
  `specs/2026-07-19-subagent-system` design-record pointer.
- R4.3 README: remove any stale `ChildTimeoutSeconds` references outside the config table (the
  table row was fixed; sweep the prose).
- R4.4 ExecGuide / skill texts: verify no poll-then-result guidance survived; the wait idiom
  is the documented default (already partially done in Task 3 — verify and finish).

---

## R5. Doctrine tests (source spec Section 14 closes with these)

Priority: LOW, small.

- R5.1 No-new-polling source scan: a test asserting `Wait(` / `Thread.Sleep` /
  `Task.Delay`-in-loops appear only in an allowlist of files (watchdog bounded settle, tests).
- R5.2 Domain-never-references-transport: architecture test asserting no type in
  `eThangAgent.Agent.Domain` namespace-file set references `eThangAgent.Transport.ACL`.
- R5.3 Audit-not-state: assert the watchdog's decision inputs never read `watchdog_events` for
  state (grep-level test or convention assertion).

---

## Non-goals (unchanged from the deleted spec's Section 10)

No DB-as-bus (delivery is always runtime-pushed), no time-based kills, no silent drops, no
implicit privilege, no second conversation writer, no content in events. These carry over
verbatim as acceptance criteria for every item above.

## Suggested order

R1 (high, self-contained) -> R4 (cheap, removes drift) -> R3 (operational payoff) -> R2
(needs the registry trust decisions) -> R5 (closes the book). Each item ships green
independently on its own commit(s).