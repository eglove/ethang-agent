---
name: ethang-tools-mapping
description: Resolve a skill action to the real eThang Agent tool it binds to. Use at the start of any conversation: the harness tool binding for every skill action name.
---

# eThang Agent Tool Mapping

Skills name actions; this harness binds them to real tools. IMPORTANT: these are
exec-bridge calls, not direct chat tools — the loop's chat surface is `exec`; every
binding above is invoked inside exec, e.g. `Tools.Invoke("skill_view", new { name = "<name>" })`
or `Tools.Invoke("spawn", new { taskPrompt = "..." })`:

| Action (as named by skills) | Binding |
| --- | --- |
| Read a file | `read` (startLine/endLine required, max 1000-line range) |
| Write / edit files | `write` / `edit` |
| Run commands, tests, or git plumbing | `exec` — C# scripting through the exec engine (Roslyn); never shell scripts |
| Dispatch a subagent | `spawn` — exec-bridge call: `Tools.Invoke("spawn", new { taskPrompt = "...", label = "..." })` (non-blocking, returns an id; await with `Tools.Invoke("agent.wait", ...)`; fetch the report with `agent.result`) |
| Create/update todos | `todo` tool |
| Invoke a skill / load its content | `skill_view` — exec-bridge call: `Tools.Invoke("skill_view", new { name = "<name>" })` (never read raw skill paths; the skill store IS the mechanism) |
| List available skills | `skill_list` — exec-bridge call: `Tools.Invoke("skill_list", new { })` |
| Store or read specs, plans, ledgers, briefs, reports | `state` tools — `state.get` / `state.set` / `state.append` (CAS ledger lines) / `state.list` / `state.find` (full-text search) / `state.prune` (SDD cleanup); design specs and implementation plans are plan records via the 'plan' provider (`plan.create`/`plan.show`) |
| Inspect the agent's own database (sessions, transcripts, state, memories, skills, preferences) | `db_schema` / `db_query` (read-only SQL; run `db_schema` first) |
| Commit work | `git_commit` tool (never raw shell commits); the style is the user's host setting — follow the commit-style guidance in this bootstrap, not a parameter |

Windows-native throughout. Tests run via the dotnet CLI with xUnit (`dotnet test`);
repo automation is plain `dotnet` CLI invocations — no `.ps1`/`.sh`/`.cmd`/`.bat`.

The using-skills skill is ALREADY ACTIVE — do not load it again. Load other skills with skill_view when they apply. This bootstrap is injected once per session.
