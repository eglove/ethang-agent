---
name: using-git-worktrees
description: Use when starting feature work that needs isolation from current workspace or before executing implementation plans - ensures an isolated workspace exists via native tools or git worktree fallback
---

# Using Git Worktrees

## Overview

Ensure work happens in an isolated workspace. Prefer your platform's native worktree tools. Fall back to manual git worktrees only when no native tool is available.

**Core principle:** Detect existing isolation first. Then use native tools. Then fall back to git. Never fight the harness.

**Announce at start:** "I'm using the using-git-worktrees skill to set up an isolated workspace."

## Step 0: Detect Existing Isolation

**Before creating anything, check if you are already in an isolated workspace.**

Via exec, capture each value into a variable in your C# script:
  Shell("git", "rev-parse", "--git-dir")          -> gitDir
  Shell("git", "rev-parse", "--git-common-dir")   -> gitCommon
  Shell("git", "branch", "--show-current")        -> branch
Compare resolved full paths of gitDir and gitCommon (Path.GetFullPath).

**Submodule guard:** `GIT_DIR != GIT_COMMON` is also true inside git submodules. Before concluding "already in a worktree," verify you are not in a submodule:

Via exec: Shell("git", "rev-parse", "--show-superproject-working-tree").
A non-empty result means you are in a submodule, not a worktree — treat as
normal repo.

**If `GIT_DIR != GIT_COMMON` (and not a submodule):** You are already in a linked worktree. Skip to Step 2 (Project Setup). Do NOT create another worktree.

Report with branch state:
- On a branch: "Already in isolated workspace at `<path>` on branch `<name>`."
- Detached HEAD: "Already in isolated workspace at `<path>` (detached HEAD, externally managed). Branch creation needed at finish time."

**If `GIT_DIR == GIT_COMMON` (or in a submodule):** You are in a normal repo checkout.

Has the user already indicated their worktree preference in your instructions? If not, ask for consent before creating a worktree:

> "Would you like me to set up an isolated worktree? It protects your current branch from changes."

Honor any existing declared preference without asking. If the user declines consent, work in place and skip to Step 2.

## Step 1: Create Isolated Workspace

**You have two mechanisms. Try them in this order.**

### Create the Worktree

This harness has no native worktree tools — run git directly through exec
(C# script: Shell("git", ...) calls).
#### Directory Selection

Follow this priority order. Explicit user preference always beats observed filesystem state.

1. **Check your instructions for a declared worktree directory preference.** If the user has already specified one, use it without asking.

2. **Check for an existing project-local worktree directory** via exec
   (Directory.Exists on `.worktrees`, then `worktrees`). If found, use it.
   If both exist, `.worktrees` wins.

3. **If there is no other guidance available**, default to `.worktrees/` at the project root.

#### Safety Verification (project-local directories only)

**MUST verify directory is ignored before creating worktree:**

Via exec: Shell("git", "check-ignore", "-q", dir) per candidate — exit 0 means ignored.

**If NOT ignored:** Add to .gitignore, commit the change, then proceed.

**Why critical:** Prevents accidentally committing worktree contents to repository.

#### Create the Worktree

Via exec: Shell("git", "worktree", "add", fullPath, "-b", branchName).
The agent workspace then moves to fullPath; children spawned while it is
active inherit that workspace root.

**Exec failure fallback:** if the exec call fails or git reports a permission error, tell your human partner and work in the current directory instead; run setup and baseline tests in place.

## Step 2: Project Setup

Auto-detect and run appropriate setup:

Detect the project type via File.Exists in your C# script and restore:
- package.json -> Node project: tell your human partner; do not guess toolchains
- Cargo.toml / requirements.txt / pyproject.toml / go.mod -> same rule
For .NET repos (the normal case here): dotnet restore then dotnet build.

## Step 3: Verify Clean Baseline

Run tests to ensure workspace starts clean:

dotnet test for .NET repos. For other project types, ask your human partner
which verification command to use — never guess a foreign toolchain.

**If tests fail:** Report failures, ask whether to proceed or investigate.

**If tests pass:** Report ready.

### Report

```text
Worktree ready at <full-path>
Tests passing (<N> tests, 0 failures)
Ready to implement <feature-name>
```

## Quick Reference

| Situation | Action |
|-----------|--------|
| Already in linked worktree | Skip creation (Step 0) |
| In a submodule | Treat as normal repo (Step 0 guard) |
| `.worktrees/` exists | Use it (verify ignored) |
| `worktrees/` exists | Use it (verify ignored) |
| Both exist | Use `.worktrees/` |
| Neither exists | Check instruction file, then default `.worktrees/` |
| Directory not ignored | Add to .gitignore + commit |
| Exec or permission failure on create | Work in place, tell your human partner |
| Tests fail during baseline | Report failures + ask |
| Foreign project type (Node/Rust/Python/Go) | Ask your human partner; never guess toolchains |

## Common Rationalizations

| Excuse | Reality |
|--------|---------|
| "I'm obviously not in a worktree — no need to check" | Run Step 0. Harness-created isolation and submodules both fool eyeballing; the detection commands settle it. |
| "Skip detection — I'll just create one" | The detection commands settle what eyeballing cannot: existing isolation and submodule traps both fool inspection. Bypassing them is the #1 mistake. |
| "The worktree directory is surely ignored already" | Run `git check-ignore`. An unignored worktree directory commits the whole tree into the repo. |
| "Any directory name works" | Explicit instructions beat an existing project-local directory, which beats the `.worktrees/` default. |
| "The workspace is fresh — baseline tests can wait" | A dirty baseline makes every later failure ambiguous. Run the tests now; proceeding past failures is your human partner's call. |
