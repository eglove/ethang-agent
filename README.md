# eThang Agent

eThang Agent is an AI agent harness for Windows, built on .NET 10 and delivered through an Avalonia desktop application. The harness is the scaffolding an AI model acts through — agent loop, tool dispatch, session persistence, desktop UI — while the model supplies the decisions. It pairs a strict Domain-Driven Design core (layered bounded contexts, CQRS, Specifications, Anti-Corruption Layers) with a pragmatic tool surface: it talks to [OpenRouter](https://openrouter.ai/) and [z.ai](https://z.ai/) models behind provider-neutral contracts, executes model-written C# scripts in-process through a dedicated ACL, and persists every session to an app-owned SQLite database so past work can be recalled.

> `AGENTS.md` is the engineering handbook — architecture rules and conventions for working *on* this codebase. This README covers what the harness *is* and how to *use* it.

## What it can do today

- One Avalonia desktop frontend over a shared host-agnostic core (`eThangAgent.Composition`) — streamed responses with reasoning/tool activity, clarify prompts answered in-place, sub-agent spawning, durable session persistence
- Conversational coding loop against [OpenRouter](https://openrouter.ai/) or [z.ai](https://z.ai/) GLM models — each agent tab is wired for exactly one provider for its lifetime
- Desktop shell opens on a main window with a left-hand menu bar; **Open Workspace** opens a dialog with an AI-provider dropdown (the providers whose API keys are configured) and a **Choose Workspace** folder picker. The opened tab is bound to that provider until closed (the status bar shows it), the same directory may be open under both providers, and the choice is remembered in the app database so the dialog pre-selects it next time. Each workspace roots path resolution, `exec` scripts' `Workspace`, and curated-memory scoping, and an `AGENTS.md` found at that root is injected verbatim into the system prompt as read
- Live response streaming — assistant text renders as it arrives,
  including interstitial reasoning between tool calls (SSE; falls back transparently when a
  provider endpoint does not stream)
- Transient-failure retries with exponential backoff against both providers (429/408/5xx,
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
 - Rich transcript rendering — assistant messages render as markdown once a block finishes streaming
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
- `edit` tool — exact literal replacements with occurrence verification
- `write_markdown` tool — renders a structured JSON document into well-formed markdown deterministically (headers, lists, tables, alerts, frontmatter); returns the string or writes it to a workspace file behind the same overwrite gate as `write`
- `search_files` tool — bounded workspace search (literal or regex, glob-filtered)
- `db_schema` / `db_query` tools — read-only inspection of the agent's own app database:
  `db_schema` lists tables, columns, and indexes (row counts opt-in); `db_query` runs one
  SELECT/WITH statement on a read-only connection with a bounded row cap — writes, multiple
  statements, and ATTACH/DETACH are rejected
- `git_status` / `working_diff` tools — inspect branch state and bounded diffs
- `git_commit` tool — validated commits of the current index in the user's chosen style
  (Conventional, Gitmoji, or plain — a host setting, resolved live per commit; never a model parameter),
  with an optional `files` array of workspace-relative paths to stage first (relative-only: no drives, no `..`, no `.`)
- `web_fetch` tool — fetch a web page or resource over HTTP(S) and return readable text:
  HTML pages are converted to markdown (headings, links with absolute URLs, lists, tables,
  fenced code); other textual responses (plain text, JSON, XML) pass through verbatim; binary
  responses are rejected. Redirects are followed and the output's first line always annotates
  the final URL, status, content type, and size
- Curated memory loop — `memories.search/add/update/remove/purge` over a categorized, full-text,
  versioned knowledge base, with turn-boundary nudges prompting curation
- Skill subsystem: 18 embedded skills (development methodology plus per-style commit guidance),
  session-start bootstrap injection, and `skill_list` / `skill_view` / `skill_manage` tools
- `clarify` tool — structured clarifying questions with numbered options
- z.ai capability tools (available only on z.ai tabs in the **General API** endpoint
  mode — the capability endpoints do not exist on the coding endpoint): `web_search` — live web search with
  bounded snippets; `web_read` — fetch one page as markdown; `count_tokens` — GLM tokenizer;
  `generate_image` — GLM-Image saved into the workspace as a PNG; `ocr_document` — GLM-OCR
  transcription of workspace PDFs/images; `transcribe_audio` — GLM-ASR transcription of
  short audio clips
- **Effort** entry (left menu, visible whenever a tab is open) — pick the session's
  reasoning effort: model default, or max, extra high, high, medium, low, minimal, none.
  Applies from the next turn to the root agent and children alike, on both OpenRouter and
  z.ai tabs (OpenRouter maps the level to what the chosen model supports). The choice is
  remembered per workspace + provider and restored when the same directory reopens
- **Context accounting + auto-compaction** — the status bar shows a live `CTX 148.2K/1M, 15%`
  readout (hover for the estimated system-prompt/messages/tools breakdown), plus the session id (first
  8 characters, full id on hover, click ⧉ to copy). The transcript auto-scrolls only while you rest
  at the bottom: your own messages never steal the scroll, scrolling up pauses the follow-the-tail
  behavior until you return to the bottom (or press End), and the reading position survives tab
  switches. Both providers
  report per-request token usage; when utilization crosses 80% at a turn boundary the oldest
  conversation is summarized by a compaction model (per-workspace setting under Settings —
  default: cheapest capable) and replaced by that handoff summary, so long sessions keep
  going without hitting the window. Compacted sessions persist and resume like any other
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
- Session persistence, recall, and resume via a versioned, app-owned SQLite database

## Requirements

- Windows (path handling and process execution assume Windows)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- At least one provider API key: [OpenRouter](https://openrouter.ai/keys) and/or [z.ai](https://docs.z.ai)

## Getting started

1. Clone the repository.
2. Build and run:

```powershell
dotnet build
dotnet run --project src/eThangAgent.Desktop # Avalonia desktop app
```

3. Add an API key: click **⚙ Settings** at the bottom of the left menu and paste your [OpenRouter](https://openrouter.ai/keys) and/or z.ai key. Keys are stored DPAPI-encrypted in the app database (only your Windows user can read them back) and apply to newly opened agents. For z.ai, also pick the endpoint next to the key field: a **GLM Coding Plan** key works only with **Coding plan (subscription)** (the default, hitting `https://api.z.ai/api/coding/paas/v4`), while a pay-as-you-go API key requires **General API** — a coding-plan key against the general endpoint is rejected as rate-limited.

The window opens directly on the shell: no workspace and no pre-configured key are required up front. Click **Open Workspace**, pick a directory, and that agent's chat opens as a tab; repeat to work with several workspaces side by side.

## Usage

### Configuration

| Setting | Where | Notes |
| ------- | ----- | ----- |
| OpenRouter API key | **⚙ Settings → API Keys** | DPAPI-encrypted in the app database. Providers without a key are not offered in the Open-Agent dialog. |
| z.ai API key | **⚙ Settings → API Keys** | Same storage and rules. Leave a field blank to remove that key. |
| z.ai endpoint mode | **⚙ Settings → z.ai endpoint** | `Coding plan (subscription)` (default) chats through `https://api.z.ai/api/coding/paas/v4`; `General API (pay-as-you-go)` through `https://api.z.ai/api/paas/v4` and is the only mode with the z.ai capability tools. Stored in the app database; applies to newly opened agents. |
| `OPENROUTER_BASE_URL` | environment variable | Optional; defaults to `https://openrouter.ai`. Useful for pointing tests at a mock server. |
| `ZAI_BASE_URL` | environment variable | Optional; defaults to `https://api.z.ai/api` — the root both z.ai endpoint modes hang off. Also for tests. |
| `ZAI_ENDPOINT_MODE` | environment variable | Optional; `coding` (default) or `general`. A stored Settings choice wins over it. Any other value aborts startup. |
| `ETHANG_AGENT_DB` | environment variable | Optional; overrides the database location. |
| Sub-agent settings (`DefaultModel`, `ChildTimeoutSeconds`, `MaxConcurrentAgents`) | `appsettings.json` (`SubAgent` section) next to the executable, overridden by `SubAgent__*` environment variables | Invalid values abort startup — configuration is validated strictly, never silently coerced. |

Saved keys apply to newly opened agents; already-open tabs keep the credentials they were created with. The same applies to the z.ai endpoint mode.

The active provider is chosen per agent in the Open-Agent dialog — switching providers is deliberately a different experience (its own model catalog, defaults, and tool surface), not a merged model list. The model is chosen per tab through the **Model** entry in the left menu (visible whenever a tab is open), and the choice applies from the next turn to the root agent and children alike. It is remembered per workspace + provider and restored when the same directory reopens; picking **Auto** again returns the session to automatic resolution. Reasoning effort works the same way through the **Effort** entry, with **Model default** returning the session to the provider's own behavior.

- **OpenRouter** — the picker offers **Auto (smart selection)** plus a searchable list of every OpenRouter model (deduped across provider endpoints, shown with effective pricing and context size). Auto is the default: the agent defers model selection to the first user prompt, where a two-stage LLM pipeline categorizes that prompt and selects the best model from OpenRouter's fetched catalog based on the task category and price. The pipeline re-runs on every 10th user message thereafter so the model tracks the conversation's evolving task. Sub-agent spawns similarly select models based on their task prompts. Selection failures fall back to the default model (`openrouter/auto`) and surface as a transcript notice.
- **z.ai** — no automatic selection. The picker lists z.ai's static lineup (`glm-5.3`, `glm-5.3-flash` — z.ai exposes no models-listing endpoint); the session runs `glm-5.3-flash` until you pick one.

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
- Integration tests exercise real ACL implementations; E2E tests drive the desktop app headless against a local mock provider server (OpenRouter- and z.ai-shaped).
- Read `AGENTS.md` for architecture rules and conventions before writing code.

## Repository layout

```text
src/     One project per bounded context and ACL (see AGENTS.md for the map)
         plus eThangAgent.Composition (shared host-agnostic wiring) and
         eThangAgent.Desktop (Avalonia frontend)
tests/   Mirror-image test projects
docs/    Project documentation only; no workflow artifacts
        (specs and implementation plans live in workspace state — keys specs/* and plans/* — not repo files)
```

## Roadmap

`grand-plan.md` holds long-range ideas (roadmap stages, a desktop UI, integrations). It is explicitly aspirational — a rough idea, not a guide for current implementation.
