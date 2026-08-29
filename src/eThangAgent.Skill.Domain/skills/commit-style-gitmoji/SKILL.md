---
name: commit-style-gitmoji
description: Commit message guidance for the Gitmoji style — loaded when the host's commit-style setting selects Gitmoji.
---

# Gitmoji commit style (active host setting)

The user has selected **Gitmoji** as this session's commit style.

When asked to commit (or when finishing a task that ends in a commit), call the `git_commit` tool:

- **Required**: `emoji_key` — the exact `:name:` key from the gitmoji catalog (for example `:sparkles:`, `:bug:`, `:pencil2:`) — plus a single-line `description` (at most 72 characters after trimming).
- **Forbidden**: `type` and `scope` — the emoji already carries the type.
- Subject form: emoji then description.
- Never stage with raw shell; prefer the tool's `files` parameter or stage via exec first.

The tool validates every rule and returns typed errors — when an error names a rule, correct that exact parameter and retry.
