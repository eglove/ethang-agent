# Parallel chain steps: the design record

Recorded 2026-11-06. A chain is a linear cursor today: one step runs at a time, and a step's
completion is what starts the next one. This record settles what it takes for a declared group of
steps to run together, and is the spec the implementation plan argues from.

## What a group is

A chain step may declare that it belongs to a **group**. Every member of a group starts at the same
moment and the group is a **barrier**: nothing after it starts until every member has settled.

A group needs at least two members. A group of one buys a worktree, a merge step, and a barrier to
run a single agent, so chain start refuses it by name.

Grouping lives on the CHAIN record, never on the plan. The precedent is the step role: the plan says
what should be true, the chain says how it runs. A grouping on the plan would let a plan edit
silently reschedule work already in flight.

## Worktrees, and why the group needs them

Every member of a group runs in its own git worktree. This is not an optimisation; it is what makes
the completion gate honest. `CompletionGate` compares a whole-workspace digest, so two sessions
writing one checkout invalidate each other's passing runs continuously - a member's green run goes
stale the moment a sibling saves a file. Per-worktree, each member's gate reads its own checkout and
the interference disappears with no change to `CompletionGate` itself.

Only group members get a worktree. Sequential steps keep running in the workspace checkout exactly
as they do today, which is the line the deferred coordination design (#314) drew and this record
keeps.

### The repository requirement

A chain that declares a group requires the workspace to be a git repository. Chain start refuses by
name when it is not. Running a declared schedule sequentially instead would be a lie about what ran,
and the refusal costs one sentence at the moment the model can fix it.

## The reconciler step

Worktrees isolate the work; they do not put it back together. Each group names a **reconciler**: a
plan step that merges the members' branches into the workspace checkout.

- The reconciler must be the plan position immediately after the group's last member. "Merge these,
  then do something else, then merge" has no defined meaning, and "the next position" is checkable
  at chain start while "somewhere later" is a rule nobody can enforce.
- The reconciler is an implementer. A review step that merged would be judging and doing at once.
- The reconciler's brief names each member's branch explicitly, and states whether the workspace
  checkout has uncommitted changes, listing the dirty files. It is told, not refused: git is the
  model's to handle, and parking a chain over something an agent resolves in one command is the
  worse trade.
- The reconciler MAY resolve a merge conflict, and must record what it resolved and why in its
  outcome. The review step after it is the check that makes resolution safe; forbidding resolution
  would make the reconciler a formality that always parks.

## Commits are the model's

A member must commit its work before completing its step, and the harness does not commit on its
behalf - a commit message the model did not write attributes work to a sentence nobody authored.

The enforcement is a NUDGE, not a park: a member's completion is refused while its worktree has
uncommitted changes, naming the exact command to run. That is the same machinery that already
refuses an unbacked done-claim, it reaches the model while it is still alive and can act on the
refusal, and it costs one retry instead of a whole extra session.

The reconciler reports an empty branch and parks naming that member. By then the member's session is
closed and there is nothing to nudge.

## Failure inside a group

Members that fail or park do not stop their siblings: the group runs to completion, which is the
rule the harness already follows for sequential chains - failure parks, it does not abort.

When the group settles with a failed or parked member, the chain runs the reconciler for the members
that SUCCEEDED and then parks naming the member that did not. Work that passed is not stranded
because a sibling failed, and the park still names exactly what needs a human.

## The completion gate and the file claims

File claims stop coordinating a group: two members editing "the same file" are editing two different
files in two different worktrees. The claim lease still protects the workspace checkout from an agent
stepping on a person's edits, and stays as it is.

## What a member is told

A member's brief names its siblings - positions and titles only. A sibling's outcome does not exist
while the group is running, and inventing one would be a lie in the record. A step that does not know
three other agents are working will confidently change a file one of them owns.

The brief's completion text changes for members: a member's completion starts nothing until the group
settles, so the sentence "completing your step is what starts the next one" is false for a member and
is replaced with what is true.

A review step may not be a group member. A reviewer judges work that already exists, and a member has
no such work - its siblings are in other checkouts and their branches are not merged. The review that
matters is the one after the reconciler.

## Review after a group

A review step after a group judges the MERGED result, not the branches. The order is: group runs,
reconciler lands it, review judges it. The reviewer sees one checkout, exactly as it does today.

A denial reopens the member whose position the finding cites, not the reconciler. Findings already
must cite a step position; in a group that citation is the routing information. Without this rule a
denial about a member's work would send the reconciler back to merge branches nobody changed.

## The surface

Each member's cell states its position and names its siblings. Groups get no vocabulary of their own
in the rail: the cells are already where a person watches agents work, and a wave concept would be a
second name for the same fact.

The reconciler is an ordinary chain step, so it has an ordinary cell. The merge is not a hidden
harness act.

## Cleanup

A group's worktrees are removed when the reconciler lands successfully, and kept when it parks on a
conflict. Successful work needs no evidence; a conflict does. This is a deliberate departure from the
existing worktree rule ("a failed child's worktree is evidence, not garbage"), which exists for
failed child runs rather than for a group whose merge succeeded.

## What this record does not settle

Concurrent writes to the workspace checkout by the reconciler and a person are not coordinated beyond
the existing file claims. The serialized landing queue and merge prediction that #314 designed remain
unbuilt, and this record does not claim they are unnecessary - it claims a group's landing is one
merge step whose conflicts a model handles with a review behind it.

## Decisions, in one place

| # | Decision |
| --- | --- |
| D1 | A group is declared on the chain, by the model, at chain start. |
| D2 | A group is a barrier: nothing after it starts until every member settles. |
| D3 | Every member runs in its own git worktree; sequential steps do not. |
| D4 | A group requires a git repository; chain start refuses otherwise. |
| D5 | A group needs at least two members. |
| D6 | A review step may not be a group member. |
| D7 | A group names a reconciler, which must be the position immediately after its last member. |
| D8 | The reconciler is an implementer and may resolve conflicts, recording what and why. |
| D9 | A member's uncommitted worktree refuses its completion, naming the command. |
| D10 | Members that fail do not stop siblings; the reconciler lands the successes, then the chain parks. |
| D11 | A review after a group judges the merged result and reopens the member a finding cites. |
| D12 | A member's brief names its siblings by position and title. |
| D13 | Worktrees are removed on a successful landing, kept on a park. |
| D14 | The reconciler is told the checkout's dirty files and decides what to do. |
