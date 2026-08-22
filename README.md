# eThang Agent

eThang Agent is an AI coding agent for Windows, built on .NET 10 and delivered through a CLI. It pairs a strict Domain-Driven Design core (layered bounded contexts, CQRS, Specifications, Anti-Corruption Layers) with a pragmatic tool surface: it talks to OpenRouter models, runs PowerShell through a dedicated ACL, and persists every session to an app-owned SQLite database so past work can be recalled.

> `AGENTS.md` is the engineering handbook — architecture rules and conventions for working *on* this codebase. This README covers what the agent *is* and how to *use* it.

## What it can do today

- Conversational coding loop against [OpenRouter](https://openrouter.ai/) models
- `exec` tool — PowerShell execution with artifact capture and structured output
- `read` tool — bounded, line-range text file reads
- `write` tool — create/replace files behind an explicit overwrite gate
- `edit` tool — exact literal replacements with occurrence verification
- `search_files` tool — bounded workspace search (literal or regex, glob-filtered)
- Capability registry exposing agent tools plus spawnable sub-agents, durable workspace state, and memory recall
- Nested sub-agents with depth limits and concurrency caps
- Session persistence and recall via a versioned, app-owned SQLite database
- Interactive REPL with line editing and autocomplete, plus a piped mode for scripts and E2E tests

## Requirements

- Windows (path handling, process execution, and the shell assume Windows)
- PowerShell
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
   dotnet run --project src/eThangAgent.CLI
   ```

The agent starts an interactive REPL and works inside whatever workspace directory you launch it from.

## Usage

### Commands

| Command | Description |
| ------- | ----------- |
| `/help` | Show the command list |
| `/exit` | Exit the agent |
| `/quit` | Exit the agent (alias of `/exit`) |

Anything else you type is sent to the model as a message.

### Piped mode

When stdin or stdout is redirected, the agent switches to a line-based REPL: one line in, one response out. Scripts and E2E tests drive the agent this way.

### Configuration

| Setting | Where | Notes |
| ------- | ----- | ----- |
| `OPENROUTER_API_KEY` | environment variable | Required. Get a key at [openrouter.ai/keys](https://openrouter.ai/keys). |
| `OPENROUTER_BASE_URL` | environment variable | Optional; defaults to `https://openrouter.ai`. Useful for pointing tests at a mock server. |
| `ETHANG_AGENT_DB` | environment variable | Optional; overrides the database location. |
| Sub-agent settings (`DefaultModel`, `ChildTimeoutSeconds`, `MaxConcurrentAgents`) | `appsettings.json` (`SubAgent` section) next to the executable, overridden by `SubAgent__*` environment variables | Invalid values abort startup — configuration is validated strictly, never silently coerced. |

The default model is declared at the composition root (`src/eThangAgent.CLI/Program.cs`, currently `stealth/ox-alpha`).

### Where your data lives

- Sessions, state transitions, and events: one SQLite database owned by the app, by default at `%LOCALAPPDATA%\eThangAgent\eThangAgent.db` (override with `ETHANG_AGENT_DB`). Schema changes run through versioned migrations.
- Exec artifacts: `%TEMP%\eThangAgent\exec-artifacts`.

## Development

```powershell
dotnet build   # solution: eThangAgent.slnx
dotnet test    # xUnit — unit, integration, and E2E layers
```

- Every change leaves the build green.
- Unit tests use fakes only — a domain test never knows PowerShell, HTTP, or OpenRouter exist.
- Integration tests exercise real ACL implementations; E2E tests drive the full CLI against a local mock OpenRouter server.
- Read `AGENTS.md` for architecture rules and conventions before writing code.

## Repository layout

```text
src/     One project per bounded context and ACL (see AGENTS.md for the map)
tests/   Mirror-image test projects
docs/    Specs and implementation plans (superpowers workflow)
```

## Roadmap

`grand-plan.md` holds long-range ideas (roadmap stages, a desktop UI, integrations). It is explicitly aspirational — a rough idea, not a guide for current implementation.
