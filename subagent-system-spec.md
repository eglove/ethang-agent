# Subagent System — Requirements & Architecture (End-State)

Status: DRAFT for review. This is the destination spec for the entire subagent scope —
what a child agent IS when the system is finished — not a v1 slice. Near-term phases fall
out of it via the adoption path (Section 10); nothing here is optional-scoped-away.
Supersedes on approval: the timeout-based child budget (SubAgentOptions.ChildTimeout), the
pull-only observe surface, and the watchdog's sweep/poll machinery (its policy survives;
its discovery machinery is replaced). Grounded in the codebase as of the agent-watchdog
merge: the heartbeat seam, resume hydration, the watchdog_events audit table, and the
root-steering inbox all exist and are consumed by this design.

---

## 1. Problem

A spawned child is currently a task with a database row, not an actor. Everything that
matters about a running child must be discovered by polling:

- The parent polls agent.status / agent.result to learn anything about progress.
- The watchdog sweeps the global agent store each tick and polls the store at 1 s
  intervals during settle-waits, because no component can await a child's terminal
  transition.
- Retry attempts are reconstructed by counting audit rows — a derivation that already
  produced one real bug (deferred retries never escalated to terminal).
- ChildTimeout is the only self-contained stuck-detector; it kills children for being slow
  rather than for being stuck, which is why the plan's "no timeout for subagents, just
  observe" has never been safely achievable.
- Children are bolted to the app process: when the app dies, every running child dies with
  it and leaves orphan rows.

The information always existed — in the runtime's active-run table, in the loop's own
iteration, in the usage reports — but nothing connects it to whoever needs it. The missing
primitive is push. The missing ownership is a supervisor.

## 2. Principles (the spec's constitution)

Every requirement below traces to one of these; requirements that cannot, are out.

- P1. Seams over commitments: every transport, process boundary, and host sits behind a
  domain-owned interface. In-process now must not preclude out-of-process later.
- P2. Facts beat derivations: state that matters (attempts, phase, liveness) is owned as
  fact by the component whose job it is; audit trails are never read back as state.
- P3. Errors are information: a dropped message, a full queue, a refused send — each is a
  result delivered to whoever can act, never a silent drop and never a crash.
- P4. Push over poll: if a component needs to know, the producer tells it. Polling is
  permitted only for humans debugging and for RSS-style metrics that are inherently
  time-based.
- P5. Observation over assumption: a child is never killed for duration. Stuck-ness is
  inferred from signals (heartbeat, burn rate), and the response is graduated (nudge,
  retry, terminal) — policy, not reflex.
- P6. Single writer per conversation: exactly one loop mutates a conversation. Every
  communication feature must preserve this invariant or explain why it does not apply.
- P7. Isolation by default, contact by consent: agents share nothing unless a feature
  explicitly routes between them.

## 3. Baseline (what exists today)

- In-process actor runtime: children as background tasks under a concurrency cap, depth
  limit 3, per-run cancellation sources, same-id persistence (AgentRecord + transcript).
- Agent loop: heartbeat beats at turn start / iteration top / around tool calls; inbox
  drain at safe points (roots only today); cancelled-turn repair (synthetic tool results);
  resume hydration with watchdog wrap-up nudge; delta-only transcript persistence.
- Watchdog: policy (Watch / RetryWrapUp / TerminalReport) + sweep-based discovery +
  watchdog_events audit. Known reconstruction hazards documented in its ledger.
- Tool surface: agent.spawn / agent.status / agent.result; children excluded from
  human-facing tools (clarify); per-provider model selection; usage accounting per call.

## 4. The Child Actor (target model)

Every child is an actor with five properties:

1. Address — AgentId, routable within its runtime; the key for both message channels.
2. Mailbox — a bounded, ordered inbox any authorized component delivers to and only the
   owner drains (generalized IAgentInbox; today roots-only, becomes universal).
3. Event stream — the runtime emits lifecycle, liveness, progress, and budget events;
   subscribers (parent context, supervisor, watchdog, host UI) subscribe instead of poll.
