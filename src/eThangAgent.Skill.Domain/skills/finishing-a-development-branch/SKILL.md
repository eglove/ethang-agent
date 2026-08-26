---
name: finishing-a-development-branch
description: Use when implementation is complete, all tests pass, and you need to decide how to integrate the work
---

# Finishing a Development Branch

## Overview

**Core principle:** Verify tests → Detect environment → Present options → Execute choice → Clean up.

**Announce at start:** "I'm using the finishing-a-development-branch skill to complete this work."

## Step 1: Verify Tests

Build and run the project's full suite: `dotnet build` then `dotnet test`. If the running desktop app locks bin/Debug (MSB3021/3027), build and test with `-c Release`.

**If tests fail**, report the failures and stop — the menu comes after a green suite:

```text
Tests failing (<N> failures). Must fix before completing:

[Show failures]
```

**If tests pass:** verify the working tree is clean — run `git status --porcelain`
via exec; any output is uncommitted work that must be committed or reverted before
the integration menu appears. A dirty tree at finish time means something was done
but never recorded.

Then continue to Step 2.

## Step 2: Detect Environment

Via exec, capture each value into variables in your C# script:
  Shell("git", "rev-parse", "--git-dir")        -> gitDir
  Shell("git", "rev-parse", "--git-common-dir")  -> gitCommon
  Shell("git", "rev-parse", "--show-toplevel")   -> worktreePath
Capture now, while still inside the workspace — Step 5 changes directory
before cleanup (Step 6) needs these values. Compare resolved full paths of
gitDir and gitCommon (Path.GetFullPath).

This determines which menu to show and how cleanup works:

| State | Menu | Cleanup |
|-------|------|---------|
| `GIT_DIR == GIT_COMMON` (normal repo) | Standard 3 options | No worktree to clean up |
| `GIT_DIR != GIT_COMMON`, named branch | Standard 3 options | Provenance-based (see Step 6) |
| `GIT_DIR != GIT_COMMON`, detached HEAD | Reduced 2 options (no merge) | Externally managed — leave in place |

## Step 3: Determine Base Branch

The base branch is whatever this work forked from — usually named in the
plan, the conversation, or the branch's upstream. If it is not already
known, ask: "This branch split from <your best guess> - is that correct?"
Confirm before merging: merging into the wrong base is expensive to undo.

## Step 4: Present Options

**Normal repo and named-branch worktree — present exactly these 3 options:**

```text
Implementation complete. What would you like to do?

1. Merge back to <base-branch> locally
2. Push and create a Pull Request
3. Keep the branch as-is (I'll handle it later)

Which option?
```

**Detached HEAD — present exactly these 2 options:**

```text
Implementation complete. You're on a detached HEAD (externally managed workspace).

1. Push as new branch and create a Pull Request
2. Keep as-is (I'll handle it later)

Which option?
```

