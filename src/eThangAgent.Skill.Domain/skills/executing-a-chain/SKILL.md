---
name: executing-a-chain
description: Execute one assigned chain step and finish it so the harness advances. Use when a session opens with a chain step brief instead of a user request.
---

# Executing a Chain

This carries the experiment and external-verdict legs of the scientific loop, one step at a time:
your step is the experiment, and a review step is the external verdict you cannot self-grade.

**Core principle: the harness advances the chain, not you.** You were started for exactly one
step of one plan. When you mark that step done, the harness closes this session and starts the
next one. Nothing else decides whether the chain continues — not you, not a parent agent.

## What you are

Your first message is a rendered brief, not a user request. It names:

- the chain id and the plan id
- your step's position, title, and detail
- the plan's goal, written by the session that planned this work
- the chain's ledger key
- the gate commands, or a statement that this workspace declares none
- the steps that already ran and what each concluded
- the completion contract: the one step you may mark done

The same facts appear in your system prompt as a standing chain contract. Read the brief for the
work and the contract for the rule, then execute. Do not ask a human to confirm the assignment:
the harness assigned it from a record, and a session parked on a question costs the chain a slot
and buys nothing.

## The completion contract

**Mark the assigned step done, and only that step.** The command is in the brief with the plan id
and position filled in:

```text
plan.update-step id=<planId> position=<position> status=Done
```

The harness refuses a `Done` on any other step of this plan, and it refuses a `Done` on this step
from any other session. Those refusals are not obstacles to work around — they are the ownership
check that makes the chain's record true.

A step whose work is deliberately not being done is `Skipped`, never `Done`. `Skipped` is the
honest exit: a `Done` on unfinished work is the false record the gate exists to prevent.

### The gate must be FRESH

A done-claim is backed by a gate that passed **since your last change**. `state.checkgoal` runs
the workspace's `goal/check` commands and records the result with the workspace's digest. A green
run from an hour ago followed by five edits proves nothing about the code you are declaring
finished, and the completion gate refuses a claim that predates the change.

Re-run `state.checkgoal` after your final edit — including after a doc edit. If the workspace
declares no `goal/check` commands, no gate applies; the brief says so, and saying so is different
from saying nothing.

### If you cannot finish

Say so plainly in the transcript and stop. Do not mark the step done, and do not mark it failed
either — the lifecycle is the harness's. A session that goes idle without completing is nudged
once and then its chain is PARKED, which is a state a human can act on. A parked chain is
recoverable; a chain that advanced on a fabricated `Done` is not.

## The ledger is chain scope

Every step of this chain reads and writes the same key: `sci.<slug>/ledger`. A refutation recorded
by an earlier step is yours to read, and one you record is the next step's to read.

An empty ledger is normal — you may be the first step. Record **as you work**, not at the end: a
refutation recorded after your session closes helps nobody, and a later step that re-derives what
you already established has wasted the chain's time.

Use the `experiment` tool. Its actions are `record-hypothesis`, `record-prediction`,
`record-verdict`, `record-observation`, `record-conclusion`, `record-skip`, `list`, and `summary`.

Before you experiment, state what you expect:

```text
record-hypothesis   text=<what you expect to be true>
record-prediction   id=<hypothesis id>  text=<the claim>  falsifiedBy=<the exact command that would refute it>
```

A prediction with no falsifying command is a change you cannot be wrong about, which means your
report can only ever confirm itself. After you run that command, record the outcome:

```text
record-verdict      id=<prediction id>  verdict=confirmed|refuted|inconclusive  evidence=<command + what it printed>
```

A refuted prediction is a result, not a failure — say what it eliminated. If the step genuinely
needs no experiment (a pure rename, a docs edit), `record-skip` with the reason rather than
inventing a prediction you know will pass.

## Do not start anything

You do not start the next session, dispatch helpers, or create a chain. There is no subagent
surface in this harness and a chain step may not start a chain: a session that started its own
successor would replace the harness's cursor with its own guess.

Work that genuinely needs a second opinion is a **review step** on the chain, assigned when the
chain was created. If you believe a review step is missing, say so in your step's outcome and in
the transcript; the planning session can revise the plan and a human can start a new chain. Do not
attempt to schedule the review yourself.

## Working the step

1. Read the brief and the standing contract. Retrieve the plan with `plan.show id=<planId>` and
   the ledger with `state.get key="sci.<slug>/ledger"`.
2. Search for related refutations before you start — `state.find` spans the workspace, so a
   finding from another chain is reachable. Do not wander into another chain's conclusions; they
   are scoped to work you are not doing.
3. Run the loop: observe, hypothesize, predict, experiment, analyze, report — with the ledger
   written as you go.
4. Verify with `verification-before-completion` before any done-claim.
5. Run `state.checkgoal` after your last change.
6. Mark your one step done.
7. Stop. The harness takes it from there.

## Common rationalizations

| Excuse | Reality |
| --- | --- |
| "The work is done, the gate is close enough" | A stale gate proves nothing about the code you are declaring finished. Re-run it. |
| "I'll mark it done and mention the gap in my summary" | The record is what the chain reads. A gap in prose is a gap nobody acts on. |
| "This step is small, I'll also do the next one" | You may not mark another step done, and the harness will refuse it. One step, one session. |
| "I need a reviewer, I'll start one" | There is no subagent surface. A review is a chain step assigned at creation. |
| "I'll start the next session myself" | Then the harness's cursor and your guess disagree, and the chain runs the step twice or skips one. |
| "The ledger is bookkeeping, I'll write it at the end" | The ledger is what survives your session. A refutation written after it ends helps no one. |
| "Parked is a failure, I should force it through" | Parked means waiting for a human. A chain that advances on a guess is the failure. |

## Related skills

- `writing-plans` — the planning half: a plan becomes a chain only when someone says so.
- `reviewing-plans` — the rubric a review step applies.
- `experiment-driven-development` — the loop's shape when a session runs several steps of a plan
  itself instead of one assigned step.
- `verification-before-completion` — the evidence rule behind every done-claim.
- `finishing-a-development-branch` — merging, once the chain has finished.
