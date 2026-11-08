---
name: writing-plans
description: Write a plan whose steps state what must be observably true, stored as a plan record. Use when requirements are settled and the work needs an executable task list.
---

# Writing Plans

This is the prediction leg of the scientific loop: a plan's acceptance criteria are the
predictions, and each criterion names the check that would falsify it. The plan is the
experiment design.

Write comprehensive implementation plans. Assume the engineer has zero context for our
codebase and questionable taste: they know the files to touch, the names and shapes they
hand on, the exact values, and what must be observably true — and nothing else about our
problem domain.

Assume they are a skilled developer, but know almost nothing about our toolset.

**Announce at start:** "I'm using the writing-plans skill to create the implementation plan."

**Context:** If working in an isolated worktree, it should have been created via the
`using-git-worktrees` skill at execution time.

**Save plans to:** persist the implementation plan as a plan record — `plan.create` with the plan as
`goal` (announce the plan id), then one `plan.add-step` per task (the step title carries the task
name; the step detail carries the file paths, the interfaces, the examples, and the exact values).

## A step is a slice of context

A step exists to bound what one session must hold: the files it touches, the names it hands
on, the behavior it must produce, and the traps already ruled out. It is not a set of orders.
The red-green-refactor cycle, the test list, and the design decisions belong to the session
doing the work — it can read the code, run the tests, and revise; you cannot.

So a step states what must be observably true and hands over the context that makes it
achievable. Everything else is the session's to decide.

## What a step's detail carries

- **Examples** — a concrete input and its expected result, one per interesting case,
  including the awkward ones: empty, boundary, failure.
- **Behaviors** — named, ordered statements of what the system must do.
- **Acceptance criteria** — what a check would observe when the behavior is true.
- **Architecture** — exact file paths, and the names and shapes of anything one step hands
  to another.
- **Exact values** — version floors, dependency limits, naming and copy rules, verbatim.
- **Traps you already ruled out**, so the session does not spend its budget rediscovering
  them.

## What a step's detail forbids

- **Test code and implementation code in any form**, including a complete snippet offered
  as an illustration. A snippet is not a hint: it is a design decision taken out of the
  hands of the session that can run the tests and see the code.
- **A numbered procedure** the session must follow step by step. "Write a test, run it,
  watch it fail, then add the field" is the session's method, not your instruction.
- **An ordered test list.** The plan states behavior; the implementer derives the tests.

### Before and after

Before — a step whose detail prescribes the method and carries the code:

> Write a test asserting that a review records four scores, run it, watch it fail, then add
> the field.

After — the same step stating what must be observably true:

> A review recorded with four scores reads back with four. A review recorded with three is
> refused, and the refusal names the missing dimension.

Both describe one change. The second leaves the test, the field, and the order of work to
the session that will run them, and it is the only one of the two a check can falsify.

### Why this rule, in numbers

The same specification passed 21% of hidden acceptance scenarios written as prose and 97%
written as examples (codecentric, Emrich and Ade, EXACT Coding). Examples carry a
specification; prose does not. So the examples are required and the code is not.

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
- Consumes: [what this task uses from earlier tasks — exact names and shapes]
- Produces: [what later tasks rely on — exact names, parameter and return types.
  A task's session sees only its own step; this block is how it learns the names
  and types neighboring tasks use.]

**Behaviors:** [named, ordered statements of what the system must do]

**Examples:**
- input `...` → `...`
- the awkward case: empty / boundary / failure, one line each

**Acceptance criteria:** [what a check observes when each behavior is true]

**Traps ruled out:** [what you already tried, and what it showed]
````

## The step detail is the only thing that reaches the session

A chain step's session is seeded with a brief the harness RENDERS from the plan record: the step's
title and detail, the plan's goal, the ledger key, the gate, and the completion contract. Nothing
else from your session travels.

So the step detail is where your intent has to live. It must carry:

- the exact file paths (create / modify / test),
- the exact interfaces the step produces, with names and shapes,
- the examples — a concrete input and its expected result, one per interesting case,
- the acceptance criteria each behavior is checked against,
- the exact values — version floors, dependency limits, naming and copy rules — verbatim,
- the traps you already ruled out.

A detail that says "see the earlier discussion" reaches a session that has no such discussion.

## No placeholders

Every step must carry the content an engineer needs. These are **plan failures** — never write
them:

- "TBD", "TODO", "implement later", "fill in details"
- "Add appropriate error handling" / "add validation" / "handle edge cases" — name the behavior
  and the example that shows it instead
- "Write tests for the above" — the tests are the session's; what is missing is the behavior and
  its example
- "Similar to Task N" (repeat the detail — the engineer may be reading tasks out of order)
- A step whose completion nothing could observe
- References to types, functions, or methods not defined in any task

## Self-review

After writing the complete plan, look at the spec with fresh eyes and check the plan against it. This
is a checklist you run yourself.

**1. Spec coverage:** Skim each section/requirement in the spec. Can you point to a task that
implements it? List any gaps.

**2. Placeholder scan:** Search your plan for red flags — any of the patterns from the "No
placeholders" section above. Fix them.

**3. Type consistency:** Do the names and shapes you used in later tasks match what you defined in
earlier tasks? A function called `clearLayers()` in Task 3 but `clearFullLayers()` in Task 7 is a
bug.

**4. Behavior, not code:** Does any step carry test code, implementation code, or an ordered
procedure? Rewrite it as the behavior it was trying to produce, with an example.

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

### The plan review comes before implementation

A plan written this way is reviewed by an independent session before anything is implemented. The
planner's own confidence is not evidence that the plan is worth executing, and a step that states
behavior instead of code is exactly the kind that reads as complete to its author and as vague to
everyone else.

The mechanism already exists: the chain's `review` role and its deny back-edge. `chain.start` takes
a role per step, so the review is step 1 of the chain, not a pause inside a session:

```text
chain.start id=<planId> steps=[{"position":1,"role":"review"},{"position":2,"role":"implement"}]
```

A `deny` verdict starts a fix session seeded with the findings and their citations, and the plan is
revised before any implementer step runs; an `approve-with-conditions` carries its conditions into
the next step's brief. The review follows `reviewing-plans`.

One review of the whole plan, before implementation. There is no per-phase pause inside a session
and no checkpoint between steps — a step's session runs to its own completion.

For inline execution the review happens the same way, in a session of its own, before the first
step runs.

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