4. Supervisor — a runtime-owned per-child object holding the idle timer, budget
   accumulators, attempt count, and phase; the child's state of record while running.
5. Contract — the spawn-time agreement: task prompt, capability grants, budget ceilings,
   model, result schema (when declared). Persisted with the record so resume and audit
   see the same contract the run started with.

The conversation loop is unchanged in shape: it beats, drains at safe points, and writes
the transcript. Everything else in this spec hangs off those five properties.

## 5. Capability tiers (the ladder to end-state)

Tiers are cumulative; each is a coherent, shippable increment that leaves the system
better than it found it.

- T0 Baseline — today (Section 3).
- T1 Facts & Events — runtime-owned attempts/phase; the event stream; watchdog observes.
- T2 Push — WhenSettled + agent.wait; universal mailboxes; agent.send / parent.send.
- T3 Topology & Governance — tree routing (multi-hop, siblings, broadcast), subtree
  cancellation, message urgency + policy-gated preemption, resource budgets enforced by
  policy, priority scheduling at the concurrency boundary.
- T4 Process independence — transport ACL, out-of-process children, survivability across
  app restarts, orphan repair retired.
- T5 Orchestration — structured result contracts, declarative spawn graphs
  (fan-out / fan-in), team patterns, cross-container linked-agent registry.

## 6. Requirements

Tags: [Tn] = the tier where the requirement lands. Lifecycle FR-L, communication FR-C,
governance FR-B, permissions FR-P, observability FR-O, process model FR-X.

### 6.1 Lifecycle and state (FR-L)

