# Subagent System — Handoff

Status: v1 scope AND all three remaining items DELIVERED on branch `master` (items 1–3
landed 2026-08-31 through commit `277bf3a`; full history below). The REMAINING WORK
section below is retained as the record of what was found and is now closed:
item 1 → the Desktop Links dialog (`bc25de7`), item 2 → the SupervisorFeed +
HostChildWatchdog (`7e46661`), item 3 → the RemoteHostE2ETests rig (`277bf3a`), which
also fixed two production defects it exposed (session open never attached the remote
runtime; the ChildHost recursively honored the app's RemoteHost flag). Only the
by-design deferrals and the test-coverage holes remain as recorded — none are gaps.

---

## What is done and verified (do not redo)

All of R1–R5 from the former remaining-work file shipped:

- **R1 dispatch-time grants**: `FilteredToolRegistry` (child loop registry) +
  `FilteredCapabilityRegistry` (exec path). `StartSpawnHandler` resolves the child's
  effective set from the parent's persisted resolution (or the session child tool surface)
  and persists it on `SpawnContract`. Denied dispatches return the verbatim
  `Error [GrantViolation]: tool '<name>' is not granted to this agent.` and append a
  `GrantViolation` watchdog audit row (best-effort). Grandchild narrowing chains through
  the parent's persisted set. Grantless default path is zero-delta (shared registry passes
  through; never double-wrapped).
- **R2 routing**: `agent.route` (link-registry resolve → runtime Deliver; `NotLinked` for
  unknown names), `agent.escalate` (N ancestor hops, per-hop receipts
  `hop=<n> to=<id> delivered|NotRunning|MailboxFull`, `reached=root` at the top).
  R2.3 decided: links are IN-MEMORY per session, FINAL for v1. R2.4 trust test: `Resolve`
  reveals only `LinkAddress(Name, Container, AgentAddress)`.
- **R3 remote mode**: opt-in `SubAgent:RemoteHost` config (strict bind; flows via
  `AgentSettings.RemoteHost` → composition → `RemoteHostSupervisor` + `RemoteAgentRuntime`).
  Host runs an ACCEPT LOOP (survives app disconnects; re-attach finds the same host via a
  workspace-derived pipe name). Host declares its live child set on every connection and
  after each start/settle. `OrphanRepairHandler` runs at session open: a Running record
  survives only if live in the container runtime (`InProcessAgentRuntime.ActiveChildren`)
  or the host's declared set, else `Failed(Interrupted)` + audit; the session root is exempt.
  Host-health notices surface via `AgentSession.NoticeSink`.
- **R4 docs**: AGENTS.md watchdog section = supervisor-event model (registry tick +
  ChildEventStream; ChildTimeout deleted; cancellation sources = interrupt / watchdog
  terminal / budget hard ceiling). Transport ACL + ChildHost in the ACL map. README config
  rows current. ExecGuide and spawn/result tool texts teach wait-first (no poll-then-result).
- **R5 doctrine tests** (`Agent.Application.Tests/DoctrineTests.cs`): no-new-polling source
  scan with named allowlist; Agent-Domain-never-references-Transport-ACL;
  enforcement-paths-never-read-the-audit-trail.
- **Late fixes** (each found by post-delivery seam audits — see `Ledger lessons`):
  fanout door opened; steering mailboxes drained by the running child; host children routed
  through the host's own runtime; remote attach/pump actually invoked; root exempt from
  orphan repair.

---

## REMAINING WORK

### 1. Link creation has no door (BLOCKS agent.route in practice)

PRIORITY: HIGH. The link REGISTRY, `agent.route`, and the consent rules are complete and
tested — but nothing can create a link. `AgentLinkRegistry.Link(name, container,
agentAddress, consented: true)` has zero production callers; the Desktop has no consent UI.
In production today every `agent.route` fails `NotLinked` forever. The design (D10) says
the HOST surfaces link creation after its own user-consent flow, so this is Desktop work:
a consent dialog (name + target agent) → `registry.Link(..., consented: true)` on the
session's `AgentLinkRegistry` singleton. Also decide persistence pressure: R2.3 ruled links
in-memory FINAL for v1; a v2 that persists them needs an `agent_links` table (migration
V13+) and the registry becomes store-backed.

### 2. Remote-mode idle detection (hung remote children)

