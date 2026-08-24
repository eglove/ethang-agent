# eThang Agent

eThang Agent is an AI coding agent for Windows, built on .NET 10 and delivered through an Avalonia desktop application. It pairs a strict Domain-Driven Design core (layered bounded contexts, CQRS, Specifications, Anti-Corruption Layers) with a pragmatic tool surface: it talks to OpenRouter models, executes model-written C# scripts in-process through a dedicated ACL, and persists every session to an app-owned SQLite database so past work can be recalled.

> `AGENTS.md` is the engineering handbook — architecture rules and conventions for working *on* this codebase. This README covers what the agent *is* and how to *use* it.

## What it can do today

- One Avalonia desktop frontend over a shared host-agnostic core (`eThangAgent.Composition`) — streamed responses with reasoning/tool activity, clarify prompts answered in-place, sub-agent spawning, durable session persistence
- Conversational coding loop against [OpenRouter](https://openrouter.ai/) models
- Desktop startup asks for a working directory through a native folder picker and re-prompts until one is chosen; the picked directory roots path resolution, `exec` scripts' cwd, and curated-memory scoping, and an `AGENTS.md` found at that root is injected verbatim into the system prompt as read
- Live response streaming — assistant text renders as it arrives,
  including interstitial reasoning between tool calls (SSE; falls back transparently when a
  provider endpoint does not stream)
- Transient-failure retries with exponential backoff against OpenRouter (429/408/5xx,
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
- Selectable transcript text in the desktop app — select any message or reasoning block
  and copy it with Ctrl+C
- `exec` tool — in-process C# scripting via Roslyn with artifact capture and structured output
- `read` tool — bounded, line-range text file reads
- `write` tool — create/replace files behind an explicit overwrite gate
- `edit` tool — exact literal replacements with occurrence verification
- `search_files` tool — bounded workspace search (literal or regex, glob-filtered)
- `git_status` / `working_diff` tools — inspect branch state and bounded diffs
- `git_commit` tool — index-only commits with validated conventional or gitmoji messages
- Curated memory loop — `memories.search/add/update/remove` over a categorized, full-text,
  versioned knowledge base, with turn-boundary nudges prompting curation
- Skill subsystem: 14 embedded development-methodology skills (superpowers), session-start bootstrap injection, and `skill_list` / `skill_view` / `skill_manage` tools
- `clarify` tool — structured clarifying questions with numbered options
- `todo` tool — durable workspace task list with compare-and-swap writes
- Capability registry exposing agent tools plus spawnable sub-agents, durable workspace state, and memory recall
- Nested sub-agents with depth limits and concurrency caps
- Session persistence and recall via a versioned, app-owned SQLite database

## Requirements

- Windows (path handling and process execution assume Windows)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An [OpenRouter API key](https://openrouter.ai/keys)

## Getting started

1. Clone the repository.
2. Set your API key:

   ```powershell
   $env:OPENROUTER_API_KEY = "sk-or-..."
   ```

3. Build and run:

```powershell
dotnet build
dotnet run --project src/eThangAgent.Desktop # Avalonia desktop app
```

On startup the app asks for a working directory; the picked directory roots path resolution, `exec` scripts' cwd, and curated-memory scoping.

## Usage

### Configuration

| Setting | Where | Notes |
| ------- | ----- | ----- |
| `OPENROUTER_API_KEY` | environment variable | Required. Get a key at [openrouter.ai/keys](https://openrouter.ai/keys). |
| `OPENROUTER_BASE_URL` | environment variable | Optional; defaults to `https://openrouter.ai`. Useful for pointing tests at a mock server. |
| `ETHANG_AGENT_DB` | environment variable | Optional; overrides the database location. |
| Sub-agent settings (`DefaultModel`, `ChildTimeoutSeconds`, `MaxConcurrentAgents`) | `appsettings.json` (`SubAgent` section) next to the executable, overridden by `SubAgent__*` environment variables | Invalid values abort startup — configuration is validated strictly, never silently coerced. |

The default model is declared at the composition root (`src/eThangAgent.Desktop/DesktopHost.cs`, currently `stealth/ox-alpha`).

### Where your data lives

- Sessions, state transitions, and events: one SQLite database owned by the app, by default at `%LOCALAPPDATA%\eThangAgent\eThangAgent.db` (override with `ETHANG_AGENT_DB`). Schema changes run through versioned migrations.
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
- Integration tests exercise real ACL implementations; E2E tests drive the desktop app headless against a local mock OpenRouter server.
- Read `AGENTS.md` for architecture rules and conventions before writing code.

## Repository layout

```text
src/     One project per bounded context and ACL (see AGENTS.md for the map)
         plus eThangAgent.Composition (shared host-agnostic wiring) and
         eThangAgent.Desktop (Avalonia frontend)
tests/   Mirror-image test projects
docs/    Specs and implementation plans (superpowers workflow)
```

## Roadmap

`grand-plan.md` holds long-range ideas (roadmap stages, a desktop UI, integrations). It is explicitly aspirational — a rough idea, not a guide for current implementation.