| ID | Requirement | Tier |
|---|---|---|
| FR-L1 | agent.spawn contract is stable forever: non-blocking, returns id + running immediately, CapReached unchanged. | T0 |
| FR-L2 | The runtime owns per-child state (attempts, phase, mailbox depth, budget burn) as fact on the running record; nothing derives state by counting audit rows. | T1 |
| FR-L3 | Resume keeps today's shape: persisted transcript hydrates; same id continues; wrap-up nudge on watchdog restart. | T0 |
| FR-L4 | Cancellation sources are exactly: user/parent interrupt (subtree-wide, FR-C6), watchdog terminal decision, budget hard ceiling (FR-B4). Wall-clock time is never one of them. | T2 (ChildTimeout deleted) |
| FR-L5 | Terminal transition is observable as an await (WhenSettled), not a poll. | T2 |
| FR-L6 | A child outlives its parent's turn; a parent may end its turn while children run, and the session stays alive until children settle or the tab closes. Tab close cascades (FR-C6) after an escalating grace policy. | T3 |
| FR-L7 | Out-of-process children survive app restarts: on relaunch, the session re-attaches to living children via the transport; in-process children remain process-bound by definition. | T4 |
| FR-L8 | Orphan-row startup repair (Running rows with no live run) becomes exact rather than heuristic: the runtime knows which ids it owns, locally and (T4) remotely. | T4 |
| FR-L9 | Depth and concurrency limits become policy, not constants: depth default 3 (host-settable per the grand plan's UI item), concurrency enforced through a priority queue (FR-B6). | T3 |

### 6.2 Communication (FR-C)

| ID | Requirement | Tier |
|---|---|---|
| FR-C1 | Mailboxes are bounded (default 64), per-sender FIFO, drained only at the receiver's safe points (iteration boundaries; never between an assistant tool-call batch and its results). Overflow fails the sender with MailboxFull — never a silent drop. | T2 |
| FR-C2 | agent.send(id, text): parent -> child into the child mailbox; accepted only while Running; NotRunning otherwise. | T2 |
| FR-C3 | parent.send(text): child -> parent into the parent mailbox; available to children only (capability-registry pattern: child surface under RunningChild). | T2 |
| FR-C4 | Message content persists with the receiver's transcript on drain; ChildProgress-style events carry metadata only, never content (D5). | T2 |
| FR-C5 | Between-turn delivery: messages persist in the mailbox (DB-backed) and drain at the next turn start; hosts surface unread counts. No auto-wake by default (D4). | T2 |
| FR-C6 | Subtree cancellation: interrupting an agent interrupts all descendants; completion of a parent implies settlement of its subtree (parents cannot be Completed while children run — spawn-block, wait, or detach-with-report are the exits). | T3 |
| FR-C7 | Multi-hop routing: an agent may address ancestors (parent.send generalizes to ancestor.send with hop visibility) and siblings via the parent's registry; every hop is visible in the audit trail. | T3 |
| FR-C8 | Broadcast: notify-subtree (children) and notify-ancestors primitives; delivery is best-effort with per-hop receipts, bounded by each mailbox (FR-C1). | T3 |
| FR-C9 | Urgency: messages carry an urgency class (Normal / Attention / Urgent). Normal drains at boundaries; Attention requests a drain at the next tool boundary even mid-tool-batch; Urgent may preempt the receiver's turn under policy (D4-revised). Misuse is a budget, not a free channel. | T3 |
| FR-C10 | Cross-container contact is opt-in via a linked-agent registry (explicit, user-consented, named); cross-workspace contact stays opt-in the same way. Isolation by default (P7) is a permanent property, not a phase. | T5 |
| FR-C11 | No database-as-bus, ever (Anti-goal A1): mailboxes may be DB-*backed* for durability between turns, but delivery is always through the runtime's push path; nothing polls a table to discover messages. | all |

### 6.3 Governance: budgets, scheduling, policy (FR-B)

| ID | Requirement | Tier |
|---|---|---|
| FR-B1 | Time is never a budget. Resource budgets are measured directly: tokens, cost (effective price), tool calls. Per-child usage already flows from provider usage reports; the supervisor accumulates per child. | T1 (observe) |
| FR-B2 | Budget thresholds are policy input: the supervisor raises BudgetAlert events (soft threshold), and the watchdog policy decides the graduated response — nudge (wrap-up), retry, terminal. | T3 |
| FR-B3 | Budget ceilings are part of the spawn contract (FR-Contract, Section 4), defaulting to session policy; the host UI may expose them (grand plan: settings). | T3 |
| FR-B4 | A hard ceiling is the only budget enforcement: at 100% of a hard ceiling the supervisor interrupts (equivalent in code path to a watchdog terminal decision — one mechanism, two policies). Defaults exist but are generous; killing for resources is a last resort with an audited decision. | T3 |
| FR-B5 | Burn-rate is a liveness signal: tokens over the recent window near zero while idle alerts fire is strong stuck evidence; the policy may combine signals but each signal alone can trigger review. | T3 |
| FR-B6 | The concurrency cap becomes a priority queue: spawns carry priority (default inherits parent's), queued spawns are visible and cancellable, and cap release wakes the highest-priority waiter — push, not retry-poll. | T3 |
| FR-B7 | The watchdog is, at end-state, a policy function over supervisor + budget events with enactment through the runtime. No sweeps, no settle-polling, no ledger reads. Its tick loop survives only for RSS sampling (inherently time-based, P4). | T2-T3 (progressively), complete by T3 |

### 6.4 Permissions (FR-P)

| ID | Requirement | Tier |
|---|---|---|
| FR-P1 | Spawn-time capability grants: each spawn declares tool grants (allow/deny sets) over the registry; default = today's child set (human-facing tools excluded). The grant is part of the persisted contract. | T3 |
| FR-P2 | Grants are checked at dispatch through the same registry seam (no new enforcement point); an unauthorized call returns a structured tool error the child can act on. | T3 |
| FR-P3 | Inheritance: a child's grants are a subset of its parent's effective grants — privilege cannot grow down the tree. | T3 |
| FR-P4 | Mid-run revocation is an open question (Section 13); if built, revocation takes effect at tool-dispatch time and is audited. | open |

### 6.5 Observability (FR-O)

| ID | Requirement | Tier |
|---|---|---|
| FR-O1 | The event stream is the single source for every progress surface: tool-elapsed status lines, agent.status output, and any UI. agent.status becomes a projection for humans/debugging, not a mechanism. | T2 |
| FR-O2 | The host agent view (grand plan: multi-window agent view, live LLM streaming) consumes ChildProgress plus an opt-in content tap; content taps are per-subscriber, explicitly attached, and never on by default (D5 boundary: events are metadata). | T3 |
| FR-O3 | Structured progress: children may report task/step progress through a progress tool; reports are events, persisted with the audit, surfaced in the host. | T3 |
| FR-O4 | Every policy decision (retry, terminal, budget kill, preemption grant) is audited to watchdog_events — audit is the record of decisions, never a state source (P2). | all |

### 6.6 Orchestration (FR-O/X — T5)

| ID | Requirement | Tier |
|---|---|---|
| FR-O5 | Structured results: a spawn may declare a result schema; the child's final report is validated against it (invalid = Failed with a retryable reason, or a bounded repair loop per policy). | T5 |
| FR-O6 | Declarative spawn graphs: a parent describes a fan-out set (or DAG) in one call; the runtime materializes children, enforces depth/concurrency/budgets, and delivers a joined result (fan-in) as one outcome. | T5 |
| FR-O7 | Team patterns are libraries over the primitives (spawn, send, wait, broadcast, budgets), never new runtime machinery: coordinator/worker, map-reduce, reviewer/author loops. | T5 |
| FR-O8 | Linked-agent registry (FR-C10) gains discovery: named, capability-described agents a session can explicitly link and address. Consent per link, revocable. | T5 |

### 6.7 Process model (FR-X)

| ID | Requirement | Tier |
|---|---|---|
| FR-X1 | The in-process runtime is the reference implementation of the same seams a remote host implements: IAgentEvents, IAgentMailbox, WhenSettled, Deliver are transport-free (P1). | T1-T2 |
| FR-X2 | A transport ACL translates the domain's actor vocabulary to a wire protocol (named pipes / sockets); the domain never learns a transport exists. | T4 |
| FR-X3 | Out-of-process children are full actors: same contracts (mailbox bounds, budgets, events, settle await) with at-least-once event delivery and explicit acks replacing in-process at-most-once (the seam's delivery semantics are declared, not assumed). | T4 |
| FR-X4 | Survivability: the app restart re-attaches to declared child hosts; a child host that died is marked with its last known state and the orphan question resolves exactly (FR-L8). | T4 |
| FR-X5 | Resource supervision travels with the child host (budgets enforced at the host boundary too, not only in-proc). | T4 |

---

## 7. Event model (end-state)

| Event | Payload | Emitted when | Tier |
|---|---|---|---|
| ChildStarted | id, parent, contract summary (model, grants, ceilings), attempts | runtime accepts a Start (initial or retry) | T1 |
| ChildProgress | id, phase (ModelCall / ToolExec / Draining), label | the existing heartbeat beat points | T1 |
| ChildIdleAlert | id, idle age, last phase | the child's supervisor idle timer | T1 (timer), T3 (per-child supervisor) |
| ChildBudgetAlert | id, budget kind, consumed, ceiling, burn rate | supervisor accumulators cross soft thresholds | T3 |
| ChildSettled | id, terminal status, reason, report-size hint | run settles | T2 |
| MessageDelivered | id, direction, urgency, size | a message lands in a mailbox | T2 |
| Preempted | id, by-whom, urgency | a turn was interrupted by an Urgent message under policy | T3 |

Delivery semantics: in-process = synchronous in-order fan-out, at-most-once, ephemeral
(D1); out-of-process = at-least-once with acks (FR-X3). Subscriber faults are contained
and logged, never propagated into a child loop. Urgency on MessageDelivered exists from
T2 (informational) and becomes actionable in T3 (FR-C9).

## 8. Architecture (end-state)

### 8.1 Domain seams (transport-free, P1)

    public interface IAgentEvents { IDisposable Subscribe(IAgentEventSubscriber s); }
    public interface IAgentEventSubscriber { void OnEvent(ChildEvent evt); }
    public abstract record ChildEvent(AgentId ChildId, DateTimeOffset At);
    // ChildStartedEvent, ChildProgressEvent, ChildIdleAlertEvent, ChildBudgetAlertEvent,
    // ChildSettledEvent, MessageDeliveredEvent, PreemptedEvent

    public interface IAgentMailbox
    {
        Result Deliver(string text, MessageUrgency urgency);   // fail-to-sender on Full/NotRunning
        IReadOnlyList<PendingMessage> Drain();                  // called by the owner at safe points
    }

    public interface IAgentRuntime
    {
        IAgentEvents Events { get; }
        SettledTask WhenSettled(AgentId id);                    // an await, not a poll
        Result Deliver(AgentId id, string text, MessageUrgency urgency);
        Result<RouteHandle> Route(AgentId from, AgentAddress to);  // T3: tree + registry addresses
        Result Start(AgentRecord record, SpawnContract contract);
        void InterruptSubtree(AgentId rootOfSubtree);              // T3
    }

    public sealed record SpawnContract(                     // persisted with the record
        string? ResultSchema,                                // T5 structured outputs
        IReadOnlyDictionary<string, string> CapabilityGrants,// T3 allow/deny
        BudgetCeilings Budgets,                              // T3 tokens/cost/calls
        MessageUrgency MaxUrgency);                          // T3 what this child may send upward

### 8.2 Application layer

- ChildSupervisor (per running child): idle timer, budget accumulators, attempt count,
  phase; raises alerts; enacts policy decisions through the runtime. One class owns the
  per-child machinery (D6).
- Watchdog: pure policy over supervisor events; enactment via Interrupt + WhenSettled.
  WatchdogPolicy (existing, tested) re-homed here unchanged in spirit; sweep, settle-poll,
  ledger reads, and ChildTimeout deleted by T3.
- AgentRegistry (T5): linked-agent discovery and consent; addresses beyond the local tree.

### 8.3 State machine (runtime-owned, P2)

    Spawned -> Running -> InterruptPending -> Resuming -> Running(attempts+1) -> Completed
                       |                                               |-> Failed(Interrupted|Hung|BudgetExhausted|ProviderError)
                       +-> (user/parent subtree interrupt) -> Interrupted

Attempts and Phase are record columns written at transitions (T1). watchdog_events is the
decision audit, never a derivation source.

### 8.4 The parent's picture (T2+)

    agent.spawn -> id                       (unchanged)
    [events arrive: progress, alerts]       (subscription, not polling)
    agent.send / broadcast                  (steer children mid-run)
    agent.wait / ChildSettled               (outcome as an await)
    parent.send (children -> parent)        (drains at safe points; Urgent per policy)

## 9. Key decisions

| ID | Decision | Rationale | Cost if wrong |
|---|---|---|---|
| D1 | In-process events are ephemeral, at-most-once, no replay; out-of-process declares at-least-once + acks (FR-X3) | In-process subscribers can be reliable cheaply; replay machinery is bus machinery (Anti-goal A1). The seam declares semantics so the wire step is explicit, not accidental. | A subscriber crash loses events; the supervisor's owned state (not the event) remains the source of truth. |
| D2 | Attempts/Phase/contract become persisted record fields, written by the runtime | Ends the reconstruction bug class. Facts beat derivations (P2). | Migration + version-pin test bumps (routine). |
| D3 | Mailboxes bounded, overflow fails the sender | Strict-boundary doctrine (P3); unbounded queues are the memory-leak shape this codebase has been burned by. | A chatty sender hits the cap and must batch — visible, correctable. |
| D4 (revised) | Normal messages never preempt; Urgent may, only under an explicit, audited policy grant, and only at tool-dispatch boundaries | Preemption-by-default breaks the single-writer turn guard and the clarify flow; preemption-by-never makes children unable to escalate genuine blockers. The end state earns the middle: an urgency channel with policy, budget, and audit. | Too strict: children cannot escalate (fallback: finish turn, then handle). Too loose: parent turns thrash on child chatter. Both visible in telemetry; the default is conservative. |
| D5 | Progress events carry metadata only; content taps are explicit per-subscriber | Content belongs to the transcript; small payloads; no duplication of conversation data into event streams. | A subscriber wanting content attaches a tap or reads the transcript — one hop. |
| D6 | Per-child supervisors own timers/accumulators (no central sweep) | Timers die with the child; O(running children); no global scan (P4). | More moving parts per child, contained in one class. |
| D7 | agent.wait is unbounded by default | The plan's explicit no-timeout stance; the event-driven watchdog guards the child; the user's stop guards the parent's turn. | A waiting parent holds a thread — same shape as any blocking tool, bounded by interrupt. |
| D8 | Budgets measure resources, never time; hard ceilings are the only hard enforcement | Time is a proxy for stuck-ness and a bad one. Resources are the real constraint and already reported per call. Graduated responses (nudge/retry/terminal) stay policy. | A runaway child burns to its ceiling — ceilings must default generous and audited. |
| D9 | Capability grants are explicit, inherit-only-narrowing, and part of the persisted contract | Permissions that appear implicitly cannot be audited; privilege must not grow down the tree. | Spawn sites become more verbose; defaults keep the common case one line. |
| D10 | Cross-container/cross-workspace contact is opt-in via a consented registry, forever | Isolation by default is a doctrine (P7), not a phase. The registry makes contact explicit and revocable. | Missed serendipity between agents; the registry exists for when it is wanted. |
| D11 | Out-of-process is a transport behind an ACL, never a runtime fork | One actor model, two transports (P1). Divergent semantics are declared at the seam (delivery guarantees). | The ACL translation layer is real work up front in T4. |
| D12 | Team patterns and spawn graphs are libraries over the primitives, never runtime features | The runtime provides addressability, mailboxes, budgets, settle-await; composition stays in application/domain code where it is testable and replaceable. | Complex DAGs push complexity into library code — acceptable; it is the replaceable layer. |

## 10. Anti-goals (the never list)

- A1. Never a database-backed message bus: delivery is always pushed by the runtime;
  nothing discovers messages or liveness by polling a table (P4). DB-backed durability
  between turns is fine; DB-as-transport is not.
- A2. Never shared conversation identity: a child sees its own conversation only. No
  hidden context bleeding between parent and child transcripts.
- A3. Never unbounded queues, silent drops, or best-effort-by-accident: every failure mode
  has a code, a surface, and a recipient (P3).
- A4. Never time-based kills: wall-clock duration is never a cancellation source
  (FR-L4). Observation and resource budgets replace it entirely.
- A5. Never implicit privilege: no tool, budget, or urgency capability exists unless
  granted, and grants only narrow down the tree (D9).
- A6. Never machinery-initiated contact without policy: preemption, auto-wake, and
  cross-agent addressing are granted, urgent, audited behaviors — never defaults (D4, D10).
- A7. Never a second conversation writer: exactly one loop mutates a conversation (P6);
  every feature above preserves this or does not ship.

## 11. Adoption path (each increment ships green)

| Step | Delivers | Tier | Deletes / retires |
|---|---|---|---|
| 1 | Attempts/Phase/contract record fields written by the runtime; watchdog reads facts | T1 | ledger-derived attempts; the deferred-escalation special case |
| 2 | IAgentEvents + emission at existing beat points; watchdog subscribes for observation | T1 | (nothing — additive) |
| 3 | WhenSettled + agent.wait; docs demote agent.status from mechanism to projection | T2 | poll-then-result patterns in docs and model-facing guides |
| 4 | Universal mailboxes (generalized IAgentInbox); agent.send / parent.send; between-turn durability | T2 | children's pull-only relationship to their parent |
| 5 | ChildSupervisor per-child idle timers + budget accumulators; watchdog becomes event policy | T3 | the sweep, the settle-poll, ChildTimeout (FR-L4 complete) |
| 6 | Tree routing: subtree interrupt, multi-hop, broadcast, urgency classes + preemption policy | T3 | the children-cannot-escalate gap |
| 7 | Priority queue at the concurrency boundary; budget ceilings + hard-ceiling enforcement | T3 | first-come concurrency; reflexive kills |
| 8 | Capability grants at spawn (inherit-only-narrowing) | T3 | implicit child tool surface as the only story |
| 9 | Transport ACL; out-of-process children; re-attach on restart; exact orphan resolution | T4 | heuristic orphan repair; process-bound children as a limitation |
| 10 | Structured results; spawn graphs with fan-in; linked-agent registry + discovery | T5 | ad-hoc result parsing; string-typed team patterns |

Steps 1-5 are the timeout-removal arc. Steps 4-6 are the two-way communication arc.
Steps 9-10 are where "subagents" becomes "an agent mesh with consent boundaries."

## 12. Error contract

| Case | Code | Surface |
|---|---|---|
| send to unknown/finished child | NotRunning | tool result |
| send to full mailbox | MailboxFull | tool result (to the sender) |
| send with urgency above the child's grant | UrgencyNotGranted | tool result |
| wait on unknown child | NotFound | tool result |
| wait cancelled | Cancelled | tool result |
| route to unlinked container/agent | NotLinked | tool result |
| budget hard ceiling reached | BudgetExhausted (FailureReason) | record + audit + parent event |
| preemption denied by policy | PreemptDenied (audited) | sender-visible result |
| subscriber throws | contained + logged | never reaches the child loop |
| delivery to self | rejected at validation | tool result |
| grant violation at dispatch | structured tool error | child-visible, auditable |

## 13. Open questions (blockers for the steps they gate)

1. Preemption policy shape (gates step 6): who grants Urgent (spawn contract? session
   policy? per-parent opt-in), and what does the interrupted turn do with its partial
   work — the cancelled-turn repair path exists, but preemption frequency decides whether
   repair or await-the-boundary is the common case.
2. Revocation semantics (FR-P4): can a parent revoke a grant mid-run, and what happens to
   a child blocked mid-plan when its key tool disappears?
3. Structured-schema strictness (gates step 10): validate-and-fail vs one bounded repair
   round with the validation error fed back; interplay with the wrap-up nudge path.
4. Budget ceiling defaults and units (tokens vs cost vs both) — needs the host settings
   story from the grand plan's UI items to land together.
5. Out-of-process hosting target (gates step 9): separate child processes vs a single
   supervised multi-actor host process; affects the transport ACL's first protocol.
6. Cross-container registry trust model: what a linked agent may learn about the linker
   (existence? capabilities? transcripts?) before any message flows.
7. Whether the depth limit should become soft guidance (budgets do the real work) once
   budgets land — the plan's "UI setting for RLM depth limit" suggests keeping it hard
   but user-set.

## 14. Testing strategy (end-state)

- Domain: mailbox semantics (FIFO, bounds, urgency gating, drain-only-at-safe-points —
  generalize the existing steering tests); contract validation (grants, ceilings, schema);
  event record shapes; policy over event payloads (the existing watchdog matrix re-homed,
  plus budget-alert matrices).
- Application: supervisor timers and accumulators on fake clocks; settle observation via
  WhenSettled with fake runtimes (an await — the deadlock-vigilance surface shrinks);
  send/wait/route tool matrices; preemption policy cases including denial.
- Infrastructure: runtime emits at every beat point; interrupt-then-settle observation;
  subtree interrupt kills descendants in order; no lost events under concurrent spawn;
  transport ACL round-trips semantics exactly (at-least-once looks like at-most-once to
  the domain's tests, with acks faked).
- E2E (the existing headless mock-provider rig): spawn -> progress observed -> mid-run
  agent.send steers the child -> parent.send lands on drain -> agent.wait returns the
  outcome; a hung child -> budget/idle alerts -> graduated policy -> terminal; fan-out
  graph with fan-in; restart re-attach against a fake child host.
- Doctrine tests: no new polling loops (a source-scan assertion like the CI lint idea);
  domain-never-references-transport (architecture test); audit-not-state (watchdog reads
  its own events, never the ledger).