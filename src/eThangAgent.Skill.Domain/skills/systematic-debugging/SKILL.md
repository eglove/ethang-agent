---
name: systematic-debugging
description: Find root causes before fixing symptoms: reproduce, isolate, hypothesize, verify, then fix. Use when facing any bug, test failure, or unexpected behavior.
---

# Systematic Debugging

## Overview

This is the debugging instantiation of the scientific loop. Phase 1 is observation and
Phase 2 is pattern analysis; Phase 3 is the experiment; Phase 4 is the report. The
discipline is unchanged - root cause before fixes, one hypothesis at a time, a minimal
test that proves it - and the loop says why each part earns its place.

**Core principle: a fix without a stated prediction is a guess.** Before touching code,
state what you expect the fix to change and the check that would prove it wrong. That
pair - prediction and falsifying check - is what turns a change into an experiment. A
change with neither is a guess that happened to be typed into an editor: if it works you
learn nothing about why, and if it fails you have no information about what to try next.

A symptom fix is the special case worth naming: it makes the observation go away
without explaining it, so the same cause returns somewhere the fix does not reach. The
loop is not slower than guessing - it is what stops the guessing from repeating.

## When to Use

Use for ANY technical issue:
- Test failures
- Bugs in production
- Unexpected behavior
- Performance problems
- Build failures
- Integration issues

**Use this ESPECIALLY when:**
- Under time pressure (emergencies make guessing tempting)
- "Just one quick fix" seems obvious
- You've already tried multiple fixes
- Previous fix didn't work
- You don't fully understand the issue

**Don't skip when:**
- Issue seems simple (simple bugs have root causes too)
- You're in a hurry (rushing guarantees rework)
- Manager wants it fixed NOW (systematic is faster than thrashing)

## The Four Phases

You MUST complete each phase before proceeding to the next.

### Phase 1: Root Cause Investigation

**BEFORE attempting ANY fix:**

1. **Read Error Messages Carefully**
   - Don't skip past errors or warnings
   - They often contain the exact solution
   - Read stack traces completely
   - Note line numbers, file paths, error codes

2. **Reproduce Consistently**
   - Can you trigger it reliably?
   - What are the exact steps?
   - Does it happen every time?
   - If not reproducible → gather more data, don't guess

3. **Check Recent Changes**
   - What changed that could cause this?
   - Git diff, recent commits
   - New dependencies, config changes
   - Environmental differences

4. **Gather Evidence in Multi-Component Systems**

   **WHEN system has multiple components (CI → build → signing, API → service → database):**

   **BEFORE proposing fixes, add diagnostic instrumentation:**
   ```text
   For EACH component boundary:
     - Log what data enters component
     - Log what data exits component
     - Verify environment/config propagation
     - Check state at each layer

   Run once to gather evidence showing WHERE it breaks
   THEN analyze evidence to identify failing component
   THEN investigate that specific component
   ```

   **Example (multi-layer system):**
   ```bash
   # Layer 1: Workflow
   echo "=== Secrets available in workflow: ==="
   echo "IDENTITY: ${IDENTITY:+SET}${IDENTITY:-UNSET}"

   # Layer 2: Build script
   echo "=== Env vars in build script: ==="
   env | grep IDENTITY || echo "IDENTITY not in environment"

   # Layer 3: Signing script
   echo "=== Keychain state: ==="
   security list-keychains
   security find-identity -v

   # Layer 4: Actual signing
   codesign --sign "$IDENTITY" --verbose=4 "$APP"
   ```

   **This reveals:** Which layer fails (secrets → workflow ✓, workflow → build ✗)

5. **Trace Data Flow**

   **WHEN error is deep in call stack:**

   See `root-cause-tracing.md` in this directory for the complete backward tracing technique.

   **Quick version:**
   - Where does bad value originate?
   - What called this with bad value?
   - Keep tracing up until you find the source
   - Fix at source, not at symptom

### Phase 2: Pattern Analysis

**Find the pattern before fixing:**

1. **Find Working Examples**
   - Locate similar working code in same codebase
   - What works that's similar to what's broken?

2. **Compare Against References**
   - If implementing pattern, read reference implementation COMPLETELY
   - Don't skim - read every line
   - Understand the pattern fully before applying

3. **Identify Differences**
   - What's different between working and broken?
   - List every difference, however small
   - Don't assume "that can't matter"

4. **Understand Dependencies**
   - What other components does this need?
   - What settings, config, environment?
   - What assumptions does it make?

### Phase 3: Hypothesis and Testing

**Scientific method:**

1. **Form Single Hypothesis**
   - State clearly: "I think X is the root cause because Y"
   - Write it down
   - Be specific, not vague

2. **Test Minimally**
   - Make the SMALLEST possible change to test hypothesis
   - One variable at a time
   - Don't fix multiple things at once

3. **Verify Before Continuing**
   - Did it work? Yes → Phase 4
   - Didn't work? Form NEW hypothesis
   - DON'T add more fixes on top

4. **When You Don't Know**
   - Say "I don't understand X"
   - Don't pretend to know
   - Ask for help
   - Research more

### Phase 4: Implementation

**Fix the root cause, not the symptom:**

1. **Create Failing Test Case**
   - Simplest possible reproduction
   - Automated test if possible
   - One-off test script if no framework
   - MUST have before fixing
   - Use the `test-driven-development` skill for writing proper failing tests

2. **Implement Single Fix**
   - Address the root cause identified
   - ONE change at a time
   - No "while I'm here" improvements
   - No bundled refactoring

3. **Verify Fix**
   - Test passes now?
   - No other tests broken?
   - Issue actually resolved?
   - Use the `verification-before-completion` skill before claiming success

