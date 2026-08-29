---
name: ethang-tools-mapping
description: How skills name actions and how they bind to real eThang Agent tools.
---

# eThang Agent Tool Mapping

Skills name actions; this harness binds them to real tools:

| Action (as named by skills) | Binding |
| --- | --- |
| Read a file | `read` (startLine/endLine required, max 1000-line range) |
| Write / edit files | `write` / `edit` |
| Search files | `search_files` |
| Run commands, tests, or git plumbing | `exec` — C# scripting through the exec engine (Roslyn); never shell scripts |
| Dispatch a subagent | `spawn` (non-blocking, returns an id; poll `status`; fetch the report with `result`) |
| Create/update todos | `todo` tool |
| Invoke a skill / load its content | `skill_view` tool (never read raw skill paths; the skill store IS the mechanism) |
| List available skills | `skill_list` tool |
| Ask the human partner a clarifying question | `clarify` tool (MANDATORY during brainstorming) |
| Store or read specs, plans, ledgers, briefs, reports | `state` tools — `state.get` / `state.set` / `state.append` (CAS ledger lines) / `state.list` / `state.find` (full-text search) / `state.prune` (SDD cleanup) |
| Inspect the agent's own database (sessions, transcripts, state, memories, skills, preferences) | `db_schema` / `db_query` (read-only SQL; run `db_schema` first) |
| Commit work | `git_commit` tool (never raw shell commits); the style is the user's host setting — follow the commit-style guidance in this bootstrap, not a parameter |

Windows-native throughout. Tests run via the dotnet CLI with xUnit (`dotnet test`);
repo automation is plain `dotnet` CLI invocations — no `.ps1`/`.sh`/`.cmd`/`.bat`.

The using-skills skill is ALREADY ACTIVE — do not load it again. Load other skills with skill_view when they apply. This bootstrap is injected once per session.
