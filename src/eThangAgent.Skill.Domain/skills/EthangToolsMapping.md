---
name: ethang-tools-mapping
description: Resolve a skill action to the real eThang Agent tool it binds to. Use at the start of any conversation: the harness tool binding for every skill action name.
---

# eThang Agent Tool Mapping

Skills name actions; this harness binds them to real tools. **`exec` is the only tool** —
everything else is a `Tools.*` dispatch inside an exec program. There are no direct chat tools.

```text
Tools.Invoke("skill_view", new { name = "<name>" })
Tools.read(new { path = "file.txt", startLine = 1, endLine = 50 })
```

Discover the surface instead of guessing: `Tools.List()` lists every action, and
`Tools.Describe("<name>")` gives one action's contract.

| Action (as named by skills) | Binding |
| --- | --- |
| Read a file | `read` (`startLine`/`endLine` required, max 1000-line range) |
| Write / edit files | `write` / `edit` (`edit` takes `hunks` for a multi-place change, applied atomically) |
| Run commands, tests, or git plumbing | `exec` — C# scripting through the exec engine (Roslyn); never shell scripts. `Shell(exe, args...)` spawns a process with no shell intermediary |
| Load a skill / list skills | `skill_view` / `skill_list` (never read raw skill paths; the skill store IS the mechanism) |
| Track what is being worked on | plan steps — `plan.create` with a `steps` array, then `plan.update-step` (`status`: Pending / InProgress / Done / Skipped). There is no todo list: plan steps, chain records, and the experiment ledger are the units |
| Execute a plan mechanically | `chain.start` — converts a plan into a chain the HARNESS advances; you do not start the next step |
| Record what the experiment showed | `experiment` — `record-hypothesis` / `record-prediction` / `record-verdict` / `record-observation` / `record-conclusion` / `record-skip` / `list` / `summary`, appended to `sci.<slug>/ledger` |
| Store or read durable workspace state | `state` tools — `state.get` / `state.set` / `state.append` (CAS ledger lines) / `state.list` / `state.find` (full-text search) / `state.prune` / `state.checkgoal` (runs the workspace's `goal/check` commands and records the result) |
| Inspect the agent's own database (sessions, transcripts, state, skills, preferences) | `db_schema` / `db_query` (read-only SQL; run `db_schema` first) |
| Inspect any workspace SQLite file | `sqlite_query` (read-only SELECT/WITH, workspace-confined) |
| Commit work | `git_commit` (never raw shell commits); the style is the user's host setting — follow the commit-style guidance in this bootstrap, not a parameter |
| Start a fresh session in this workspace | `new_session` (returns the new session's id; the tab is replaced in place) |
| Fetch a web page | `web_fetch` |
| Read a document | `docx` / `pdf` (read, render, create, edit) |
| Read an image | `read_image` (PNG/JPEG, attached to the result as an image part) |
| Actions gated by host settings | some actions exist only when the host enables them; `Tools.List()` reports what is actually bound in this session |
| Call a configured MCP server | `mcp` (`list` / `call`) |

**Agents are sessions, not subagents.** A session does not dispatch children and then wait for
them: the harness starts a session per chain step and reacts to a completion fact. Three `agent`
actions remain — `status`, `result`, and `cancel` — for reading and stopping a run. The messaging
actions that used to let agents talk to each other are retired; agents communicate by STATUS
through the database, and human steering of a running session is the one message channel that
survives. `Tools.List()` is the authority on what exists: run it rather than trusting any list,
including this one.

Windows-native throughout. Tests run via the dotnet CLI with xUnit (`dotnet test`);
repo automation is plain `dotnet` CLI invocations — no `.ps1`/`.sh`/`.cmd`/`.bat`.

The using-skills skill is ALREADY ACTIVE — do not load it again. Load other skills with skill_view when they apply. This bootstrap is injected once per session.
