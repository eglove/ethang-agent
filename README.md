# eThang Agent

eThang Agent is an AI coding agent for Windows, built on .NET 10 and delivered through an Avalonia desktop application. It pairs a strict Domain-Driven Design core (layered bounded contexts, CQRS, Specifications, Anti-Corruption Layers) with a pragmatic tool surface: it talks to [OpenRouter](https://openrouter.ai/) and [z.ai](https://z.ai/) models behind provider-neutral contracts, executes model-written C# scripts in-process through a dedicated ACL, and persists every session to an app-owned SQLite database so past work can be recalled.

> `AGENTS.md` is the engineering handbook — architecture rules and conventions for working *on* this codebase. This README covers what the agent *is* and how to *use* it.

## What it can do today

- One Avalonia desktop frontend over a shared host-agnostic core (`eThangAgent.Composition`) — streamed responses with reasoning/tool activity, clarify prompts answered in-place, sub-agent spawning, durable session persistence
- Conversational coding loop against [OpenRouter](https://openrouter.ai/) or [z.ai](https://z.ai/) GLM models — each agent tab is wired for exactly one provider for its lifetime
- Desktop shell opens on a main window with a left-hand menu bar; **Open Agent** opens a dialog with an AI-provider dropdown (the providers whose API keys are configured) and a **Choose Workspace** folder picker. The opened tab is bound to that provider until closed (the status bar shows it), the same directory may be open under both providers, and the choice is remembered in the app database so the dialog pre-selects it next time. Each workspace roots path resolution, `exec` scripts' `Workspace`, and curated-memory scoping, and an `AGENTS.md` found at that root is injected verbatim into the system prompt as read
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
  point (never splitting a tool call from its results). `/stop` (or the Stop button) hard-cancels
  the active turn and all of this session's sub-agents; half-finished tool batches are repaired
  in place so conversation history stays valid, and the interruption surfaces as
  `Error [TurnCancelled]` / child `interrupted` outcomes rather than crashes or lost state
- Selectable transcript text in the desktop app — select any message or reasoning block
  and copy it with Ctrl+C
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
- `git_status` / `working_diff` tools — inspect branch state and bounded diffs
- `git_commit` tool — index-only commits with validated conventional or gitmoji messages
- Curated memory loop — `memories.search/add/update/remove` over a categorized, full-text,
  versioned knowledge base, with turn-boundary nudges prompting curation
- Skill subsystem: 15 embedded development-methodology skills, session-start bootstrap injection, and `skill_list` / `skill_view` / `skill_manage` tools
- `clarify` tool — structured clarifying questions with numbered options
- z.ai capability tools (available only on z.ai tabs): `web_search` — live web search with
  bounded snippets; `web_read` — fetch one page as markdown; `count_tokens` — GLM tokenizer;
  `generate_image` — GLM-Image saved into the workspace as a PNG; `ocr_document` — GLM-OCR
  transcription of workspace PDFs/images; `transcribe_audio` — GLM-ASR transcription of
  short audio clips
- `/effort <level>` — set the session's reasoning effort for z.ai (max, xhigh, high, medium,
  low, minimal, none); bare `/effort` shows the current level. Applies from the next turn to
  the root agent and children alike; on OpenRouter tabs the choice is remembered but inert
- `/model <glm-5.3|glm-5.3-flash>` — pick the session's z.ai model yourself; bare `/model`
  shows the current model. Applies from the next turn to the root agent and children alike.
  z.ai tabs run no automatic model selection — you choose (default `glm-5.3-flash`); on
  OpenRouter tabs the command is unavailable because the model is selected automatically
- `todo` tool — durable workspace task list with compare-and-swap writes
- Capability registry exposing agent tools plus spawnable sub-agents, durable workspace state, and memory recall
- Nested sub-agents with depth limits and concurrency caps
- Session persistence and recall via a versioned, app-owned SQLite database

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

3. Add an API key: click **⚙ Settings** at the bottom of the left menu and paste your [OpenRouter](https://openrouter.ai/keys) and/or z.ai key. Keys are stored DPAPI-encrypted in the app database (only your Windows user can read them back) and apply to newly opened agents.

The window opens directly on the shell: no workspace and no pre-configured key are required up front. Click **Open Agent**, pick a directory, and that agent's chat opens as a tab; repeat to work with several workspaces side by side.

## Usage

### Configuration

| Setting | Where | Notes |
| ------- | ----- | ----- |
| OpenRouter API key | **⚙ Settings → API Keys** | DPAPI-encrypted in the app database. Providers without a key are not offered in the Open-Agent dialog. |
| z.ai API key | **⚙ Settings → API Keys** | Same storage and rules. Leave a field blank to remove that key. |
| `OPENROUTER_BASE_URL` | environment variable | Optional; defaults to `https://openrouter.ai`. Useful for pointing tests at a mock server. |
| `ZAI_BASE_URL` | environment variable | Optional; defaults to `https://api.z.ai/api`. Also for tests. |
| `ETHANG_AGENT_DB` | environment variable | Optional; overrides the database location. |
| Sub-agent settings (`DefaultModel`, `ChildTimeoutSeconds`, `MaxConcurrentAgents`) | `appsettings.json` (`SubAgent` section) next to the executable, overridden by `SubAgent__*` environment variables | Invalid values abort startup — configuration is validated strictly, never silently coerced. |

Saved keys apply to newly opened agents; already-open tabs keep the credentials they were created with.

The active provider is chosen per agent in the Open-Agent dialog — switching providers is deliberately a different experience (its own model catalog, defaults, and tool surface), not a merged model list. Model choice works differently per provider:

- **z.ai** — no automatic selection. The session runs `glm-5.3-flash` (the catalog's fast default) and you switch with `/model <glm-5.3|glm-5.3-flash>`; the choice applies from the next turn to the root agent and children alike. The static catalog (z.ai exposes no models-listing endpoint) is the selectable lineup, and a typed `/model` outranks an explicit `Model:Id` pin.
- **OpenRouter** — automatic. When no model is explicitly configured via `Model:Id` (env var `MODEL__ID`), the agent defers model selection to the first user prompt: a two-stage LLM pipeline categorizes that prompt and selects the best model from OpenRouter's fetched catalog based on the task category and price. The pipeline re-runs on every 10th user message thereafter so the model tracks the conversation's evolving task. Sub-agent spawns similarly select models based on their task prompts. Selection failures fall back to the default model (`openrouter/auto`) and surface as a transcript notice. Explicit configuration always takes precedence and skips selection entirely.

### Where your data lives

- Sessions, state transitions, and events: one SQLite database owned by the app, by default at `%LOCALAPPDATA%\eThangAgent\eThangAgent.db` (override with `ETHANG_AGENT_DB`). Schema changes run through versioned migrations.
- API keys: the same database (Settings → API Keys), DPAPI-encrypted with current-user scope — plaintext keys never touch disk.
- Exec artifacts: `%TEMP%\eThangAgent\exec-artifacts`.

## Development

```powershell
dotnet build   # solution: eThangAgent.slnx
dotnet test    # xUnit — unit, integration, and E2E layers
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
