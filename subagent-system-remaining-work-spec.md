# Subagent System — Remaining Work Spec

Status: the source of truth for ALL work standing between the subagent system as it
exists on `master` (through `8305082`) and its finished state. Supersedes and deletes
`subagent-system-handoff.md`. Nothing here is optional, deferred, or versioned: every
item must land for this document to be deleted by its successor as delivered. Items are
ordered by dependency, not importance — W1 unblocks the most, so start there.

Progress (updated as items land): **1.1 DELIVERED (86f9a52)**, **1.2 DELIVERED
(79bee4b)**, **1.3 DELIVERED (26c435f) — W1 COMPLETE.** **2.1 DELIVERED (4107a90)**,
**2.2 DELIVERED (4107a90)**, **2.3 DELIVERED (94acd5d + 0a54d39)**, **2.4 DELIVERED
(b29f1c9)**, **2.5 DELIVERED (4107a90, 94acd5d, b29f1c9) — W2 COMPLETE.** **W3
COMPLETE (97a78fc)** — 3.1 locator seam + capability-provider fallback, 3.2 composition wiring
(ProcessMailboxLocator + per-container sources), 3.4 both E2E variants — with a
delivery defect found and fixed in the same change: the ChildHost's wire deliver path
fed a server-owned mailbox the child loop never drained (the SessionHost inboxFor
bridge was dead code); delivery now resolves through the child container's own
registry, pinned by ChildHostDeliverWireTests. **W4 COMPLETE** — 4.1/4.2 notify-subtree/notify-ancestors (capability actions over a new `IAgentQueries.ListChildrenAsync` walk, escalate-format receipts), 4.3 capability door + ExecGuide 2.8, 4.4 unread badge (`MailboxDrainedEvent`, supervisor-feed no-beat pin, Desktop tab badge through the real shell wiring) — with a delivery defect found and fixed by the W4 E2E: `CapabilityNameRules` rejected hyphenated action names, killing every session at registry construction (relaxed with a reasoned named decision; stale ChildHost exe rebuilt). W5–W6 untouched.

## Ground rules (carry from the ledger — read before writing code)

- **Suite greenness does not establish wiring.** For every feature below, the delivery
  review must trace each mode's resolve→invoke chain in the REAL host: config → settings,
  library → capability door, handler → invocation, sender → receiver. Every defect found
  in this subsystem's review history was of that shape.
- **Both halves of every seam get an end-to-end test**, preferably through the composed
  app, not fakes. The remote rig in `RemoteHostE2ETests` is the template: real Desktop
  composition, real host exe, real database, mock provider server.
- **Operational**: run test rigs that spawn the ChildHost DETACHED (the in-process xunit
  runner keeps grandchild pipe handles open and wedges Shell()/in-process runs); drain
  output through a queue with a hard deadline; sweep `dotnet build-server shutdown` +
  orphaned `testhost`/`eThangAgent.ChildHost` processes after any interrupted run.
- **Doctrine is enforced by tests, not memory**: keep `DoctrineTests` (no-new-polling
  source scan, domain/transport isolation, enforcement-never-reads-audit) green, and
  extend it when a new seam class appears.
- Every change leaves the build green, tests passing, `dotnet format eThangAgent.slnx
  --verify-no-changes --severity warn` clean, and the tree committed (Conventional
  Commits via the git_commit tool). Update AGENTS.md/README.md in the same change
  wherever a statement goes stale.

## W1 — Watchdog hardening (host-side wiring is untested in the real host)

**Priority: HIGHEST — do first.** `HostChildWatchdog` and `SupervisorFeed` are unit-
tested, and `ChildHostServer.RunChildAsync` attaches the watchdog after start, but NO
test drives a hung child through the REAL host process and asserts the HOST interrupts
and retries/fails it. This is the exact "implemented but never invoked" defect class
that shipped five times in this subsystem.

