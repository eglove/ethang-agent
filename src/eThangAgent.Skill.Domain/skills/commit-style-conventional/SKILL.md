---
name: commit-style-conventional
description: Commit message guidance for the Conventional Commits style — loaded when the host's commit-style setting selects Conventional.
---

# Conventional commit style (active host setting)

The user has selected **Conventional Commits** as this session's commit style.

When asked to commit (or when finishing a task that ends in a commit), call the `git_commit` tool:

- **Required**: `type` from the fixed set — feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert — plus a single-line `description` (at most 72 characters after trimming).
- **Optional**: a lowercase `scope` matching `[a-z0-9-]+` in parentheses; a `body` paragraph after a blank line.
- **Forbidden**: `emoji_key` — the type already carries the intent.
- Subject form: `type(scope): description` (scope omitted, `type: description`).
- Never stage with raw shell; prefer the tool's `files` parameter or stage via exec first.

The tool validates every rule and returns typed errors — when an error names a rule, correct that exact parameter and retry.
