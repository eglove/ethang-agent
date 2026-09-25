# eThang Agent

eThang Agent is an AI agent harness for Windows, built on .NET 10 and delivered through an Avalonia desktop application. The harness is the scaffolding an AI model acts through — agent loop, tool dispatch, session persistence, desktop UI — while the model supplies the decisions. It pairs a strict Domain-Driven Design core (layered bounded contexts, CQRS, Specifications, Anti-Corruption Layers) with a pragmatic tool surface: it talks to [OpenRouter](https://openrouter.ai/) behind provider-neutral contracts, executes model-written C# scripts in-process through a dedicated ACL, and persists every session to an app-owned SQLite database so past work can be recalled.

> `AGENTS.md` is the engineering handbook — architecture rules and conventions for working *on* this codebase. This README covers what the harness *is* and how to *use* it.

## What it can do today

- One Avalonia desktop frontend over a shared host-agnostic core (`eThangAgent.Composition`) — streamed responses with reasoning/tool activity, sub-agent spawning, durable session persistence

- **Agent watchdog** — a per-session maintenance loop (default tick 60 s) detects spawned child agents that stopped making progress: the agent loop beats a heartbeat every iteration and around tool calls; a child idle past 15 minutes is cancelled and restarted on the same id with a wrap-up nudge (its partial transcript is preserved and resumed); a second breach marks it Failed(Hung) so the parent gets a well-formed failure. The same watchdog runs host-side for out-of-process children (the ChildHost attaches one per child run), so a hung remote child is detected and retired locally — the app never guesses from absent beats. Every decision lands in a structured `watchdog_events` audit table. Separately, a process-lifetime RSS monitor samples the app's working set for as long as the app runs — no session required — recording rate-limited `RssBreached` rows plus one `RssSustained` row per sustained breach (observe-only; a future force-recycle policy will key off the sustained marker)
- Conversational coding loop against [OpenRouter](https://openrouter.ai/) — each agent tab is wired for exactly one provider for its lifetime
- Desktop shell opens on a main window with a left-hand menu bar; **Open Workspace** opens a dialog with an AI-provider dropdown (OpenRouter, offered when its API key is configured) and a **Choose Workspace** folder picker. The opened tab is bound to that provider until closed (the status bar shows it), and the choice is remembered in the app database so the dialog pre-selects it next time. Each workspace roots path resolution, `exec` scripts' `Workspace`, and curated-memory scoping. Session-start file reads are user-configured in Settings → Files: checked global files load for every workspace, checked workspace files for that workspace only, and their verbatim contents are injected into the system prompt (missing or unreadable files are skipped with a note; nothing loads unless configured). Remote (out-of-process) child agents anchor at the same workspace root and receive the same configured files
- Live response streaming — assistant text renders as it arrives,
  including interstitial reasoning between tool calls (SSE; falls back transparently when a
  provider endpoint does not stream)
- Transient-failure retries with exponential backoff (429/408/5xx,
  transport errors, timeouts — four attempts by default; a server `Retry-After` hint is
  honored). A streaming request is retried only while nothing has been emitted to the UI;
  mid-stream failures surface as errors so output is never duplicated
- Reasoning streams render readably: hard wraps inside words and
  CamelCase identifiers join, wraps before closing punctuation attach directly, real
  sentence/bullet breaks stay, and blank-line floods collapse to one paragraph break
- Length-truncated turns continue automatically: when a response hits the model's output
  limit (`finish_reason: length`), the partial answer is kept, a continuation nudge is
  appended, and the loop resumes — bounded per turn, with `MaxOutputContinuations` raised
  as a visible error if the cap is exhausted. A stream cut off without its terminator is a
  `StreamInterrupted` error, never a silently truncated "answer"
- Turn steering and interruption — input typed while a turn runs is never dropped: it is
  posted to a session inbox and delivered to the model as a user message at the next safe
  point (never splitting a tool call from its results). The Stop button hard-cancels
  the active turn and all of this session's sub-agents; half-finished tool batches are repaired
  in place so conversation history stays valid, and the interruption surfaces as
  `Error [TurnCancelled]` / child `interrupted` outcomes rather than crashes or lost state
- Selectable transcript text in the desktop app — select any message or reasoning block
  and copy it with Ctrl+C
 - Rich transcript rendering — MarkView-powered markdown in the transcript, live while a block streams (re-renders coalesced) and finalized on close
   (headings, bold/italic, inline code, fenced code blocks, lists, links), and tool calls/results appear as
   expandable cards: pretty-printed JSON arguments on the call, the full result content on the result
   (errors highlighted red)
- `exec` tool — in-process C# scripting via Roslyn with artifact capture and structured output
- Every tool call carries a mandatory `timeoutSeconds` budget (1–3600): a call exceeding its
  budget is stopped and returned as `Error [ToolTimeout]` for self-correction; the agent's
  tool loop itself runs uncapped until the model answers without tool calls, with per-turn
  cancellation always honored
- `read` tool — bounded, line-range text file reads
- `write` tool — create/replace files behind an explicit overwrite gate
- `edit` tool — exact literal replacements with occurrence verification, or line-range replacement (lines N..M, no anchor, endLine past EOF rejected)
- `write_markdown` tool — renders a structured JSON document into well-formed markdown deterministically (headers, lists, tables, alerts, frontmatter); returns the string or writes it to a workspace file behind the same overwrite gate as `write`
- `sqlite_query` tool — the same read-only SELECT/WITH inspection as `db_query`,
  but against any SQLite file inside the workspace (inventory databases, exports);
  the path is resolved against the workspace root and refused outside it, and the
  connection is read-only so nothing can be created or written
- `db_schema` / `db_query` tools — read-only inspection of the agent's own app database:
  `db_schema` lists tables, columns, and indexes (row counts opt-in); `db_query` runs one
  SELECT/WITH statement on a read-only connection with a bounded row cap — writes, multiple
  statements, and ATTACH/DETACH are rejected
- `git_status` / `working_diff` tools — inspect branch state and bounded diffs
- `git_commit` tool — validated commits of the current index in the user's chosen style
  (Conventional, Gitmoji, or plain — a host setting, resolved live per commit; never a model parameter),
  with an optional `files` array of workspace-relative paths to stage first (relative-only: no drives, no `..`, no `.`)
- `worktree` tool — list git worktrees, or create/remove one under the workspace's `.worktrees/` folder
  (branch `worktree/<name>` from HEAD; dirty worktrees refuse removal unless forced)
- `!` chat commands — a line starting with `!` runs as a shell command against the workspace
  (through the machine's shell: pwsh, PowerShell, or cmd — probed in that order) instead of going
  to the model, even mid-turn. Output is captured to the local transcript and the app database,
  never sent as context; the conversation receives only a compact system message
  (`User ran: ... (id: N, exit code M)`) plus the read-back tool:
- `command_output` tool — reads a stored `!` command's output by id (default: the latest run),
  optionally capped to the last `tailLines` lines; ids persist per workspace across sessions
- `web_fetch` tool — fetch a web page or resource over HTTP(S) and return readable text:
  HTML pages are converted to markdown (headings, links with absolute URLs, lists, tables,
  fenced code); other textual responses (plain text, JSON, XML) pass through verbatim; binary
  responses are rejected. Redirects are followed and the output's first line always annotates
  the final URL, status, content type, and size
- Curated memory loop — `memories.search/add/update/remove/purge` over a categorized, full-text,
  versioned knowledge base, with turn-boundary nudges prompting curation
- Skill subsystem: embedded skills (the development methodology plus per-style commit guidance),
  file skills from user-configured agentskills.io directories (precedence built-in > file >
  learned, collisions announced), a budgeted always-on skill listing in every session's system
  prompt, session-start bootstrap injection of the using-skills contract, and `skill_list` /
  `skill_view` / `skill_manage` tools
- Skill registry: search the skills.sh directory (`skill_search`) and install/update/uninstall
  community skills into a configured skill directory (`skill_registry`) — GitHub repos,
  `owner/repo/skill` addresses, `file:///` fixture URLs, or skills.sh entries; every install is
  gated by a deterministic content scan (BLOCK findings abort unconditionally; advisory
  findings require explicit confirmation), name collisions are refused against authoritative
  built-ins and learned skills, and installed skills surface immediately through hot reload
- Skill invocation channel: type `/` in the chat input for an autocomplete popup (manual
  skills marked and listed first, case-insensitive filter, arrow-key navigation, Tab or Enter
  to complete) and submit `/skill-name arguments` — or ask the agent, which calls
  `skill_invoke`. Both resolve through one shared core; the skill's content enters the
  conversation as a system message (bodies over 4,000 characters fall back to
  `skill_view`), manual skills resolve exactly like the rest, and unknown `/names` send
  as ordinary messages

- **Model Settings** window (left menu, visible whenever a tab is open) — the one
  surface for per-session model choices. The **Model** section is a searchable catalog of
  the provider's lineup (plus **Auto (smart selection)** on OpenRouter); the
  **Reasoning effort** selector offers model default or max, extra high, high, medium,
  low, minimal, none. The **Sampling** section carries the twelve knobs
  (top-p, top-k, frequency/presence/repetition penalties, min-p, top-a,
  seed, verbosity, parallel tool calls, temperature and max-tokens caps); the OpenRouter
  section adds provider routing, the twelve server tools, plugins, and loop budgets.
  Choices apply from the next turn, root and children alike, and are persisted per
  workspace + provider — no config files
- **Context accounting + auto-compaction** — the status bar shows a live `CTX 148.2K/1M, 15%`
  readout (hover for the estimated system-prompt/messages/tools breakdown), plus the session id (first
  8 characters, full id on hover, click ⧉ to copy). The transcript auto-scrolls only while you rest
  at the bottom: your own messages never steal the scroll, scrolling up pauses the follow-the-tail
  behavior until you return to the bottom (or press End), and the reading position survives tab
  switches. OpenRouter
  reports per-request token usage; when utilization crosses 80% at a turn boundary the oldest
  conversation is summarized by a compaction model (per-workspace setting under Settings —
  default: cheapest capable) and replaced by that handoff summary, so long sessions keep
  going without hitting the window. The model can also compact on its own: the `context_edit`
  tool lists the indexed messages and removes or shortens selections at a milestone, and a
  shrunk session persists and resumes exactly like a compacted one. Compacted sessions persist and resume like any other
- **Sessions** entry (left menu) — resume a previous conversation: every persisted
  session is listed newest-first with its workspace, provider, start time, and status;
  sessions already open in a tab are greyed out (hover explains why). Confirming a row
  reopens that session on its original provider and workspace and replays the full
  persisted transcript — including tool calls and results — so the conversation continues
  where it stopped, with prior history carried into the next turn. Opening a workspace
  through **Open Workspace** always starts a NEW session; resume is a deliberate pick from
  the menu, never automatic per directory. A workspace can hold many sessions, and
  resuming one never merges another session's history into it
- `todo` tool — durable workspace task list with compare-and-swap writes
- Capability registry exposing agent tools plus spawnable sub-agents, durable workspace state, and memory recall
- Nested sub-agents with depth limits and concurrency caps
- Per-child capability grants enforced at dispatch (`tool.allow`/`tool.deny` on `agent.spawn`; violations return `Error [GrantViolation]` and are audited). A spawn can also anchor the child's workspace at a validated directory (`workspaceRoot`, absolute, existing, inside the parent's EFFECTIVE root — the parent's own anchor for grandchild chains, else the session workspace): the child's exec scripts resolve at the anchor, and registry re-rooting engages for workspace-scoped tools when they are present in the child's registry. The child's ENTIRE path-rooted surface is re-rooted by the anchor: exec scripts AND the capability surface (read/write/edit/git and other workspace-relative tools) resolve paths at the anchor — paths outside it return `Error [PathOutsideWorkspace]` — and an anchored parent spawns anchored children by default (a workspaceRoot-less child inherits the parent's anchor).
- Fix rounds on settled children: `agent.resume` re-enters a settled child (completed or failed) on the same id with a fix-round message — transcript, workspace anchor, and grants carry over. In-process children only; remote children report `Error [ResumeUnsupported]`
- Steering mid-run: `agent.wait` (one await instead of polling), `agent.send`/`parent.send` push-delivery with bounded persistent mailboxes, urgency with audited preemption, `agent.notify-subtree`/`agent.notify-ancestors` one-call broadcasts with per-target receipts, and subtree interrupt. The session tab shows an unread-steering badge while a child has queued messages (pushed by the child event stream — it appears on delivery and clears when the child drains)
- Out-of-process children: `eThangAgent.ChildHost` over a named-pipe transport (`eThangAgent.Transport.ACL`) with declared connection-loss failures
- Structured child results (JSON-schema validated with one repair round), fan-out/fan-in spawn graphs (`agent.fanout` parses its `children` argument strictly and fails the join fast when a start fails, surfacing the real error code), and a consent-based agent link registry — links are created in the Desktop's per-tab **Links** dialog (🔗 rail entry: pick the target agent, name the link, confirm; revoke from the same list), and `agent.route` delivers to the consented link by name — including agents opened in a
different session of the same app (in-process or running in the ChildHost); links persist
per workspace across restarts
- **Computer Use** — when enabled in settings (`computer_use_enabled`, default OFF), the agent can observe and operate native Windows apps through a supervised broker: the accessibility tree as readable text, clicks and keyboard input, and screenshots that reach vision-capable models. Consent-first: the toggle is off until you switch it on.
- Session persistence, recall, and resume via a versioned, app-owned SQLite database

## Requirements

- Windows (path handling and process execution assume Windows)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An [OpenRouter](https://openrouter.ai/keys) API key

## Getting started

1. Clone the repository.
2. Build and run:

```powershell
dotnet build
dotnet run --project src/eThangAgent.Desktop # Avalonia desktop app
```

3. Add an API key: click **⚙ Settings** at the bottom of the left menu and paste your [OpenRouter](https://openrouter.ai/keys) key. It is stored DPAPI-encrypted in the app database (only your Windows user can read it back) and applies to newly opened agents.

The window opens directly on the shell: no workspace and no pre-configured key are required up front. Click **Open Workspace**, pick a directory, and that agent's chat opens as a tab; repeat to work with several workspaces side by side.

## Usage

### Configuration

| Setting | Where | Notes |
| ------- | ----- | ----- |
| OpenRouter API key | **⚙ Settings → API Keys** | DPAPI-encrypted in the app database. Providers without a key are not offered in the Open-Agent dialog. |
| OpenRouter base URL | **⚙ Settings → Advanced** | Optional; blank keeps the provider default (`https://openrouter.ai`). Stored in the app database; an invalid value is refused at save and re-validated at startup — never silently coerced. |
| `ETHANG_AGENT_DB` | environment variable | Optional; overrides the database location. This is the one remaining environment variable — the bootstrap chicken-and-egg (the database itself must be locatable before preferences can be read) — not app configuration. |
| Sub-agent settings (`DefaultModel`, `MaxConcurrentAgents`, `RemoteHost`) | Settings window — **Agents** tab; stored in the app database (`app_preferences`). Absent values fall back to shipped defaults (max concurrent 4, no default model, in-process children). | Invalid values are refused at save and re-validated at startup — configuration is validated strictly, never silently coerced. There is deliberately no child-timeout setting: wall-clock is never a child cancellation source. `RemoteHost` is `false` by default; `true` runs children in the out-of-process `eThangAgent.ChildHost`, which survives app restarts (the app re-attaches and reconciles running children exactly). |
| Sub-agent watchdog (`TickInterval`, `IdleThreshold`, `MaxWrapUpAttempts`) | Settings window — **Agents** tab; stored in the app database. Durations use constant format (`00:00:02`); every knob is optional and blank keeps the default (60-second tick, 15-minute idle threshold, 1 wrap-up attempt). Invalid text is refused at save and at startup — never clamped, never coerced (bare integers are rejected: without units they would bind as days). The configured knobs govern BOTH watchdogs: the app-side loop and the out-of-process child host's — the child host picks changes up for newly opened agents, while the app-side loop binds them once per process (changes reach it after the app restarts). |

Saved keys apply to newly opened agents; already-open tabs keep the credentials they were created with.

The active provider is chosen per agent in the Open-Agent dialog — switching providers is deliberately a different experience (its own model catalog, defaults, and tool surface), not a merged model list. The model is chosen per tab through the **Model Settings** window (left menu, visible whenever a tab is open), and the choice applies from the next turn to the root agent and children alike. It is remembered per workspace + provider and restored when the same directory reopens; picking **Auto (smart selection)** again returns the session to automatic resolution. Reasoning effort works the same way through the window's **Reasoning effort** selector, with **Model default** returning the session to the provider's own behavior.

The picker offers **Auto (smart selection)** plus a searchable list of every OpenRouter model (deduped across provider endpoints, shown with effective pricing and context size). Auto is the default: the agent defers model selection to the first user prompt, where a two-stage LLM pipeline categorizes that prompt and selects the best model from OpenRouter's fetched catalog based on the task category and price. The pipeline re-runs on every 10th user message thereafter so the model tracks the conversation's evolving task. Sub-agent spawns similarly select models based on their task prompts. Selection failures fall back to the default model (`openrouter/auto`) and surface as a transcript notice.

### Skills

Every session's system prompt carries a budgeted skills listing so the model can prefer a
matching skill over improvising and load the full body with `skill_view`. Skills come from
three sources with fixed precedence: built-in > file > learned.

- **Built-in skills** ship with the app: the development-methodology set (debugging,
  brainstorming, TDD, planning, code review, worktrees, sub-agent driving, verification),
  the `using-skills` session contract, the tools-mapping skill, and one commit-guidance skill
  per commit style.
- **File skills** load from directories you configure in **⚙ Settings → Files → Skill
  directories**. Global directories are scanned for every workspace; workspace directories
  for the workspace they belong to. Each entry can be toggled on or off, and a workspace
  directory equal to a global one is scanned once. Directories use the
  [agentskills.io](https://agentskills.io/) layout: each immediate subdirectory holding a
  `SKILL.md` is one skill, and a `SKILL.md` at the directory root is a single-skill directory;
  everything else in the folder is ignored. With no directories configured, no file skills load.
- **Frontmatter**: `name` and a non-empty `description` are required; `metadata.version`
  (integer) and `disable-model-invocation: true` (manual skill) are honored; `allowed-tools`,
  `license`, and `compatibility` are known but unapplied, and unknown keys load with a warning
  instead of failing the file. One unreadable or unparseable skill degrades to a warning in
  the listing — the rest still load.
- **Precedence and collisions**: a file skill never shadows a built-in, and a learned skill
  never shadows either. A same-name collision is announced as a `[collision]` line in the
  listing and in `skill_list`, and `skill_manage` refuses to create a learned skill under an
  existing built-in or file-skill name.
- **Manual skills** (`disable-model-invocation: true`) are excluded from the always-on listing,
  marked `[manual]` in `skill_list`, and load by name through `skill_view` — the user names
  them, the model loads them.
- **Invocation channel**: type `/` as the first character of the chat input to open the
  autocomplete popup — skills list manual-first, filter as you type, ArrowUp/Down to move,
  Tab or Enter to complete the name, Esc to close. Submitting `/skill-name arguments`
  injects the skill as a system message ahead of your message; an unknown name sends as an
  ordinary message, and an ambiguous one sends nothing and lists the matches in a notice.
  The agent can invoke skills the same way through the `skill_invoke` tool. Skills invoked
  through either channel record no usage rows — viewing with `skill_view` remains the only
  usage signal.
- **The listing budget** is 8,000 characters for the whole block, with descriptions truncated
  at 60 characters, grouped Built-in / Global directory skills / Workspace directory skills /
  Learned. Load failures render as `[warning]` lines; when the budget forces content out, a
  truncation marker states what was shown and dropped — never silent. Remote (out-of-process)
  children receive the identical listing.

### Where your data lives

- Sessions, state transitions, and events: one SQLite database owned by the app, by default at `%LOCALAPPDATA%\eThangAgent\eThangAgent.db` (override with `ETHANG_AGENT_DB`). Schema changes run through versioned migrations.
- API keys: the same database (Settings → API Keys), DPAPI-encrypted with current-user scope — plaintext keys never touch disk.
- Exec artifacts: `%TEMP%\eThangAgent\exec-artifacts`.

## Development

```powershell
dotnet build   # solution: eThangAgent.slnx
dotnet test    # xUnit v3 on Microsoft.Testing.Platform — unit, integration, E2E
```

Production build (framework-dependent single file for win-x64):

```powershell
dotnet publish src/eThangAgent.Desktop -c Release -r win-x64 --self-contained false
```

- Every change leaves the build green.
- Unit tests use fakes only — a domain test never knows Roslyn, HTTP, or OpenRouter exist.
- Integration tests exercise real ACL implementations; E2E tests drive the desktop app headless against a local mock provider server (OpenRouter-shaped).
- Read `AGENTS.md` for architecture rules and conventions before writing code.

## Repository layout

```text
src/     One project per bounded context and ACL (see AGENTS.md for the map)
         plus eThangAgent.Composition (shared host-agnostic wiring) and
         eThangAgent.Desktop (Avalonia frontend)
tests/   Mirror-image test projects
docs/    Project documentation only; no workflow artifacts
        (design specs and implementation plans live as plan records — create/read them through the 'plan' capability; working ledgers, task briefs, and reports stay in workspace state keys — not repo files)
```

## Roadmap

`grand-plan.md` holds long-range ideas (roadmap stages, a desktop UI, integrations). It is explicitly aspirational — a rough idea, not a guide for current implementation.
