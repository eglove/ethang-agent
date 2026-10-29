---
name: reviewing-plans
description: Review a plan before implementation: score requirement coverage, testability, and blast radius, then return a verdict with cited findings. Use when a chain step is assigned the review role.
---

# Reviewing Plans

This is the external-verdict leg applied to a plan: an independent session judges the plan
before anyone implements it, so the planner's own confidence is not the only evidence that
the plan is worth executing.

**Core principle:** a verdict nobody can act on is not a verdict. Every finding names the
requirement or step it is about, and every score says why it has that value.

## What you are given

Three inputs, and nothing else:

- **The plan** — its goal and every step with its position and status:
  `plan.show id=<plan-id>`. Read all of it. A plan is judged as a whole, not step by step.
- **The requirements** — the spec or issue the plan claims to satisfy. The plan's goal
  names where they came from; read them at the source rather than trusting the plan's
  paraphrase of them.
- **The rubric** — below. It is the same three dimensions every review scores, which is
  what makes two reviews comparable.

**Never read the planner's session history.** Not its transcript, not its reasoning, not
the conversation that produced the plan. A reviewer that reads the planner's framing
inherits it, and a verdict that agrees with the author is exactly the self-grading the
external verdict exists to prevent. If the plan does not explain itself, that is a finding
— not a gap to fill from the author's head.

## The rubric

Score each dimension 1-5, where 5 is best:

| Dimension | Question | 5 | 1 |
| --- | --- | --- | --- |
| Requirement coverage | Does every requirement have a step that satisfies it? | every requirement traced to a step | requirements unaddressed |
| Testability | Can each step's completion be checked mechanically? | every step names its check | steps say "make it work" |
| Blast radius | How much does the plan touch, and is that proportionate? | the smallest change that satisfies the requirements | reaches far beyond what the work needs |

The scale is this skill's, not the record's: the review record stores the integers and
enforces only that they are non-negative, so a review scored on a different scale is not
comparable with any other review. If you deviate, say which scale you used.

A large blast radius is not automatically a bad plan — a genuinely cross-cutting change
has one. What is scored is whether the plan's reach is justified and stated, not whether
it is small.

## The output contract

Write the verdict first, because the harness reads the first line and everything after it
is for the human reading the record.

```text
verdict: approve | deny | approve-with-conditions
scores: requirement-coverage <1-5> — <one line why>
        testability <1-5> — <one line why>
        blast-radius <1-5> — <one line why>
findings:
- <finding text> [cites: <requirement id or step position>]
```

**The verdict is exactly one of the three tokens.** Not "approved", not "approve with
conditions", not "LGTM". Those three spellings are what the review record stores; anything
else fails to parse, and a verdict that fails to parse is a review that did not happen.

**Three integer scores, each with a one-line justification.** An unjustified score is an
opinion with a number attached: "blast-radius 4 — touches three files in one project" is a
score, while "blast-radius 4" is a guess nobody can check.

**Every finding cites a requirement id or a step position.** The citation is what makes the
note actionable and auditable: "the plan is vague" cannot be acted on, while "step 3 has no
acceptance criterion for requirement R2" can. A finding with no citation is refused when
the review is recorded, so one uncited finding costs you the whole verdict.

## What your verdict does

The verdict is not advice. It moves the chain:

| Verdict | What happens next |
| --- | --- |
| `approve` | The chain advances and the plan is executed as written. |
| `deny` | A fix session starts, seeded with your findings and their citations, and the plan is revised before anything is implemented. |
| `approve-with-conditions` | Both: the chain advances, and the conditions travel into the next step's brief. |

That is why the findings must be specific. A deny with no actionable finding sends the
planner back with nothing to change, and an approve-with-conditions whose conditions are
never stated is an approve with extra words.

## When you cannot reach a verdict

Say so plainly in the transcript and stop. Do not approve to be agreeable, and do not deny
because something is merely unfamiliar. A chain that parks on an honest "I could not judge
this" is a chain a human can unblock; a chain that advances on a fabricated verdict is one
nobody can audit.

## Red flags

- **A finding with no citation.** Refused at write time — and before that, it is a complaint
  rather than a review.
- **Reading the planner's history.** You have inherited the framing you were asked to judge.
- **A verdict line that is not one of the three tokens.** It fails to parse and the review is
  lost.
- **Scores with no justification.** Unfalsifiable, and therefore uncomparable.
- **Fixing what you review.** A reviewer that edits the plan to make it pass has replaced the
  independent verdict with its own work.
- **Approving because the plan is long.** Length is not coverage; check each requirement
  against a step.
- **Inventing criteria the rubric does not have.** A review scored on its own invented
  dimensions cannot be compared with any other, which defeats the point of scoring at all.