4. **If Fix Doesn't Work**
   - STOP
   - Count: How many fixes have you tried?
   - If < 3: Return to Phase 1, re-analyze with new information
   - **If ≥ 3: STOP and question the architecture (step 5 below)**
   - DON'T attempt Fix #4 without architectural discussion

5. **If 3+ Fixes Failed: Question Architecture**

   **Pattern indicating architectural problem:**
   - Each fix reveals new shared state/coupling/problem in different place
   - Fixes require "massive refactoring" to implement
   - Each fix creates new symptoms elsewhere

   **STOP and question fundamentals:**
   - Is this pattern fundamentally sound?
   - Are we "sticking with it through sheer inertia"?
   - Should we refactor architecture vs. continue fixing symptoms?

   **Discuss with your human partner before attempting more fixes**

## Loop checks - signals your reasoning has left the loop

These are self-checks, not verdicts. If you catch yourself thinking one of them, the
hypothesis is being assumed rather than tested - go back to observation and gather the
evidence that would make it a hypothesis instead.

- "Quick fix for now, investigate later" - the fix is being chosen before the cause is known.
- "Just try changing X and see if it works" - no prediction, so the result cannot be read.
- "Add multiple changes, run tests" - several variables at once; a pass or a fail explains nothing.
- "Skip the test, I'll manually verify" - the check is not repeatable, so it is not evidence.
- "It's probably X, let me fix that" - "probably" marks a guess; state the observation X would explain.
- "I don't fully understand but this might work" - the experiment has no falsifying check.
- "Pattern says X but I'll adapt it differently" - the reference is being assumed, not read.
- "Here are the main problems: [lists fixes without investigation]" - conclusions before observation.
- Proposing solutions before tracing data flow - the symptom is being treated as the cause.
- "One more fix attempt" (when already tried 2+) - the accumulated failures are evidence against the current hypothesis, not an argument for repeating it.
- Each fix reveals a new problem in a different place - the evidence now points at the architecture rather than at any single cause.

Each of these means the hypothesis is being assumed rather than tested - return to
observation and let the evidence name the cause.

If 3+ fixes have failed: question the architecture (see Phase 4.5).

## Signals from your human partner

Your partner reads the transcript, not your intentions, and their redirections are
observations you have not yet made. Each one names a hypothesis that is running
untested:

- "Is that not happening?" - you assumed without verifying; the observation is missing.
- "Will it show us...?" - the evidence-gathering step was skipped; add the instrumentation.
- "Stop guessing" - fixes are being proposed without an understanding of the cause.
- "Ultra-think this" - the fundamentals need questioning, not just the symptom.
- "We're stuck?" (frustrated) - the current approach is not producing evidence; change it.

When you see these, the loop has an untested hypothesis in it. Return to observation.

## Predictions people get wrong

Each row is a prediction that feels true and is not. The right column says what the
evidence actually shows - which is why the loop is the faster path, not the slower one.

| Prediction | What the evidence shows |
|------------|-------------------------|
| "Issue is simple, don't need process" | Simple issues have root causes too. The loop is fast for simple bugs - observation is usually one read of the error. |
| "Emergency, no time for process" | Systematic debugging is FASTER than guess-and-check thrashing. Under time pressure the loop is the cheaper path, not the luxury one. |
| "Just try this first, then investigate" | The first fix sets the pattern, and it was chosen without a prediction - so its result cannot distinguish cause from coincidence. Observe first; then the first fix is an experiment. |
| "I'll write test after confirming fix works" | An untested fix doesn't stick: without a failing test first there is no prediction to confirm and no evidence the change addressed the cause rather than the symptom. The test first IS the experiment. |
| "Multiple fixes at once saves time" | Can't isolate what worked - several variables changed, so a pass proves nothing about which one mattered and a fail eliminates nothing. That is how new bugs get introduced alongside the old one. |
| "Reference too long, I'll adapt the pattern" | Partial understanding guarantees bugs: the parts you skipped are exactly the parts whose absence produces the failure. Read it completely, then the differences you find are evidence. |
| "I see the problem, let me fix it" | Seeing symptoms is not understanding root cause - the visible failure is an observation, and the cause is a hypothesis that has not been tested. |
| "One more fix attempt" (after 2+ failures) | 3+ failures is evidence of an architectural problem, not of a fix that was nearly right. Question the pattern instead of fixing again. |

## Quick Reference

| Phase | Key Activities | Success Criteria |
|-------|---------------|------------------|
| **1. Root Cause** | Read errors, reproduce, check changes, gather evidence | Understand WHAT and WHY |
| **2. Pattern** | Find working examples, compare | Identify differences |
| **3. Hypothesis** | Form theory, test minimally | Confirmed or new hypothesis |
| **4. Implementation** | Create test, fix, verify | Bug resolved, tests pass |

## When Process Reveals "No Root Cause"

If systematic investigation reveals issue is truly environmental, timing-dependent, or external:

1. You've completed the process
2. Document what you investigated
3. Implement appropriate handling (retry, timeout, error message)
4. Add monitoring/logging for future investigation

**But:** 95% of "no root cause" cases are incomplete investigation.

## Supporting Techniques

These techniques are part of systematic debugging and available in this directory:

- **`root-cause-tracing.md`** - Trace bugs backward through call stack to find original trigger
- **`defense-in-depth.md`** - Add validation at multiple layers after finding root cause
- **`condition-based-waiting.md`** - Replace arbitrary timeouts with condition polling

---

For the report leg - fresh evidence before any completion claim - use the
`verification-before-completion` skill. For how the loop routes across task types, see
`scientific-method`.
