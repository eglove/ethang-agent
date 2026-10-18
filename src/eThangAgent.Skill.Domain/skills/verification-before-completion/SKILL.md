---
name: verification-before-completion
description: Verify claims with real evidence — builds, tests, output — before declaring work complete. Use when about to claim work is done, fixed, or passing.
---

# Verification Before Completion

## Overview

This is the report leg of the scientific loop: no conclusion without fresh evidence.
It is the harness's anti-self-grading rule — the verdict leg of every experiment, and
the gate on every completion claim.

**Core principle: a verdict is only as good as its evidence** — fresh command output,
captured now, from the exact check the prediction named. An earlier green run is an
observation about an earlier tree, not evidence about this one.

The reasoning is the same one that governs any experiment. A claim is a conclusion, and
a conclusion drawn from memory, from confidence, or from a check other than the one
that would falsify it is not a conclusion at all — it is a prediction wearing a
verdict's clothes. The report leg asks for one thing: the observation, taken now, that
the claim rests on.

## The Gate Function

```text
Before claiming any status or expressing satisfaction:

1. IDENTIFY: What command proves this claim?
2. RUN: Execute the full command (fresh, complete)
3. READ: Full output, check exit code, count failures
4. VERIFY: Does output confirm the claim?
  - If NO: State actual status with evidence
  - If YES: State claim with evidence
5. ONLY THEN: Make the claim

Skipping any step leaves the claim resting on something other than verification
```

## Common Failures

| Claim | Requires | Not Sufficient |
|-------|----------|----------------|
| Tests pass | Test command output: 0 failures | Previous run, "should pass" |
| Linter clean | Linter output: 0 errors | Partial check, extrapolation |
| Build succeeds | Build command: exit 0 | Linter passing, logs look good |
| Bug fixed | Test original symptom: passes | Code changed, assumed fixed |
| Regression test works | Red-green cycle verified | Test passes once |
| Agent completed | VCS diff shows changes | Agent reports "success" |
| Requirements met | Line-by-line checklist | Tests passing |

## Loop checks — signals a verdict is being stated without evidence

These are self-checks, not verdicts. Each one marks a place where the claim is going
out ahead of the observation that would support it. Catching one means running the
check, not apologizing.

- Using "should", "probably", "seems to" — those words mark a prediction, and a prediction is not a verdict.
- Expressing satisfaction before verification ("Great!", "Perfect!", "Done!") — the claim went out ahead of the observation.
- About to commit/push/PR without verification — the evidence is being assumed at the moment it matters most.
- Trusting an agent's success report — that is another observer's claim; the diff is the observation.
- Relying on partial verification — the uncovered part of the claim stays unsupported.
- Thinking "just this once" — the exception is exactly the case where the missing evidence goes unnoticed.
- Tired and wanting the work over — fatigue selects for the cheapest verdict, not the true one.
- Any wording implying success without having run verification — the wording is the claim, and the run is the evidence.

## Predictions people get wrong

Each row is a prediction that feels true and is not. The right column says what the
evidence actually shows — which is why the report leg is the cheap path, not the slow
one.

| Prediction | What the evidence shows |
|------------|-------------------------|
| "Should work now" | "Should" is a prediction. The verdict needs the run: the same command, fresh output. |
| "I'm confident" | Confidence is a feeling about evidence, not evidence. It predicts the run's result; only the run reports it. |
| "Just this once" | There is no exception case — the one skipped run is the one whose result you never see. |
| "Linter passed" | A linter is a different instrument: it does not compile, so it cannot speak to a build claim. |
| "Agent said success" | Another observer's claim. Read the diff and run the check yourself. |
| "I'm tired" | Exhaustion changes what you want the verdict to be, not what the evidence says. |
| "Partial check is enough" | A partial check speaks only to the part it covered; the rest of the claim stays unobserved, so the verdict is still unsupported. |
| "Different words so rule doesn't apply" | The claim is what a reader takes away, not the phrasing — a paraphrase of success is still a success claim, and it needs the same evidence. |

## Key Patterns

**Tests:**
```text
✅ [Run test command] [See: 34/34 pass] "All tests pass"
❌ "Should pass now" / "Looks correct"
```

**Regression tests (TDD Red-Green):**
```text
✅ Write → Run (pass) → Revert fix → Run (must fail) → Restore → Run (pass)
❌ "I've written a regression test" (without red-green verification)
```

**Build:**
```text
✅ [Run build] [See: exit 0] "Build passes"
❌ "Linter passed" (linter doesn't check compilation)
```

**Requirements:**
```text
✅ Re-read plan → Create checklist → Verify each → Report gaps or completion
❌ "Tests pass, phase complete"
```

**Agent delegation:**
```text
✅ Agent reports success → Check VCS diff → Verify changes → Report actual state
❌ Trust agent report
```

## The Clean-Tree Gate

"Green" means more than tests passing. Before declaring a task or session done:

1. Build succeeds and the full suite passes (fresh output, this message).
2. `git status --porcelain` is empty. Every intended change is committed; every
  unintended change is either reverted or explicitly surfaced to your human
  partner. Uncommitted work is unfinished work — a rename split across files
  has silently stranded itself in the working tree before.
3. The claim you are about to make lists both: test evidence and tree state.

## When To Apply

**Always before:**
- Any variation of success/completion claims
- Any expression of satisfaction
- Any positive statement about work state
- Committing, PR creation, task completion
- Moving to next task
- Delegating to agents

**Rule applies to:**
- Exact phrases
- Paraphrases and synonyms
- Implications of success
- Any communication suggesting completion/correctness

---

For the loop's routing across task types, see `scientific-method`. For the experiment
leg — the failing test that is the falsification — see `test-driven-development`.
