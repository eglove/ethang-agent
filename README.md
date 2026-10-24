# eThang Agent

eThang Agent is an AI agent harness for Windows, built on .NET 10 and delivered through an Avalonia desktop application. The harness is the scaffolding an AI model acts through — agent loop, tool dispatch, session persistence, desktop UI — while the model supplies the decisions. It pairs a strict Domain-Driven Design core (layered bounded contexts, CQRS, Specifications, Anti-Corruption Layers) with a pragmatic tool surface: it talks to [OpenRouter](https://openrouter.ai/) behind provider-neutral contracts, executes model-written C# scripts in-process through a dedicated ACL, and persists every session to an app-owned SQLite database so past work can be recalled.

> `AGENTS.md` is the engineering handbook — architecture rules and conventions for working *on* this codebase. This README covers what the harness *is* and how to *use* it.

## What it can do today

- One Avalonia desktop frontend over a shared host-agnostic core (`eThangAgent.Composition`) — streamed responses with reasoning/tool activity, sub-agent spawning, durable session persistence

- **Agent watchdog** — a per-session maintenance loop (default tick 60 s) detects spawned child agents that stopped making progress: the agent loop beats a heartbeat every iteration and around tool calls; a child idle past 15 minutes is cancelled and restarted on the same id with a wrap-up nudge (its partial transcript is preserved and resumed); a second breach marks it Failed(Hung) so the parent gets a well-formed failure. The same watchdog runs host-side for out-of-process children (the ChildHost attaches one per child run), so a hung remote child is detected and retired locally — the app never guesses from absent beats. Every decision lands in a structured `watchdog_events` audit table. Separately, a process-lifetime RSS monitor samples the app's working set for as long as the app runs — no session required — recording rate-limited `RssBreached` rows plus one `RssSustained` row per sustained breach (observe-only; a future force-recycle policy will key off the sustained marker)
- Conversational coding loop against [OpenRouter](https://openrouter.ai/) — each agent tab is wired for exactly one provider for its lifetime
- Desktop shell opens on a main window with a left-hand menu bar; **Open Workspace** opens a dialog with an AI-provider dropdown (OpenRouter, offered when its API key is configured), a **Choose Workspace** folder picker, and the session-file / skill-directory configuration for the launch (added through native file and folder pickers). The opened tab is bound to that provider until closed (the status bar shows it), and the choice is remembered in the app database so the dialog pre-selects it next time. Each workspace roots path resolution, `exec` scripts' `Workspace`, and durable state-key scoping. Session-start file reads are user-configured in the Open Workspace dialog: checked global files load for every workspace, checked workspace files for that workspace only, and their verbatim contents are injected into the system prompt (missing or unreadable files are skipped with a note; nothing loads unless configured). Remote (out-of-process) child agents anchor at the same workspace root and receive the same configured files
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
 - Status bar branch display — a git workspace's current branch shows in the tab's status bar and
   updates live on branch switches, checkouts, and commits (event-driven on `.git` writes; a
   non-git workspace shows nothing)
 - Server-side tool call surfacing — provider-executed tool calls (OpenRouter's `openrouter:web_search`
   and the Responses API's `web_search_call` family) never enter the message history, so the
   transcript surfaces each as a system line with its query plus the result URLs the wire carried
  - Generated images are real artifacts — the `openrouter:image_generation` tool's output is
    written into the workspace under `.ethang/images/` (format sniffed from the bytes, 20 MB cap)
    and rendered inline in the transcript, so the picture the user paid for is a file they can
    open rather than a dangling reference to a path on the provider's machine
- `exec` tool — in-process C# scripting via Roslyn with artifact capture and structured output
- Every tool call carries a mandatory `timeoutSeconds` budget (1–3600): a call exceeding its
  budget is stopped and returned as `Error [ToolTimeout]` for self-correction; the agent's
  tool loop itself runs uncapped until the model answers without tool calls, with per-turn
  cancellation always honored
- `read` tool — bounded, line-range text file reads with a freshness guard: every read's annotation carries a file-version token (the (Length, LastWriteTimeUtc) pair the ACL captures at the read moment); re-reading a range the model already holds at the same version returns an elided stub naming the held span instead of the content (`force: true` returns it anyway). The ledger is turn-local and per-agent — reset at turn start and wherever history is replaced (compaction, context shrink) — so elision never hides content the model no longer holds. Read's path policy is read-only-broad: relative paths resolve against the workspace root (and a child's anchor) and may not leave it, while absolute paths are accepted from anywhere on disk — mutating tools keep strict workspace containment
- `read_image` tool — read one image file from the workspace and attach it to the tool
  result so the model can see it (PNG/JPEG sniffed from file bytes, 20 MB cap; the image
  is withheld with a notice when the session model has no image input)
- `docx` tool — read, edit, create, and inspect .docx documents: a numbered block
view (paragraphs/tables with styles and pagination), atomic addressed edit batches
(replace text, set styles, insert/delete paragraphs, cells, rows, run formatting,
literal find/replace) with a stale-edit version guard, create from a pinned markdown
subset, comment list/add/delete, tracked-revision list/accept/reject, and embedded
image extraction (PNG/JPEG attach; other formats listed). Macro-enabled (.docm)
content is refused; the extension alone is never trusted (magic-byte + package sniffing)
- `pdf` tool — read, render, create, and manipulate PDF documents: paginated
 per-page text extraction with a version token, page rendering to PNG images (attached
 when the model accepts images, up to 10 pages per call), create from a pinned markdown
 subset, and atomic structural manipulation (rotate, delete, reorder, extract, merge,
 watermark, set metadata) behind a stale-edit version guard. Non-PDF and encrypted files
 are refused; named caps bound file size, pages, DPI, batch size, and markdown size
- `write` tool — create/replace files behind an explicit overwrite gate; written text has its line endings normalized mechanically to the target file's own convention (the project's for a new file: `.editorconfig` `end_of_line`, then `.gitattributes` `eol=`, then the workspace's existing majority, then LF), and the annotation line states which convention landed
- `edit` tool — exact literal replacements with occurrence verification, or line-range replacement (lines N..M, no anchor, endLine past EOF rejected); the replacement is normalized to the file's own line-ending convention before splicing (untouched lines keep their exact bytes), so an edit never introduces a foreign terminator; a missed anchor quotes the file's nearest matching region (exact line numbers and content) so one retry corrects the anchor
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
- Image paste & drop — paste a clipboard image (Ctrl+V) or drop image files onto the chat
  surface to attach up to 4 images (PNG/JPEG, 20 MB each) to the next message; staged
  images render as removable chips above the input
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
- `openrouter_management` tool — review OpenRouter usage logs for debugging through the
  [Management API](https://openrouter.ai/docs/guides/overview/auth/management-api-keys):
  `generation` (one request's log line — model, provider, finish reason, tokens, cost, latency),
  `generation_content` (the stored prompt/completion/error of one request), `credits` (balance),
  `activity` (per-day usage rows, 30 days or one date), and `analytics_meta` / `analytics_query`
  (aggregated usage queries — metrics, dimensions, granularity, time range). Requires the optional
  management key under **⚙ Settings → API Keys**; model API keys never serve management routes and
  management keys never touch model calls
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
  skills marked and listed first, then session commands, then plain skills; case-insensitive
  filter, arrow-key navigation, Tab or Enter to complete) and submit `/skill-name arguments`
  — or ask the agent, which calls `skill_invoke`. Both resolve through one shared core; the
  skill's content enters the conversation as a system message (bodies over 4,000 characters
  fall back to `skill_view`), manual skills resolve exactly like the rest, and unknown
  `/names` send as ordinary messages
- **Start a new session in place** — `/new <prompt>` (listed in the `/` popup as a command,
  badged `[command]`) starts a fresh session in the CURRENT tab's workspace: the tab is
  replaced, not duplicated, so the transcript clears and the status bar names the new session
  id, while the old session closes gracefully and stays resumable from the **Sessions** entry.
  It is refused while a turn is running (stop it first). The agent can do the same through the
  `new_session` tool, which asks the app to start the session and answers with its id

- **Model Settings** window (left menu, visible whenever a tab is open) — the one
  surface for per-session model choices. The **Model** section is a searchable catalog of
  the provider's lineup; the
  **Reasoning effort** selector offers model default or max, extra high, high, medium,
  low, minimal, none. The **Sampling** section carries the twelve knobs
  (top-p, top-k, frequency/presence/repetition penalties, min-p, top-a,
  seed, temperature and max-tokens caps as text fields; verbosity, parallel tool
  calls and the fixed-vocabulary routing/plugin fields as dropdowns over the
  provider-documented values); the OpenRouter section adds provider routing,
  the ten wired server tools, plugins, and loop budgets.
  Choices apply from the next turn, root and children alike, and are persisted per
  workspace + provider — no config files
- **Reasoning is stored as telemetry, never as context** — the reasoning text the provider
  streams is accumulated on the response and recorded once per iteration as a `reasoning` row
  in the app database's append-only `agent_events` table (identity: agent + turn + iteration),
  so after a session the thinking is readable from the database for debugging, evals, and safety
  review. Nothing reads it back: the request builder has no path to the event log, so the next
  request is byte-identical whether or not a trace was persisted, and the streaming display is
  unchanged. Recorded best-effort with the run's cancellation token ignored — a crash or
  interrupt leaves every completed iteration's trace persisted, and a store fault never fails a
  turn.
- **Context accounting + auto-compaction + in-loop overflow recovery** — the status bar shows a live `CTX 148.2K/1M, 15%`
  readout (hover for the estimated system-prompt/messages/tools breakdown), plus `, cache 82%` and
   `, reasoning 1.2K` segments whenever the provider reports a prompt-cache hit or a reasoning-token
   count (an unreported count renders nothing at all; a reported zero renders as a real zero), plus the
   session id (first
  8 characters, full id on hover, click ⧉ to copy). The transcript auto-scrolls only while you rest
  at the bottom: your own messages never steal the scroll, scrolling up pauses the follow-the-tail
  behavior until you return to the bottom (or press End), and the reading position survives tab
  switches. OpenRouter
  reports per-request token usage; compaction is model-driven: the model trims served
  tool results as it works (the `context_trim` tool replaces a spent result with a one-line
  relocation marker naming what produced it and how to re-fetch it — clean at task
  milestones, not when the window fills), while the harness compaction pressures derive
  from the context window itself (`CompactionPolicy`: a forced deep clean from a ~80%
  ceiling on small windows sliding to 50% at 1M+, a one-time soft reminder below the
  ceiling naming the pressure). The deep clean replaces the oldest exchange groups with a
  summarizer model's handoff summary (per-workspace setting under Settings — default:
  cheapest capable), protects everything through the first user message, and compresses
  oversized kept tool results to relocation markers until it lands at the policy target,
  so long sessions keep going without hitting the window. The model can also shrink its
  own context at a milestone: the `context_edit` tool lists the indexed messages and
  removes or shortens selections, and a shrunk session persists and resumes exactly like a
  compacted one. Compacted sessions persist and resume like any other.
   Every provider request carries the session's durable id as OpenRouter's `session_id` (sticky routing
   for prompt caching): the root uses its persisted session id, each child its own, so multi-turn
   requests stay pinned to one provider endpoint and keep the cache warm
- **Sessions** entry (left menu) — resume a previous conversation: every persisted
  session is listed newest-first with its workspace, provider, start time, and status;
  sessions already open in a tab are greyed out (hover explains why). Confirming a row
  reopens that session on its original provider and workspace and replays the full
  persisted transcript — including tool calls and results — so the conversation continues
  where it stopped, with prior history carried into the next turn. Opening a workspace
  through **Open Workspace** always starts a NEW session; resume is a deliberate pick from
  the menu, never automatic per directory. A workspace can hold many sessions, and
  resuming one never merges another session's history into it. A session replaced in place
  by `/new` (or by a compaction handoff) is closed gracefully and appears here too, so
  nothing is lost when a conversation restarts
- `todo` tool — durable workspace task list with compare-and-swap writes
- Capability registry exposing agent tools plus spawnable sub-agents, durable workspace state, and memory recall
- Nested sub-agents with depth limits and concurrency caps
- Per-child capability grants enforced at dispatch (`tool.allow`/`tool.deny` on `agent.spawn`; violations return `Error [GrantViolation]` and are audited). A spawn can also anchor the child's workspace at a validated directory (`workspaceRoot`, absolute, existing, inside the parent's EFFECTIVE root — the parent's own anchor for grandchild chains, else the session workspace): the child's exec scripts resolve at the anchor, and registry re-rooting engages for workspace-scoped tools when they are present in the child's registry. The child's ENTIRE path-rooted surface is re-rooted by the anchor: exec scripts AND the capability surface (read/write/edit/git and other workspace-relative tools) resolve paths at the anchor — paths outside it return `Error [PathOutsideWorkspace]`, except that `read` keeps its read-only-broad policy (relative paths confined to the anchor, absolute paths accepted from anywhere) — and an anchored parent spawns anchored children by default (a workspaceRoot-less child inherits the parent's anchor).
- Fix rounds on settled children: `agent.resume` re-enters a settled child (completed or failed) on the same id with a fix-round message — transcript, workspace anchor, and grants carry over. In-process children only; remote children report `Error [ResumeUnsupported]`
- Steering mid-run: `agent.wait` (one await instead of polling), `agent.send`/`parent.send` push-delivery with bounded persistent mailboxes, urgency with audited preemption, `agent.notify-subtree`/`agent.notify-ancestors` one-call broadcasts with per-target receipts, and subtree interrupt. The session tab shows an unread-steering badge while a child has queued messages (pushed by the child event stream — it appears on delivery and clears when the child drains)
- Out-of-process children: `eThangAgent.ChildHost` over a named-pipe transport (`eThangAgent.Transport.ACL`) with declared connection-loss failures
- Root turns over that transport — a root conversation turn can be forwarded to the child host as a `root-turn` envelope and streamed back as `root-turn-event` envelopes (deltas, notices, tool activity, a settle). The app runs no agent work for such a turn; it sends the input plus the seed it already holds and renders what the host streams. Every turn is bounded by an idle window that resets on each event plus a separate send bound for putting one frame on the wire — there is deliberately no total-duration ceiling, so a long turn runs until it finishes or the user stops it — and every failure is typed — `InvalidTurnId`, `HostUnavailable`, `TurnCancelled`, `TurnUnaddressable` — so a caller is never left waiting. The host heartbeats while a turn is quiet, so the idle window measures liveness rather than busyness: a long script or a long sub-agent wait no longer looks like a dead host, and a turn the app gives up on is stopped on the host so the session keeps working. The host RUNS the turn (its own conversation, its own agent loop); a turn it cannot run settles with the real reason — "not bound to a root session" for a missing session binding, and the underlying cause (a database it could not open, a settings file it could not read) for anything else
- Structured child results (JSON-schema validated with one repair round) and fan-out/fan-in spawn graphs (`agent.fanout` parses its `children` argument strictly and fails the join fast when a start fails, surfacing the real error code). `agent.route` delivers to consented links by name (the registry is store-backed and persists per workspace across restarts; consent is granted programmatically), including agents opened in a
different session of the same app (in-process or running in the ChildHost)
- **Split-screen subagent view** — when the main agent spawns subagents, the workspace view splits side by side (`| Main Agent | Subagents |`): the right column is an accordion of the running children, each row expandable to watch its transcript in realtime (pushed by the child event stream, transcripts streamed from the store). The panel appears only while subagents exist and collapses back to the full-width chat when they finish
- **Computer Use** — when enabled in settings (`computer_use_enabled`, default OFF), the agent can observe and operate native Windows apps through a supervised broker: the accessibility tree as readable text, clicks and keyboard input, and screenshots that reach vision-capable models. Consent-first: the toggle is off until you switch it on.
- **MCP (Model Context Protocol)** — the agent can call tools on configured MCP servers through one `mcp` dispatch tool (`list` shows configured servers with their connection state; `call` invokes a tool). Servers connect lazily on first dispatch and their tool lists cache per workspace; the definition budget stays flat no matter how many servers are configured. Trust-first: a server connects only after it is approved — the 🔌 MCP Servers dialog (per open tab) is the config form (name, transport, command or URL, args/env/headers JSON, scope, pinned version, a per-server gate for mutating calls) and the approval door (approve/revoke are the trust events, logged to the decision log; editing any trust-relevant field — command, args, env, headers, pinned version — re-opens approval; removal deletes its tokens and log); the same dialog shows each server's live state with its last error and captured stderr. A failed connect is a structured error plus a status entry, and the next call retries. stdio servers spawn contained: environment inheritance off (they see only the SDK default set plus their configured env, never your credentials uninvited), a kill-on-close Job Object so a server can never outlive the app, and stderr captured to a bounded tail for diagnosis; a server that dies mid-session is reconnected automatically on the next dispatch. A server can also gate its mutating calls (a logged, structured refusal until you lift the gate), and subagents can be granted MCP reach by namespace (`mcp.github.*` grants one server's tools; a bare `mcp` grant reaches exactly what the parent reached).
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
| OpenRouter management key (optional) | **⚙ Settings → API Keys** | DPAPI-encrypted in the app database. A separate [management API key](https://openrouter.ai/docs/guides/overview/auth/management-api-keys) (created at openrouter.ai/settings/keys) that unlocks the `openrouter_management` tool — the debugging surface: per-request generation logs, stored prompt/completion content, credits, activity, and analytics queries. It never touches model calls; without it the tool refuses with `ManagementUnavailable`. |
| OpenRouter base URL | **⚙ Settings → Advanced** | Optional; blank keeps the provider default (`https://openrouter.ai`). Stored in the app database; an invalid value is refused at save and re-validated at startup — never silently coerced. |
| `ETHANG_AGENT_DB` | environment variable | Optional; overrides the database location. This is the one remaining environment variable — the bootstrap chicken-and-egg (the database itself must be locatable before preferences can be read) — not app configuration. |
| Compaction handoff (`compaction_handoff_enabled`, default OFF) | Settings window — **Agents** tab; stored in the app database (`app_preferences`). | When on, reaching the compaction ceiling HANDS OFF instead of compacting in place: the turn ends with a named line and the conversation continues in a NEW session seeded with the summarizer's summary (the old session stays resumable). The composition reads the preference at every compaction, so a save applies from the next one — no restart. |
| Sub-agent settings (`DefaultModel`, `MaxConcurrentAgents`) | Settings window — **Agents** tab; stored in the app database (`app_preferences`). Absent values fall back to shipped defaults (max concurrent 4, no default model). | Invalid values are refused at save and re-validated at startup — configuration is validated strictly, never silently coerced. There is deliberately no child-timeout setting: wall-clock is never a child cancellation source. There is no remote-host toggle: **every** conversation runs in its own out-of-process `eThangAgent.ChildHost` (spec D6/D7), keyed by the conversation id so two conversations in one workspace get two hosts and two budgets. The host survives app restarts — the app re-attaches, reuses a host already serving that conversation, and reconciles running children exactly. |
| Sub-agent watchdog (`TickInterval`, `IdleThreshold`, `MaxWrapUpAttempts`) | Settings window — **Agents** tab; stored in the app database. Durations use constant format (`00:00:02`); every knob is optional and blank keeps the default (60-second tick, 15-minute idle threshold, 1 wrap-up attempt). Invalid text is refused at save and at startup — never clamped, never coerced (bare integers are rejected: without units they would bind as days). The configured knobs govern BOTH watchdogs: the app-side loop and the out-of-process child host's — the child host picks changes up for newly opened agents, while the app-side loop binds them once per process (changes reach it after the app restarts). |
| Session files and skill directories | **Open Workspace** dialog (shown when a workspace is opened), not the Settings window. Global rows load for every workspace; workspace rows for that workspace only. Rows are added through native file/folder pickers (no typed paths), toggled per row, and stored in the app database (`app_preferences`) — the stored lists prefill the next launch, and the confirmed lists apply to the session being opened. |

Saved keys apply to newly opened agents; already-open tabs keep the credentials they were created with.

The active provider is chosen per agent in the Open-Agent dialog — switching providers is deliberately a different experience (its own model catalog, defaults, and tool surface), not a merged model list. The model is chosen per tab through the **Model Settings** window (left menu, visible whenever a tab is open), and the choice applies from the next turn to the root agent and children alike. It is remembered per workspace + provider and restored when the same directory reopens; clearing the choice returns the session to the provider's default model. Reasoning effort works the same way through the window's **Reasoning effort** selector, with **Model default** returning the session to the provider's own behavior.

The picker offers a searchable list of every OpenRouter model (deduped across provider endpoints, shown with effective pricing and context size). Catalog prices are served final: OpenRouter's endpoints API reports the price you actually pay, and its `discount` field is promo metadata rather than a multiplier. The catalog is **persisted in the app database** (scoped by provider and API source, so proxy/mock catalogs cannot overwrite the live catalog; legacy source-less snapshots are ignored) and served **stale-while-revalidate**: reopening the app shows the model picker instantly from the last fetched catalog while a background refresh revalidates it. Every new catalog client revalidates a persisted snapshot even within the 24-hour TTL; warm reads use the TTL, and opening Model Settings joins the refresh and updates the model list without resetting search or selection. A failed refresh keeps cached models visible and shows its error. With no persisted snapshot, the initial load waits for the API.

The chosen model is pinned per session (the persisted `ModelUsed` record is the pin) — the model picker is the way to change models. The status bar always shows the model that will serve the next turn: the pick notice updates it when a choice lands, and a session that already carries a pin (resumed, or past its first turn) shows the pinned id from attach. The harness never substitutes a different model for the one you picked: if the chosen model cannot be served (its catalog context window is unknown), the turn fails with `Error [UnknownModelWindow]` instead of quietly running on another model. A session that has picked nothing names its default model explicitly, so the status bar and the model that actually runs are always the same model. Sub-agent spawns follow the session's model choice (unless the spawn names a model or a configured default applies); with no choice and no configured default, the provider's fallback model serves.

### Skills

Every session's system prompt carries a budgeted skills listing so the model can prefer a
matching skill over improvising and load the full body with `skill_view`. Skills come from
three sources with fixed precedence: built-in > file > learned.

- **Built-in skills** ship with the app: the development-methodology set (debugging,
  grill, TDD, planning, code review, worktrees, sub-agent driving, verification),
  the `using-skills` session contract, the tools-mapping skill, and one commit-guidance skill
  per commit style.
- **File skills** load from directories you configure in the **Open Workspace**
  dialog's **Skill directories** section. Global directories are scanned for every
  workspace; workspace directories for the workspace they belong to. Each entry can be
  toggled on or off, and a workspace directory equal to a global one is scanned once.
  Directories use the
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

- The published folder must carry the conversation host (`eThangAgent.ChildHost.exe`) next to the app: the app always launches the host that ships WITH it, so the two are one build and speak the same wire protocol (the same rule the computer-use broker follows). The Desktop project references the host project, so the exe is always there — never publish the app without it, and never point the app at a host from another build.
- Each sidecar app must publish its own dependency manifest (`eThangAgent.ChildHost.deps.json`, `eThangAgent.ComputerUse.Host.deps.json`): it is what resolves the app's NATIVE assets (SQLite's `e_sqlite3.dll`). A publishing app copies a referenced project's assemblies but NOT that file, so a published host could not open SQLite and every turn failed (2026-10-22). The sidecar projects declare the manifest as publish content, and the host self-checks its deployment at startup so an incomplete deployment is named instead of failing cryptically.

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
