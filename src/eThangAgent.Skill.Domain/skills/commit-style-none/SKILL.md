---
name: commit-style-none
description: Commit message guidance for the plain style (no prefix) — loaded when the host's commit-style setting selects None.
---

# Plain commit style (active host setting)

The user has selected the **None** (plain) style as this session's commit style.

When asked to commit (or when calling the `git_commit` tool):

- **Only**: a single-line `description` (at most 72 characters after trimming) — the description stands alone.
- **Forbidden**: `type`, `scope`, and `emoji_key` — a plain description, nothing else.
- Optional `body` paragraph after a blank line.
- Never stage with raw shell; prefer the tool's `files` parameter or stage via exec first.

The tool validates every rule and returns typed errors — when an error names a rule, correct that exact parameter and retry.
