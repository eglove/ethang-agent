# Subagent System — Remaining Work BDD

Status: the behavioral mirror of [`subagent-system-remaining-work-spec.md`](subagent-system-remaining-work-spec.md),
one feature section per work item (W1–W6) plus the definition-of-done walk. The spec defines
HOW the remaining work is built and verified; this document defines WHAT the people and agents
involved will experience when each item lands — the user behavior definitions the spec lost
when it replaced the end-state spec. Behavior wording traces to the original requirements
spec (`subagent-system-spec.md`, deleted from the repo root, recoverable at commit `bf291dd`,
Sections 6–14) and to AGENTS.md's descriptions of what shipped since. Receipt lines and error
codes are quoted verbatim from those sources — tests and docs may copy them from here.

This document shares the spec's lifecycle: it is deleted by the delivery commit once W1–W6
have landed, as its predecessors were.

Progress: **W1 COMPLETE** — 1.1 (The host interrupts a hung remote child) DELIVERED
(86f9a52), 1.2 (The operator tunes the host watchdog) DELIVERED (79bee4b), 1.3 (The
supervisor feed's contract is pinned) DELIVERED (26c435f) — scenarios below are
implemented and their pins green. **W2 COMPLETE** — A consented link outlives the
session (W2.2–W2.4) DELIVERED (94acd5d, 0a54d39, b29f1c9); The links table migrates
safely (W2.1, W2.5) DELIVERED (4107a90, b29f1c9) — scenarios below are implemented and
their pins green. **W3 COMPLETE** — "A link can be dialed outside its session"
DELIVERED (locator seam + composition wiring + both E2E variants; the remote variant
exposed and fixed a real defect: the host's wire deliver path fed a mailbox the child
loop never drained — now pinned by ChildHostDeliverWireTests). **W4 COMPLETE** — One call reaches a whole subtree, One call reaches every ancestor, The broadcast actions exist and are documented, and The user sees a child's unread steering DELIVERED (capability actions + `MailboxDrainedEvent` + Desktop tab badge; delivery defect found+fixed: the capability-name rule rejected hyphenated action names — relaxed with a reasoned named decision, remote E2Es needed a rebuilt ChildHost exe) — scenarios below are implemented and their pins green. W5–W6 not started.

The spec's ground rules — delivery review traces every resolve→invoke chain in the real host,
both halves of every seam get end-to-end tests, detached test rigs, doctrine tests stay green,
build + format gate + clean tree — are process, not behavior. They remain in force and live in
the spec; they are not restated per scenario here.

## Actors

- **User** — drives the Desktop: consents to links, revokes them, reads tabs, badges, and
  transcripts, and performs the acceptance walk.
- **Operator** — configures the app and the child host through settings JSON: tunes the
  watchdog, flips remote-host mode.
- **Agent** — the model at any node of the agent tree (root or child). Its interface is its
  tool surface; tool results, receipt lines, and error codes are its user experience.
- **System** — the child host, its watchdog, the supervisor feed, and the runtime: they act
  on everyone's behalf and must record and surface what they did (audit, receipts).

## Vocabulary

- **Link** — a named, user-consented address of one agent, usable by other agents through
  `agent.route`. **Consent** is the user's explicit decision that creates it; **revocation**
  removes it.
- **Route** — one agent sending a message to another through a link. Fails `NotLinked`
  (no such link), `NotRunning` (nobody home), `MailboxFull` (receiver's queue is bounded
  and full).
- **Mailbox / steering** — a bounded per-agent inbox for mid-run messages; the owner drains
  it at its next **safe point** (never mid-tool-batch).
- **Settle** — a child reaching a terminal outcome (Completed, Failed(reason), Interrupted),
  observable as an await, never a poll.
- **Idle breach** — the watchdog's stuck-ness signal: no supervisor progress facts for the
  idle threshold. Never wall-clock duration (A4). Response is graduated: first breach
  interrupts and retries the same child id (wrap-up retry, partial transcript preserved);
  second breach marks the child Failed(Hung).
- **Orphan repair** — startup reconciliation of Running records: a record survives only if
  its id is live in-process or in the child host's declared set; otherwise Failed(Interrupted)
  with an audit row.

## Feature map

| BDD feature | Spec item | Restores (source spec) |
|---|---|---|
| The host interrupts a hung remote child — **DELIVERED (86f9a52)** | W1.1 | P5 graduated response, FR-L5, A4 |
| The operator tunes the host watchdog — **DELIVERED (79bee4b)** | W1.2 | operator configuration surface |
| The supervisor feed's contract is pinned — **DELIVERED (26c435f)** | W1.3 | P2/P4 (facts, no re-deciding silently) |
| A consented link outlives the session — **DELIVERED (94acd5d, 0a54d39, b29f1c9)** | W2.2–W2.4 | FR-C10, P7; supersedes ruling R2.3 |
| The links table migrates safely — **DELIVERED (4107a90, b29f1c9)** | W2.1, W2.5 | persistence discipline |
| A link can be dialed outside its session — **DELIVERED (97a78fc)** | W3 | FR-C10's purpose, R2.4, FR-C7 receipts |
| One call reaches a whole subtree | W4.1 | FR-C8 broadcast, FR-C7/A3 receipts |
| One call reaches every ancestor | W4.2 | FR-C8, FR-C7 |
| The user sees a child's unread steering | W4.4 | FR-C5 unread counts |
| The broadcast actions exist and are documented | W4.3 | FR-C8 tool surface |
| The wire's deliver path feeds the host mailbox | W5.1 | FR-X3 |
| agent.fanout reads its argument strictly | W5.2 | FR-O6 |
| Host notices reach the transcript | W5.3 | host observability |
| The documentation tells the truth | W6 | — |
| The operator's acceptance walk | DoD | FR-L7/L8, FR-X4 |

---

## W1 — Watchdog hardening

### Feature: The host interrupts a hung remote child (W1.1) — DELIVERED (86f9a52)

A child running out-of-process that stops making progress is interrupted and retried by the
HOST — not by the app guessing from absent signals, and never by a wall-clock timer. With
default options the idle threshold is 15 minutes; the E2E rig drives it with a small
threshold via the configuration feature below.

Delivered as `HungRemoteChildE2ETests`: real host process, real wire, host-authored audit
rows, `Failed(Hung)` terminal, final settle envelope observed by the app runtime. One
defect found and fixed by writing it: the remote runtime dropped a run's second settle
envelope (now retained per the in-process Settle contract).

```gherkin
Background:
  Given the app runs with remote-host mode enabled
    And a child host process is attached to the session
    And the provider endpoint is a mock server that can be made to stop responding

  Scenario: A child whose provider request hangs is interrupted and retried by the host
    Given a child is running in the child host
    When its provider request hangs and no progress facts arrive for the idle threshold
    Then the child host writes a HungDetected audit row for the child's id
      And the host interrupts the hung run
      And the host restarts the same child id as a wrap-up retry
      And a RetrySpawned audit row names that same child id
      And the child's partial transcript is preserved into the retry

  Scenario: A child that hangs through its wrap-up retry fails terminally
    Given a child has already been interrupted and retried for one idle breach
    When the retry also breaches the idle threshold
    Then the child settles as Failed (Hung)
      And the settle outcome reaches the app runtime attached to the child host
      And the waiting parent learns the terminal outcome without polling
```

### Feature: The operator tunes the host watchdog (W1.2) — DELIVERED (79bee4b)

The host watchdog's thresholds are configuration, not constants: tunable without
recompiling, bound strictly — an invalid value is a startup error, never silently corrected.

```gherkin
  Scenario: Configured values govern the host watchdog
    Given a SubAgent:Watchdog configuration naming an idle threshold, a tick interval,
      and a wrap-up attempt limit
    When the child host starts
    Then its watchdog runs with exactly those values

  Scenario: An invalid value aborts startup instead of being corrected
    Given a SubAgent:Watchdog configuration holding an invalid value
    When the child host starts
    Then startup fails with a configuration error
      And no watchdog ever runs with a clamped or defaulted substitute

  Scenario: Absent configuration means today's defaults
    Given no SubAgent:Watchdog configuration exists
    When the child host starts
    Then the idle threshold is 15 minutes
```

### Feature: The supervisor feed's contract is pinned (W1.3) — DELIVERED (26c435f)

Every event kind's meaning for the idle window is decided once, in the open, so no future
contributor re-decides it silently.

Pinned contract: budget alerts, preemptions, and mail deliveries do NOT beat; started
events DO beat (a fresh supervisor is minted per (re)start); idle alerts NEVER feed
(lock reentrancy + alert preservation).

```gherkin
  Scenario: A budget alert is not progress
    Given a child's idle window is growing because no work is happening
    When a budget alert for that child arrives on the supervisor feed
    Then the idle window is not reset

  Scenario: Preemption's effect on the idle window is decided once
    Given the W1.3 decision for preemption events has been pinned
    When a preemption event for a child arrives on the supervisor feed
    Then the idle window responds exactly as the pinned contract states
      And a test fails if the contract changes without a new decision
```

---

## W2 — Links persistence

### Feature: A consented link outlives the session that made it (W2.2–W2.4) — DELIVERED (94acd5d, 0a54d39, b29f1c9)

Consent is a decision the user made; it must survive tab closes, crashes, and restarts, and
so must revocation. Today every link silently dies with its session and the agent's route
vocabulary breaks without a signal to anyone.

```gherkin
  Scenario: A link made in an earlier session routes in a later one
    Given the user consented to a link named "researcher" in an earlier session
      over this workspace
      And that session is now gone (tab closed, app restarted, or crashed)
    When the user opens a new session over the same workspace
      And its agent routes a message through "researcher"
    Then the link resolves and the message is delivered to the linked agent

  Scenario: The Links dialog lists persisted links, no restart caveat
    Given links were consented in earlier sessions over this workspace
    When the user opens the Links dialog
    Then every persisted link for this workspace is listed
      And no "links are lost on restart" warning appears, because links simply survive

  Scenario: Revocation is permanent
    Given a persisted link named "researcher"
    When the user revokes it in the Links dialog
      And any later session over this workspace looks for it
    Then the link does not exist
      And routing by that name fails NotLinked

  Scenario: Consent is still the door
    Given an attempt to create a link without consent
    When the creation fails its consent gate
    Then nothing is stored
      And routing by that name fails NotLinked

  Scenario: Re-using a name re-points the link
    Given a persisted link "researcher" bound to one agent
    When the user consents to a new link also named "researcher" bound to a different agent
    Then "researcher" resolves to the new agent only
      And the old binding is gone

  Scenario: A link to a departed agent reports honestly
    Given a persisted link whose target agent no longer exists
    When an agent routes through the link
    Then the route fails NotRunning
      And the failure reads exactly as an in-session NotRunning does today

  Scenario: Links belong to their workspace
    Given a link consented in one workspace
    When a session over a different workspace opens the Links dialog
    Then the other workspace's links are not listed and cannot be routed through
```

### Feature: The links table migrates safely (W2.1, W2.5) — DELIVERED (4107a90, b29f1c9)

```gherkin
  Scenario: Version 12 applies cleanly over a version-11 database
    Given a database at migration version 11 carrying real sessions
    When the app migrates it to version 12
    Then the agent_links table exists with one row per persisted link
      And every earlier table and row is untouched

  Scenario: Two processes opening the same database race to migrate
    Given the version-12 migration about to run concurrently in two processes
    When both processes open the database at startup
    Then exactly one migration effect results and both processes proceed
```

---

## W3 — Cross-container route delivery

### Feature: A link can be dialed outside its own session (W3)

Links exist to reach agents a session did not spawn. Today a route can only deliver through
the session's own runtime, so a link to an agent in any other session fails NotRunning in
every real case — links without cross-container delivery are inert.

```gherkin
  Background:
    Given two open sessions in the same app
      And session A's agent is linked to a child of session B

  Scenario: A route crosses sessions within one process
    When session A's agent routes a message through the link
    Then the message lands in session B's child's mailbox
      And the child drains it at its next safe point

  Scenario: A route crosses into the child host
    Given session B's child is running out-of-process in the child host
    When session A's agent routes a message through the link
    Then the message is carried over the wire into the child's mailbox
      And the delivery is visible in the child's transcript when it drains

  Scenario: A route to an address nobody holds fails NotRunning
    Given the linked agent's id is neither local nor resolvable to any live mailbox
    When session A's agent routes through the link
    Then the route fails NotRunning — the same answer an unknown in-session child gets

  Scenario: Another app instance is out of reach
    Given the linked agent lives in a second, separate instance of the app
    When session A's agent routes through the link
    Then the route fails NotRunning
      # cross-process delivery is declared out of scope: the failure is honest, not silent

  Scenario: A session without cross-container reach behaves exactly as today
    Given no cross-container locator is configured for a session
    When its agent routes to an agent outside that session
    Then the outcome is byte-for-byte today's behavior, including the NotRunning failure

  Scenario: Resolving reveals the address and nothing else (R2.4 trust rule)
    When session A's agent resolves the link's name
    Then it learns the target's name, container, and agent address — and nothing more
      And it learns nothing about who consented, when, or why

  Scenario: Receipts and failures read exactly as they do within one session
    When a cross-session route delivers
    Then the sender sees "delivered to=<address> link=<name>"
      And failures still read NotLinked, NotRunning, or MailboxFull exactly as today

  Scenario: A cross-session delivery is auditable on the target's side
    When a message is delivered across containers
    Then the target agent's event stream records a MessageDelivered event
      marked cross-container
```

---

## W4 — Steering surface completion — DELIVERED

### Feature: One call reaches a whole subtree (W4.1) — DELIVERED

`agent.notify-subtree(text, urgency)` broadcasts to every live descendant. Delivery is
best-effort with per-target receipts — every failure has a surface (A3), and nothing is
ever retried or polled for later (push-only, A1).

```gherkin
  Scenario: notify-subtree reaches every live descendant
    Given an agent with a subtree of live children and grandchildren
    When the agent calls agent.notify-subtree with a text and an urgency
    Then every live descendant's mailbox holds the message
      And the result lists one receipt line per target:
        "hop=<n> to=<id> delivered|NotRunning|MailboxFull"
      And a summary line reads "reached=<count> delivered=<count>"

  Scenario: Descendants that are gone are reported, never retried
    Given a subtree mixing live children with settled and foreign agent ids
    When the agent calls agent.notify-subtree
    Then settled and foreign ids report NotRunning in their receipt lines
      And live targets still receive the message
      And nothing is queued or retried for later — delivery is push-only

  Scenario: An agent with no live descendants broadcasts to no one, successfully
    Given an agent with an empty subtree
    When the agent calls agent.notify-subtree
    Then the result reads "reached=0 delivered=0" with no target receipts
```

### Feature: One call reaches every ancestor (W4.2) — DELIVERED

`agent.notify-ancestors(text, urgency)` is the sibling of escalate: escalate's receipt
semantics, but walking ALL the way to the root instead of stopping at a hop count.

```gherkin
  Scenario: notify-ancestors walks all the way to the root
    Given a child three levels below its root
    When the child calls agent.notify-ancestors with a text and an urgency
    Then the parent, the grandparent, and the root each hold the message
      And each hop reports its receipt line in escalate's format

  Scenario: An agent at the root has no one to notify
    Given an agent with no ancestors
    When the agent calls agent.notify-ancestors
    Then the result reports zero targets reached, without error

  Scenario: escalate's hop contract is untouched
    Given the existing agent.escalate tool with its hops argument
    When a child escalates with a hop count
    Then escalate stops at exactly that hop count, as it does today
      And notify-ancestors remains the separate tool that walks to the root
```

### Feature: The user sees a child's unread steering (W4.4) — DELIVERED

The unread count already exists on every mailbox; nothing shows it. When a busy child has
steering waiting between turns, the user should see that on the session tab — pushed by
events, never polled.

```gherkin
  Scenario: Queued steering raises a badge on the session tab
    Given a child is mid-turn in an open session
    When a steering message is delivered to the child's mailbox and waits
    Then a small unread badge appears on that session's tab

  Scenario: The badge clears when the child catches up
    Given a session tab shows an unread badge
    When the child drains its mailbox at its next safe point
    Then the badge disappears

  Scenario: The badge is pushed its updates, never polled
    Given a session tab is open
    Then its badge changes only on delivery and settle events from the child event stream
      And no timer ever polls mailbox depth to refresh it

  Scenario: Headless hosts are unaffected
    Given a host running without the Desktop UI
    When steering queues on children
    Then nothing UI-related is constructed and nothing breaks
```

### Feature: The broadcast actions exist and are documented (W4.3) — DELIVERED

```gherkin
  Scenario: An agent's tool surface offers the broadcast actions
    Given any session's agent capability surface
    When the agent's available actions are resolved
    Then agent.notify-subtree and agent.notify-ancestors are present
      with strict argument validation
      And the child-surface declaration stays in sync with the provider, pinned by a test

  Scenario: The model-facing guide teaches the broadcast shapes
    Given the pinned exec contract a model reads
    Then it documents both notify actions' arguments and their receipt format
```

---

## W5 — Named test-coverage holes

These close the last untested halves of shipped seams. Each feature states the behavior the
missing test must pin.

### Feature: The wire's deliver path feeds the host mailbox (W5.1)

```gherkin
  Scenario: A deliver envelope arrives with everything the sender sent
    Given a child running in the child host with an attached transport pair
    When a deliver envelope is sent over the wire for that child
    Then the child's mailbox holds the message
      And its urgency and sender are exactly as sent

  Scenario: A deliver for a child that is not running is dropped without a ripple
    Given a deliver envelope addressed to an unknown or already-settled child id
    When it arrives at the child host
    Then it is discarded silently
      And the connection does not fault
      # the sending side already received its receipt — nothing new is owed to anyone
```

### Feature: agent.fanout reads its children argument strictly (W5.2)

```gherkin
  Scenario Outline: children argument outcomes
    Given an agent invoking agent.fanout with a children argument that is <case>
    When the argument is parsed
    Then the outcome is <outcome>

    Examples:
      | case                                        | outcome                                 |
      | one well-formed child                       | one child spawns                        |
      | several children carrying labels            | each child spawns under its label       |
      | a child object missing taskPrompt           | rejected: InvalidActionInput, verbatim  |
      | text that is not JSON                       | rejected at validation                  |
      | an empty children list                      | rejected at validation                  |
      | a set deeper than the allowed depth         | rejected: DepthExceeded                 |
      | children carrying per-child model overrides | each child runs on its overridden model |
```

### Feature: Host notices reach the transcript safely (W5.3)

```gherkin
  Scenario: A notice posted from a background thread renders on the UI
    Given a session transcript is open in the Desktop
    When a host-health notice is posted from a non-UI thread
      through the production notice sink
    Then the notice appears as an entry in the transcript view
      And the UI thread was never touched from the wrong side
```

---

## W6 — Documentation truth pass

No runtime behavior lands in W6; its acceptance is that the documentation describes the
behaviors in this document as shipped reality:

- AGENTS.md documents persistent links (replacing the superseded in-memory ruling text),
  the mailbox locator, the notify actions, and the host watchdog configuration section.
- README.md's capability list matches the final tool surface (notify actions, persistent
  links wording) and its configuration table documents `SubAgent:Watchdog:*`.
