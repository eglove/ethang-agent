---
name: requesting-code-review
description: Request structured code review of completed work before integrating it. Use when finishing a task, feature, or fix that another reviewer should check.
---

# Requesting Code Review

This is the external-verdict leg: a reviewer who did not write the change grades it — the loop's
check against self-grading.

**Core principle:** review early, review often.

## How a review happens here

A review is an INDEPENDENT SESSION that judges your work. In a chain, that is a step assigned the
`review` role when the chain was created — the harness starts it, it records a verdict, and the
cursor acts on that verdict. You do not schedule it and you do not dispatch it.

**A review judges implementation, not a plan.** It looks at work that already exists and answers one
question: may this stand? Plan work is a different role — `refine` — which makes a plan executable
before anything is built. Do not confuse the two: a refiner returns no verdict, and a reviewer does
not rewrite the plan.

**There is no reviewer subagent.** A session cannot start one, and it cannot review itself into an
external verdict. If your work needs an independent verdict and no review step exists, say so — the
planning session can revise the plan, and a human can start a new chain that includes one.

## When a review is worth a chain step

**Mandatory:**

- Before a chain completes and its work is integrated.
- After a step whose change is large enough that a reviewer could meaningfully reject it.

**Optional but valuable:**

- A step you expect to be contentious.
- A step that changes a shared interface later steps build on.

## What the reviewer receives

The reviewer's brief is rendered by the harness from the plan record. It names the step, the plan's
goal, the chain's ledger key, and the step it judges — and it never includes your session's history.
A reviewer that reads your reasoning inherits your framing, which is the self-grading the external
verdict exists to prevent.

## What the reviewer returns

A verdict recorded with `chain.judge` — not prose in a transcript:

```text
verdict: pass | fail
findings: [{text: <what is wrong>, citation: <requirement id, file, test, or step position>}]
```

| Verdict | What happens |
| --- | --- |
| `pass` | The chain advances past the review step and runs what follows. |
| `fail` | The judged step reopens for a fix session seeded with the findings and their citations. |

A fail is capped at three round trips per step, counted on the step being reopened rather than on
one reviewer. Past the cap the chain parks rather than looping: a third fail of the same work means
the disagreement needs a human, not a fourth round.

## If you are the reviewer

Two rules that matter most:

- **Never read the implementer's session history.** Judge the work, the plan's requirement, and the
  evidence that the work runs.
- **Never fix what you review.** A reviewer that edits the work to make it pass has replaced the
  independent verdict with its own.

A finding with no citation is not a finding. "The code is unclear" cannot be acted on or audited;
"step 3 has no acceptance criterion for requirement R2" can.

## Acting on feedback you receive

- Fix Critical issues immediately.
- Fix Important issues before proceeding.
- Note Minor issues in the ledger for later.
- Push back when the reviewer is wrong — with reasoning and evidence, and by recording the ruling,
  never by silently dropping the finding.

## Red flags

**Never:**

- Skip review because "it's simple".
- Ignore a Critical finding.
- Proceed with unfixed Important findings.
- Argue with valid technical feedback.
- Review your own change and call it reviewed.

**If the reviewer is wrong:** push back with technical reasoning, show the code or tests that prove
it works, and record the ruling in the ledger.

## Related skills

- `reviewing-plans` — the rubric for a PLAN review, which is a different judgment entirely.
- `receiving-code-review` — what to do with feedback you get.
- `executing-a-chain` — the step whose completion a verdict gates.

---
