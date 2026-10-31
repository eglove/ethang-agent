# #304: the parallel-invalidation question, resolved

Recorded 2026-10-31 by the #304 session, satisfying that issue's acceptance criterion
"The parallel-invalidation question is resolved in writing before parallel dispatch ships."

## The question

`CompletionGate` compares a WHOLE-WORKSPACE digest. Two sessions writing one checkout
invalidate each other's gate continuously: A's green run goes stale the moment B saves a
file, so per-step gates on a shared tree are mutually invalidating and a parallel chain would
never see a fresh gate.

## The answer

**The gate runs in the agent's own worktree, and again at landing.**

The gate already takes a `workspaceRoot`, so a per-worktree gate is the SAME call against a
different root. Each worktree has its own digest, so parallel sessions stop invalidating each
other, and `CompletionGate` needs no change at all.

The landing queue then requires a FRESH gate on the rebased result, which is the check that
actually matters: it is the only one run against the code that will exist. A gate that passed
in a worktree proves something about that worktree; a gate that passed on the rebased landing
result proves something about `main`.

## What this supersedes

The three candidates the issue listed:

1. Gate at CHAIN level only until per-session worktrees exist - superseded. Chain-level
   gating is not a fallback; it is one of the two places the gate belongs (a chain's
   `Completed` claim), and it shipped here.
2. Scope the digest to the paths a step declared it would touch - rejected. A step's
   declaration is a plan, not a measurement, and a digest over declared paths would certify
   a tree nobody actually built.
3. Give each parallel session its own worktree - ADOPTED, and it is where the gate runs.

## What is NOT resolved here

The CLAIM half of the floor (a later falsification invalidating an earlier success claim) is
a different question, and it is settled by D5: the step gains an `Invalidated` marker without
changing its status, and the chain cannot complete while any step is invalidated. That shipped
in this issue.

The file-coordination mechanism itself - scoped path leases, merge-tree conflict prediction,
the serialized landing queue - is #314. This record states where the GATE runs; #314 states
how concurrent work is scheduled and landed.

## Sequential chains need none of this

A sequential chain has no concurrent writer, so its gate is the ordinary whole-workspace one
and it ships first. Nothing here changes what a sequential chain does today.