- the pinned exec contract (ExecGuide) documents the notify actions' shapes.
- the design record at state key `specs/2026-07-19-subagent-system` and the ledger at
  `sdd.2026-07-19-subagent-system/ledger` carry one entry per landed item (decision +
  commit + suite status).

---

## Definition of done — the operator's acceptance walk

The spec's definition of done includes process gates (full suite green via the
detached-runner discipline, format verify clean, every resolve-to-invoke chain traced in
the real host and written into the ledger). The behavioral half is this walk, performed by
hand in the real Desktop:

```gherkin
  Scenario: Two sessions, one link, one delivered route — by hand
    Given the Desktop running with two open sessions over one workspace
    When the user links an agent of one session to a child of the other
      in the Links dialog
      And the first session's agent routes a message through the link
    Then the message is observed arriving in the target child, end to end

  Scenario: A child visibly executes in the child host
    Given remote-host mode is enabled in configuration
    When a child is spawned
    Then the child is observed executing inside the child host process,
      not the app process

  Scenario: Killing the app mid-child repairs exactly
    Given a child running in the child host
    When the app process is killed and re-opened
    Then the re-opened app re-attaches to the child if the host still holds it live
      Or else marks the run Failed(Interrupted) with an audit row
      And nothing in between: no zombie Running rows, no heuristic guesses

  Scenario: The delivery commit closes the book
    Given W1 through W6 each merged with their named tests
      And every resolve-to-invoke chain of every mode traced in the real host
      And each trace written into the ledger
    When the delivery commit lands
    Then the remaining-work spec is deleted, and this document with it
```