1.1. **DELIVERED (86f9a52). Hung-remote-child E2E** (`HungRemoteChildE2ETests`,
    Transport.ACL.Tests, real host exe per the `ReAttachE2ETests` launch pattern): the
    host runs with a small `SubAgent:Watchdog` threshold (1.2's surface); the mock
    answers the child's first (tool-call) request then hangs every later one. Asserted:
    HOST-authored `HungDetected`/`RetrySpawned`/`TerminalReport` audit rows, the child
    record settling `Failed(Hung)`, and the final settle envelope reaching the attached
    app runtime. (Delivery finding, fixed in the same change: `RemoteAgentRuntime`
    dropped a remote run's second settle envelope — the pump now retains completed
    sources per the in-process Settle contract, pinned by
    `RemoteRuntimeSettleRetentionTests`.)
1.2. **DELIVERED (79bee4b). Host watchdog configuration surface**: `ChildHostServer.BuildChildWatchdog`
    hardcodes `WatchdogOptions.Default`. Add a `SubAgent:Watchdog:*` configuration
    section (idle threshold, tick interval, max wrap-up attempts; strict bind like
    `SubAgent:RemoteHost` — invalid values are a startup error, never clamped), plumbed
    `AgentSettings` → supervisor → settings JSON → host, so operators can tune the host
    watchdog without recompiling. Unit-test the bind matrix (absent / valid / invalid /
    boundary) the way `SubAgentConfigurationTests` does.
1.3. **DELIVERED (26c435f). Supervisor feed contract for the other event kinds**: `SupervisorFeed` handles
    `ChildProgressEvent` and `ChildSettledEvent`. Decide and pin the contract for the
    remaining stream events: does a budget alert (`ChildBudgetAlertEvent`) reset the
    idle window? (Argue it should NOT — a budget alert is not progress — and pin that
    with a test.) Does `PreemptedEvent`? Pin whichever way is chosen so the next
    contributor does not re-decide it silently.
    (Delivered contract: budget alerts, preemptions, and mail deliveries do NOT beat;
    started events DO beat — the runtime mints a fresh supervisor per (re)start; idle
    alerts NEVER feed — published from inside CheckIdle under the supervisor's
    non-reentrant lock, an echo would self-deadlock and clear the alert. Pinned by
    `SupervisorFeedContractTests`.)

## W2 — Links persistence (session-scoped links are data loss today) — DELIVERED (4107a90, 94acd5d, 0a54d39, b29f1c9)

Links live only in the per-session `AgentLinkRegistry` singleton. Closing the tab, an
app crash, or a restart silently drops every consented link; the agent's `agent.route`
vocabulary breaks with no signal to the user. Consent is a decision the user made — it
must survive restarts, and revocation must persist too.

2.1. **DELIVERED (4107a90). Migration V12** (`AppDatabase`, Storage ACL): `agent_links` table —
    `workspace_id` TEXT NOT NULL, `name` TEXT NOT NULL, `container` TEXT NOT NULL,
    `agent_address` TEXT NOT NULL, `created_at` TEXT NOT NULL, PRIMARY KEY
    (workspace_id, name) — matching the registry's replace-by-name semantics. Follow the
    existing ApplyV* / user_version discipline (highest current version is 11).
2.2. **DELIVERED (4107a90). Store seam**: `ILinkStore` in the Agent Domain (the domain never learns SQL),
    SQLite implementation in the Storage ACL registered per container. Methods: list by
    workspace, upsert, delete by (workspace, name).
2.3. **DELIVERED (94acd5d, 0a54d39). Registry becomes store-backed**: `AgentLinkRegistry` keeps its exact public
    contract (`Link` with the consent gate, `Revoke`, `Resolve`, `Snapshot`) and gains a
    write-through + hydrate-at-construction discipline against `ILinkStore`, scoped by
    the container's `IWorkspaceContext.WorkspaceId`. `Link` with `consented: false`
    still fails without touching the store. Revocation deletes the row — a link removed
    in one session must not reappear.
2.4. **DELIVERED (b29f1c9). Desktop behavior**: the Links dialog lists persisted links on open (hydration
    makes `Snapshot` truthful across restarts); no restart notice is needed because
    links simply survive. If the target agent row is gone, `agent.route` already fails
    `NotRunning` — surface that unchanged.
2.5. **DELIVERED. Tests**: store round-trip (upsert/list/delete); registry hydrate-on-construct;
    replace-by-name; revoke-deletes-row; consent-failure writes nothing; and a
    composition E2E — create a link in session one, dispose the container, open session
    two over the same database, assert `Resolve` succeeds and `agent.route` delivers to
    a live target. Migration test: V12 applies cleanly over a V11 database and is
    idempotent under the concurrent-open race the MigrationGate documents.

## W3 — Cross-container route delivery (links that can actually be dialed)

`agent.route` delivers through the session's OWN `IAgentRuntime` keyed by the link's
address. That runtime only holds mailboxes for children it started itself. A link to an
agent in a DIFFERENT session or container — the documented purpose of links — fails
`NotRunning` in every real case. Links without cross-container delivery are inert.

3.1. **Domain seam**: `IAgentMailboxLocator` in the Agent Domain —
    `TryGet(AgentId id)` returning the agent's mailbox or none. `AgentCapabilityProvider`
    gains a locator parameter; `Route` consults it when (and only when) the
    local runtime fails `NotRunning`. Absent locator: today's behavior byte-for-byte.
3.2. **Composition implementation**: a process-wide locator that resolves an id to the
    owning session container's mailbox — in-process children via the owning container's
    runtime; remote children via the remote runtime's deliver path (wire `deliver`, the
    host's existing mailbox enqueue). Registry of live containers already exists in
    embryo: `OpenSessionIds` on the shell and the runtime's `_mailboxes` maps. Scope it
    honestly: same-process containers first; cross-process (two app instances) stays out
    of the locator contract and remains `NotRunning`.
3.3. **Trust and receipts unchanged**: the R2.4 rule stands — resolve reveals only the
    address tuple; delivery receipts (`delivered to=<address> link=<name>`) and failures
    (`NotLinked`/`NotRunning`/`MailboxFull`) render exactly as today. A cross-container
    delivery emits `MessageDeliveredEvent` on the TARGET's stream with direction
    `cross-container` for the host-side audit trail.
3.4. **Tests**: capability-provider unit matrix (local hit / local miss + locator hit /
    both miss → `NotRunning`); composition E2E — two sessions in one process, a child in
    each, a link from A to B's child, `agent.route` delivers and the child drains it
    (steering-bridge test shape); remote variant — B's child runs in the ChildHost, the
    locator delivers over the wire, drain observed in the child's transcript.

## W4 — Steering surface completion (notify-subtree / notify-ancestors / unread) — DELIVERED

The source spec's broadcast vocabulary (FR-C2/C7) never shipped: `notify-subtree` and
`notify-ancestors` do not exist anywhere in `src` (verified by source search; the earlier
scoping ruling recorded them as never landed, and nothing since landed them).
`agent.send` (parent-to-child) and `agent.escalate` (child-to-ancestors, bounded) exist;
the BROADCAST forms and human visibility of queued mail do not.

4.1. **`agent.notify-subtree(text, urgency)`**: deliver one message to every live
    descendant of the current agent (walking `ParentId` links in the store, the same
    chain `InterruptSubtree` walks). Per-target receipt lines in the result, exactly the
    escalate format: `hop=<n> to=<id> delivered|NotRunning|MailboxFull`, plus a
    `reached=<count> delivered=<count>` summary line. Settled or foreign ids report
    `NotRunning` and are skipped, never retried — push-delivery only (A1).
4.2. **`agent.notify-ancestors(text, urgency)`**: the sibling of escalate with escalate's
    receipt semantics, delivering to ALL ancestors up to the root instead of stopping at
    a hop count. `agent.escalate` keeps its `hops` contract unchanged.
4.3. **Capability door + docs**: both land as actions on `AgentCapabilityProvider` with
    strict argument validation, entries in `ActionNames` (the child-surface sync test
    pins it), and ExecGuide/AGENTS.md sections in the same change.
4.4. **Unread-mailbox surfacing in the Desktop**: `IAgentMailbox.UnreadCount` exists and
    nothing reads it. Surface it on the tab header — a small badge on the owning session
    tab when a CHILD has undelivered steering queued between turns (mid-turn with a
    bounded queue). Refresh via the existing `ChildEventStream` subscription (deliver and
    settle events), never a poll. Headless hosts stay unaffected.
4.5. **Tests**: notify-subtree/ancestors receipt matrices over fakes (live child; mixed
    live/settled; empty subtree; at root); a Desktop E2E through the mock server — send
    to a busy child, badge appears, child drains at its next safe point, badge clears;
    doctrine review: the notify actions are push-only like send, covered by the existing
    no-new-polling scan.

## W5 — Named test-coverage holes (close all three)

These are specific, named seams with no direct test. Each is small; together they close
the last untested halves of shipped seams.

5.1. **`ChildHostServer.HandleDeliver`** (wire to host mailbox enqueue): a direct wire
    test — attach a transport pair, `SendAsync` a `deliver` envelope for a running
    child, assert the mailbox received the message with urgency and sender preserved;
    and the stale case: a deliver for an unknown or settled child id is dropped silently
    with no fault (the app already returned the receipt — the pinned comment becomes
    pinned behavior).
5.2. **`agent.fanout` argument parser**: `SpawnGraphHandler` join semantics are unit-
    tested but the capability action's `children` JSON parser is only exercised at the
    provider boundary. Port those cases into capability-provider unit tests: valid single
    child; valid set with labels; missing `taskPrompt` yielding `InvalidActionInput`
    verbatim; malformed JSON; empty list; depth violation yielding `DepthExceeded`;
    per-child model overrides honored.
5.3. **Desktop `NoticeSink` marshalling**: the Dispatcher.Post to transcript-notice path
    (host-health notices reaching the session transcript) has no test. Headless
    AvaloniaFact: post a notice from a non-UI thread through the production sink shape,
    pump the dispatcher, assert the notice entry renders in the transcript view-model.

## W6 — Documentation truth pass

AGENTS.md and README.md already describe the watchdog feed, the host watchdog, and the
Links dialog because those landed with doc updates. Completing W1-W5 stales more
statements. This item is done only when:

- AGENTS.md's Agent Domain section documents persistent links (W2, replacing the
  superseded in-memory ruling text), the mailbox locator (W3), the notify actions (W4), and
  the host watchdog configuration section (W1.2);
- README.md's capability list matches the final tool surface (notify actions, persistent
  links wording) and its config table documents `SubAgent:Watchdog:*`;
- `ExecGuide` (the pinned exec contract) documents the notify actions' shapes;
- the design record at state key `specs/2026-07-19-subagent-system` and the ledger at
  `sdd.2026-07-19-subagent-system/ledger` carry one entry per landed item, in the
  established format (decision + commit + suite status).

## Definition of done for the whole document

W1 through W6 each merged with their named tests; the full suite (20 projects) green via
the detached-runner discipline; the format verify gate clean; every resolve-to-invoke
chain of every mode traced in the real host and written into the ledger; and the Desktop
manual pass performed and noted: open two sessions, link them, deliver a route message
end to end; flip `SubAgent:RemoteHost=true` and watch a child execute in the host; kill
the app mid-child and re-open to see exact orphan repair. This file is then deleted by
the delivery commit, as its predecessor was.
