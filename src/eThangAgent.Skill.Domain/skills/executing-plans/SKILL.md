---
name: executing-plans
description: Execute a stored implementation plan task by task with per-task verification. Use when a written implementation plan exists and the work is starting or resuming.
---

# Executing Plans

## Overview

Load the plan, review it critically, execute every task, report when complete.

**Announce at start:** "I'm using the executing-plans skill to implement this plan."

**Note:** this is the skill for work that stays in ONE session. If the plan's steps each deserve
their own session and their own reviewer, convert it to a chain instead — use
`experiment-driven-development`.

## The process

### Step 1: Load and review the plan

1. Ensure an isolated workspace: use `using-git-worktrees` to create one or verify the existing one.
2. Read the plan with `plan.show id=<planId>`.
3. Review it critically — identify questions or concerns.
4. If concerns: raise them with your human partner before starting.
5. If no concerns: proceed.

### Step 2: Execute the tasks

For each task:

1. Mark its plan step `InProgress`.
2. Follow each step exactly — the plan's steps are bite-sized on purpose.
3. Run the verifications the plan specifies.
4. Record what you learned in the ledger: `experiment` with `record-hypothesis`, then
   `record-prediction` naming the command that would refute you, then `record-verdict` with the
   evidence. A prediction you never named is a change you cannot be wrong about.
5. Mark the step `Done` — or `Skipped` if it deliberately is not being done.

### Step 3: Complete the work

After all tasks complete and are verified:

- Announce: "I'm using the finishing-a-development-branch skill to complete this work."
- **REQUIRED SUB-SKILL:** Use `finishing-a-development-branch`.
- Follow that skill to verify tests, present options, and execute the choice.

## When to stop and ask

**STOP executing immediately when:**

- You hit a blocker (a missing dependency, a failing test, an unclear instruction).
- The plan has critical gaps that prevent starting.
- You do not understand an instruction.
- Verification fails repeatedly.

**Ask rather than guess.**

## When to revisit earlier steps

**Return to review (Step 1) when:**

- Your partner updates the plan from your feedback.
- The fundamental approach needs rethinking.

**Don't force through blockers** — stop and ask.

## Remember

- Review the plan critically first.
- Follow the plan's steps exactly.
- Don't skip verifications.
- Reference skills when the plan says to.
- Stop when blocked; don't guess.
- Never start implementation on main/master without explicit consent.

## Related skills

- `experiment-driven-development` — when each step deserves its own session.
- `executing-a-chain` — what a chain step's own session follows.
- `finishing-a-development-branch` — completing the work.
