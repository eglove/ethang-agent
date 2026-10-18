---
name: scientific-method
description: Use when debugging, feature, refactoring, or research tasks are non-trivial and need the scientific loop's legs routed to the skills that carry them.
---

# Scientific Method

## Overview

Every non-trivial task in this harness runs one loop: **observe** (what is actually
true), **hypothesize** (what might explain it), **predict** (what would follow if the
hypothesis holds), **experiment** (the cheapest test of that prediction), **analyze**
(what the result says), **report** (the claim, together with the evidence for it).

This skill routes that loop. It does not carry the legs itself — each leg is carried
by a skill built for it, and this skill says which one. Read the routing table, load
the carrying skills, and run the loop through them.

**Core principle: gate, don't mandate.** The loop is not a ceremony to perform on
every request; it is what makes a claim checkable. Small, well-understood work needs
none of it. What the loop actually protects is the prediction:

> A prediction states what you expect to see and names the exact check that would
> prove you wrong.

That falsifying check is the load-bearing leg. Without it, "experiment" degrades into
"try something and see", and "report" degrades into "it seems to work". With it, a
failed experiment is information rather than a setback: it eliminates a hypothesis
and narrows the next one.

**Hypothesize late.** Evidence before commitment. An observation gathered first
usually makes the hypothesis obvious; a hypothesis formed first turns every later
observation into a search for confirmation. The order matters more than the speed.

## Routing by task fit

| Task type | Loop legs | Carrying skills |
| --- | --- | --- |
| Debugging | observe -> hypothesize -> experiment -> report | `systematic-debugging` (full loop), `test-driven-development` (failing test = prediction), `verification-before-completion` (report) |
| Feature work | spec -> plan -> execute -> review -> finish | `grill` (observation), `writing-plans` (predictions = acceptance criteria, each with its falsifying check), `experiment-driven-development` (experiments), `requesting-code-review` + `receiving-code-review` (external verdicts), `finishing-a-development-branch` (conclusions + ledger gate) |
| Refactoring | invariants -> transform -> verify | `test-driven-development` (property tests as predictions), `verification-before-completion` |
| Research | question -> claim -> evidence | claim-evidence form (below) |
| Document writing | claims with evidence only | `writing` skill + claims-with-evidence rule (below) |
| Trivial work | none | no loop; gate, don't mandate |

## Adaptations

**Research.** Every claim records who claims it — the paper, the source, the
measurement — so the loop's report leg becomes attribution rather than a fix.
Hypotheses here are claims about the world, not about code, and the falsifying check
is usually "what would this source have to say for me to be wrong" rather than a
command you run.

**Documents.** The loop collapses to a single rule: every claim states its evidence.
No phases, no ledger, no experiment section. A document that asserts something
without saying how it is known hands the reader the verification work.

## Red flags

- Stating a fix before stating what observation would refute it.
- A prediction with no falsifying check named.
- Reporting success without fresh evidence from the falsifying check.
- Running the loop on trivial work (process for its own sake).