Present the menu exactly as written — concise, with every option coming
from the list above. Discarding the work happens only in response to your
human partner explicitly asking for it (see "If your human partner asks to
discard the work" below). Wait for their answer; the integration decision
is theirs.

## Step 5: Execute Choice

### Option 1: Merge Locally

Via exec, in order:
  mainRoot = Shell("git", "rev-parse", "--show-toplevel")   // CWD safety anchor
  Shell("git", "checkout", baseBranch)
  Shell("git", "pull")
  Shell("git", "merge", featureBranch)
Then verify tests on the merged result: dotnet build + dotnet test.

If tests fail on the merged result: stop, leave the worktree and branch in
place, and investigate — nothing has been pushed, so the merge is local
and recoverable.

Once the merged result is green: clean up the worktree (Step 6), then
delete the branch:

Via exec: Shell("git", "branch", "-d", featureBranch)

### Option 2: Push and Create PR

Via exec: Shell("git", "push", "-u", "origin", featureBranch).
From a detached HEAD, name the branch on the remote:
  Shell("git", "push", "origin", "HEAD:refs/heads/" + newBranch)

Then create the pull/merge request against <base-branch> with the forge's
tooling — its CLI if one is available, or the creation URL most forges
print when you push — following the repo's PR template and conventions if
present, and report the URL to your human partner.

Keep the worktree — your human partner iterates on PR feedback there.

### Option 3: Keep As-Is

Report: "Keeping branch <name>. Worktree preserved at <path>."

### If your human partner asks to discard the work

This path exists only as a response to an explicit request to throw the
work away. Confirm first:

```text
This will permanently delete:
- Branch <name>
- All commits: <commit-list>
- Worktree at <path>

Type 'discard' to confirm.
```

Wait for that exact confirmation. When it arrives:

Via exec: mainRoot = Shell("git", "rev-parse", "--show-toplevel"); use it as
the working directory anchor for cleanup calls.

Then clean up the worktree (Step 6) and force-delete the branch:

Via exec: Shell("git", "branch", "-D", featureBranch)

## Step 6: Cleanup Workspace

**Runs for Option 1 and confirmed discards.** Options 2 and 3 always
preserve the worktree. Both callers have already changed directory to the
main repo root — worktree removal must run from outside the worktree —
and use the gitDir/gitCommon/worktreePath values captured in Step 2, from
before that directory change.

**If gitDir == gitCommon:** Normal repo, no worktree to clean up. Done.

**If `WORKTREE_PATH` is under `.worktrees/` or `worktrees/`:** The skills system
created this worktree — we own cleanup:

Via exec:
  Shell("git", "worktree", "remove", worktreePath)
  Shell("git", "worktree", "prune")   // self-healing: clears stale registrations

**If removal is refused** (`contains modified or untracked files`): the
worktree holds files that exist nowhere else — uncommitted plans, notes,
or scratch work. Never `--force` on your own initiative. Show your human
partner what is at stake and ask:

Via exec: Shell("git", "-C", worktreePath, "status", "--porcelain", "-uall")

```text
Worktree removal refused — these files were never committed:

<file list>

1. Commit them to <branch> before cleanup
2. Move them into <main repo root>
3. Delete them (unrecoverable)

Which?
```

Carry out the choice, then remove the worktree.

**Otherwise:** The host environment owns this workspace — leave it in place.

## Quick Reference

| Option | Merge | Push | Keep Worktree | Cleanup Branch |
|--------|-------|------|---------------|----------------|
| 1. Merge locally | yes | - | - | yes |
| 2. Create PR | - | yes | yes | - |
| 3. Keep as-is | - | - | yes | - |
| Discard (explicit request only) | - | - | - | yes (force) |

## Common Rationalizations

| Excuse | Reality |
|--------|---------|
| "Tests passed earlier this session" | Run the suite on the tree you are about to integrate. A green run only proves the tree it ran on. |
| "They obviously want it merged" | Integration is your human partner's decision. Present the menu and wait. |
| "They seem done with this feature — I'll offer to discard it" | The menu is complete as written. Discard happens only when your human partner asks for it in so many words. |
| "'Yeah, get rid of it' counts as confirmation" | Only the typed word `discard` authorizes deletion. |
| "The PR is up, so the worktree is clutter now" | PR feedback gets fixed in that worktree. It stays until the work lands. |
| "This other worktree looks stale — I'll clean it too" | Clean up only worktrees under `.worktrees/` or `worktrees/`. Everything else belongs to the host. |
| "Removal refused — `--force` is just finishing the cleanup" | The refusal means files exist only in that worktree. `--force` destroys them permanently. Show your human partner and ask. |
| "The merged-result failure is probably flaky" | A failing merged result stops everything. Branch and worktree stay put while you investigate. |
| "The base branch is obviously main" | Confirm the fork point or ask. Merging into the wrong base is expensive to undo. |
| "The push was rejected — force-push will fix it" | A rejected push means the remote moved. Investigate; force-push only on your human partner's explicit request. |
