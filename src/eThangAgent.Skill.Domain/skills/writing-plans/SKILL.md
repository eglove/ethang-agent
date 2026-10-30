---
name: writing-plans
description: Write a detailed, bite-sized implementation plan from an approved spec, stored as a plan record. Use when requirements are settled and the work needs an executable task list.
---

# Writing Plans

## Overview

This is the prediction leg of the scientific loop: a plan's acceptance criteria are the
predictions, and each criterion names the check that would falsify it. The plan is the
experiment design.

Write comprehensive implementation plans assuming the engineer has zero context for our codebase and
questionable taste. Document everything they need to know: which files to touch for each task, code,
testing, docs they might need to check, how to test it. Give them the whole plan as bite-sized tasks.
DRY. YAGNI. TDD. Frequent commits.

Assume they are a skilled developer, but know almost nothing about our toolset or problem domain.
Assume they don't know good test design very well.

**Announce at start:** "I'm using the writing-plans skill to create the implementation plan."

**Context:** If working in an isolated worktree, it should have been created via the
`using-git-worktrees` skill at execution time.

**Save plans to:** persist the implementation plan as a plan record — `plan.create` with the plan as
`goal` (announce the plan id), then one `plan.add-step` per task (the step title carries the task
name; the step detail carries the file paths, the interfaces, the test code, and the exact values).

## Speaking to the user

Every message addressed TO THE USER speaks to them as a user/stakeholder, not a developer — plain
language, user-visible consequences, no code vocabulary. Where a decision genuinely requires
technical framing, present it as a recommendation the user approves or rejects rather than a
technical question. This governs messages to the user only: plans and task specs stay technical.

## Scope check

If the spec covers multiple independent subsystems, suggest breaking it into separate plans — one
per subsystem. Each plan should produce working, testable software on its own.

## File structure

Before defining tasks, map out which files will be created or modified and what each one is
responsible for. This is where decomposition decisions get locked in.

- Design units with clear boundaries and well-defined interfaces. Each file should have one clear
  responsibility.
- You reason best about code you can hold in context at once, and your edits are more reliable when
  files are focused. Prefer smaller, focused files over large ones that do too much.
- Files that change together should live together. Split by responsibility, not by technical layer.
- In existing codebases, follow established patterns. If the codebase uses large files, don't
  unilaterally restructure — but if a file you're modifying has grown unwieldy, including a split in
  the plan is reasonable.

This structure informs the task decomposition. Each task should produce self-contained changes that
make sense independently.

## Task right-sizing

A task is the smallest unit that carries its own test cycle and is worth a fresh reviewer's gate.
When drawing task boundaries: fold setup, configuration, scaffolding, and documentation steps into
the task whose deliverable needs them; split only where a reviewer could meaningfully reject one
task while approving its neighbor. Each task ends with an independently testable deliverable.

## Bite-sized task granularity

**Each step is one action (2-5 minutes):**

- "Write the failing test" — a step
- "Run it to make sure it fails" — a step
- "Implement the minimal code to make the test pass" — a step
- "Run the tests and make sure they pass" — a step
- "Commit" — a step

## Plan document header

**Every plan MUST start with this header:**

```markdown
# [Feature Name] Implementation Plan

> **For agentic workers:** this plan is executed either as a chain of sessions (`experiment-driven-development`) or inline in one session (`executing-plans`). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** [One sentence describing what this builds]

**Architecture:** [2-3 sentences about approach]

**Tech Stack:** [Key technologies/libraries]

**Spec:** [plan id of the spec this plan implements (the spec is the plan's goal body); executors retrieve it with `plan.show <id>` — the plan argues from the spec, so the spec travels with it]

## Global Constraints

[The spec's project-wide requirements — version floors, dependency limits,
naming and copy rules, platform requirements — one line each, with exact
values copied verbatim from the spec. Every task's requirements implicitly
include this section.]

---
```

## Task structure

````markdown
### Task N: [Component Name]

**Files:**
- Create: `exact/path/to/file.py`
- Modify: `exact/path/to/existing.py:123-145`
- Test: `tests/exact/path/to/test.py`

**Interfaces:**
- Consumes: [what this task uses from earlier tasks — exact signatures]
- Produces: [what later tasks rely on — exact function names, parameter
  and return types. A task's session sees only its own step; this block is
  how it learns the names and types neighboring tasks use.]

- [ ] **Step 1: Write the failing test**

```python
def test_specific_behavior():
    result = function(input)
    assert result == expected
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/<Project>.Tests --filter <TestName>`
Expected: FAIL (member missing / assertion unmet)

- [ ] **Step 3: Write minimal implementation**

```python
def function(input):
    return expected
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/<Project>.Tests --filter <TestName>`
Expected: PASS

- [ ] **Step 5: Commit**

Stage the touched files (`git add ...` via exec), then commit with the
`git_commit` tool — description `add specific feature`, with type/scope or emoji_key per the session's active commit-style guidance. Never raw shell commits.
````

## The step detail is the only thing that reaches the session

A chain step's session is seeded with a brief the harness RENDERS from the plan record: the step's
title and detail, the plan's goal, the ledger key, the gate, and the completion contract. Nothing
else from your session travels.

So the step detail is where your intent has to live. It must carry:

- the exact file paths (create / modify / test),
- the exact interfaces the step produces, with names and signatures,
- the actual test code, not "write tests for the above",
- the exact values — version floors, dependency limits, naming and copy rules — verbatim,
- the traps you already ruled out.

A detail that says "see the earlier discussion" reaches a session that has no such discussion.

## No placeholders

Every step must contain the actual content an engineer needs. These are **plan failures** — never
write them:

- "TBD", "TODO", "implement later", "fill in details"
- "Add appropriate error handling" / "add validation" / "handle edge cases"
- "Write tests for the above" (without actual test code)
- "Similar to Task N" (repeat the code — the engineer may be reading tasks out of order)
- Steps that describe what to do without showing how (code blocks required for code steps)
- References to types, functions, or methods not defined in any task

## Self-review

After writing the complete plan, look at the spec with fresh eyes and check the plan against it. This
is a checklist you run yourself.

**1. Spec coverage:** Skim each section/requirement in the spec. Can you point to a task that
implements it? List any gaps.

**2. Placeholder scan:** Search your plan for red flags — any of the patterns from the "No
placeholders" section above. Fix them.

**3. Type consistency:** Do the types, method signatures, and property names you used in later tasks
match what you defined in earlier tasks? A function called `clearLayers()` in Task 3 but
`clearFullLayers()` in Task 7 is a bug.

If you find issues, fix them inline. No need to re-review — just fix and move on. If you find a spec
requirement with no task, add the task.

## Execution handoff

After saving the plan, offer the execution choice:

**"Plan complete and stored as plan record `#<id>`. Two execution options:**

**1. Chain (recommended)** — the harness starts a session per step and advances on each completion,
with review steps where an independent verdict is worth the round trip.

**2. Inline execution** — this session executes the steps itself with `executing-plans`, batching
with checkpoints.

**Which approach?"**

**If a chain is chosen:**

- **REQUIRED SUB-SKILL:** Use `experiment-driven-development`.
- Convert with `chain.start`, naming each executable step and its role.

**If inline execution is chosen:**

- **REQUIRED SUB-SKILL:** Use `executing-plans`.
- Batch execution with checkpoints for review.

## Related skills

- `executing-a-chain` — what each chain step's session follows.
- `experiment-driven-development` — how a plan becomes a running chain.
- `executing-plans` — inline execution in one session.
- `reviewing-plans` — the verdict a review step records.