PRIORITY: MEDIUM. Host-side children now get supervisors, budget hard ceilings, mailbox
lifecycle, and interrupt (route: app `Interrupt` → wire `interrupt` → host cancels the
child's CTS). What they do NOT get: idle detection. The watchdog's idle heuristic needs
progress beats/phase facts, which are emitted host-side and never shipped to the app.
Consequence: a hung-but-under-budget remote child runs forever; in-process it would be
retried once then Failed(Hung). Correct shape: host-side idle detection — the host already
builds a full container per child run; wire a `WatchdogLoop` + per-child `AgentWatchdog`
host-side (subscribe to the host container's `ChildEventStream`), with the host's policy
deciding retry/terminal locally, emitting `settle` envelopes as usual. Do NOT ship app-side
guessing from absent beats.

### 3. Full-app remote-mode E2E

PRIORITY: MEDIUM. The seams are individually tested (transport E2E over a real host exe;
composition session-open orphan-repair E2E; steering bridge test), but NO single test runs
the Desktop composition with `RemoteHost=true` end-to-end. The three wiring bugs found in
post-delivery review lived exactly in that seam. Shape: extend the existing headless
Desktop E2E rig — real host exe, `AgentSettings` with `RemoteHost: true`, spawn a child
that must settle through the wire, kill the app-side container (not the host), re-open,
assert the child's outcome is retrievable and orphan repair marked exactly the right rows.

### 4. Deferred by design (recorded rulings — do not treat as gaps)

- Mid-run grant revocation: ledgered ruling says out of scope; if built, it takes effect at
  dispatch and is audited.
- Unread mailbox surfacing: `BoundedAgentMailbox.UnreadCount` exists; no host UI reads it.
  Cosmetic v2 surface.
- Links in-memory (see item 1's v2 note).

---

## Known test-coverage holes (none blocking, all named)

- `ChildHostServer.HandleDeliver` (wire → host mailbox enqueue) has no direct test; the
  in-process bridge and the transport E2E cover their halves. A wire-level deliver test
  would close it.
- `SpawnGraphHandler` join semantics (fail-fast vs collect-all) are unit-tested in
  Application.Tests but `agent.fanout`'s argument parser is tested only at the provider
  boundary.
- The Desktop `NoticeSink` marshalling (Dispatcher.Post → AddSystemNotice) has no test.

---

## Ledger lessons (how the bugs above shipped — read before extending)

Four post-delivery review rounds found five wiring defects, all invisible to a green suite:

1. Config parsed then discarded (`SubAgent:RemoteHost`) — trace config→settings, not just
   implementation→test.
2. Handler implemented but never invoked (`OrphanRepairHandler`) — trace open→enforcement.
3. Library without a door (`SpawnGraphHandler` had no capability action; links still do) —
   a public class is not a shipped feature.
4. Concrete-type resolution vs interface registration (`InProcessAgentRuntime` registered
   only as `IAgentRuntime`) — owner sets were silently empty; and the session's own root
   row is Running by design, so repair needs an exemption.
5. Sender half shipped, receiver half missing (steering mailboxes never drained; host had
   no `deliver` case) — for every seam, test BOTH halves end-to-end.

Rule going forward: for any multi-mode feature, review must trace each mode's
resolve→invoke chain in the REAL host. Suite greenness does not establish wiring.

Also operational: `dotnet test --filter-class` is rejected by the current SDK (MSB1001);
use the xunit in-process runner directly on the built DLL (`dotnet <dll> -class <FQN>`,
`-verbose` for live lines). `Shell()`-style runs without output draining can deadlock on
chatty children; sweep `dotnet build-server shutdown` + orphaned testhost processes after
any interrupted run.

---

## Delivery history (newest first)

| Commit | Item |
| ------ | ---- |
| `c764667` | fanout door (agent.fanout) + host children through the host runtime |
| `7f33714` | remote attach/pump invoked; root exempt from orphan repair |
| `3f00439` | steering mailboxes drained by the running child; host `deliver` case |
| `a3906e0` | SubAgent:RemoteHost plumbed; orphan repair invoked at session open; NoticeSink |
| `afca3c7` | remaining-work doc marked delivered |
| `d7899dd` | R5 doctrine tests + the defects they flushed out |
| `6fa65d5` | R2 route/escalate + link decision + trust test |
| `091169c` | R3 re-attach + exact orphan repair + host supervision |
| `4d7b63c` | R4 docs (watchdog model, ACL map, README, wait idiom) |
| `3c835b8` | R1 dispatch-time grants (+ two dead-grant defect fixes) |
| `eb48e2d` | style: pre-existing lint-gate debt (ChildHost/Transport/tests) |
| `1badc20` | end-state spec replaced by the remaining-work spec |

Earlier feature commits (`fe50b56`..`dcdc91b`) delivered the original 10-task plan:
wait/settle, mailboxes, send, supervisors, watchdog re-home, urgency/preemption, priority
queue + ceilings, spawn-time grants, transport seam, ChildHost, remote runtime, structured
results, spawn-graph handler, link registry. Full detail: `git log master` and the design
record at state key `specs/2026-07-19-subagent-system` (rulings + ledger at
`sdd.2026-07-19-subagent-system/ledger`, 55 entries).