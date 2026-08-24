# Remove the CLI — Desktop becomes the only frontend

**Date:** 2026-08-24
**Status:** Approved (design conversation, this document records it)

## Problem / Goal

eThang Agent ships two frontends over one shared core (`eThangAgent.Composition`): a
terminal CLI and an Avalonia desktop app. The desktop app has reached feature parity
and beyond (workspace folder-picker startup, streamed transcript with reasoning and
tool activity, clarify-in-place, off-UI-thread turns, error dialogs). The project's
stated direction is "a persistent desktop application that manages its own world."
Keeping a second full frontend doubles wiring surface, test surface, and doc surface
for no remaining purpose.

**Goal:** delete the CLI entirely; the desktop app becomes the only UI.

## Decisions made during brainstorming

1. **Terminal.ACL is deleted too.** Its only consumers are `eThangAgent.CLI` and its
   test project. A seam nothing consumes is dead weight; git history preserves it if a
   TUI frontend ever returns.
2. **E2E scenarios are ported into `Desktop.Tests` headless**, not dropped: real
   composition → real OpenRouter client → local mock provider server → `MainViewModel`,
   asserting on transcript entries and captured request bodies.
3. **Single-change sequencing (port-then-delete).** Port first, then delete, one green
   build at every step; no intermediate shared E2E harness project (YAGNI with exactly
   one frontend).

## Current state (verified)

| Piece | Role | Fate |
| ----- | ---- | ---- |
| `src/eThangAgent.CLI` | Exe: interactive TUI + piped line-REPL; owns `CliCommands`, `CwdWorkspaceContext`, `PipedClarifyChannel`, `InteractiveClarifyChannel`; old root-session bootstrap in `Program.cs` | Delete |
| `src/eThangAgent.Terminal.ACL` | ANSI terminal, line editor, transcript pane, statusline, TUI layout | Delete |
| `tests/eThangAgent.CLI.Tests` | 13-scenario piped E2E suite driving compiled `CLI.exe` over stdin/stdout + relocated-candidate unit tests | Delete after porting |
| `tests/eThangAgent.Terminal.ACL.Tests` | Terminal.ACL coverage | Delete |
| `src/eThangAgent.Desktop` | Avalonia WinExe through Composition; `DesktopHost` already covers config load, DI build, root-session persistence, workspace startup | Survives unchanged |
| `tests/eThangAgent.Desktop.Tests` | Headless Avalonia tests incl. `DesktopPipelineSmokeTests` (real core → mock server → VM) and its own full-feature `MockOpenRouterServer` twin | Gains ported E2Es |

No domain, ACL, or Composition code references either deleted project. The desktop's
`MainViewModel` exposes `SubmitAsync` / `WaitForTurnAsync`, making ported E2Es fully
deterministic without process piping or stdout scraping.

## Design

### 1 · Deletions

- `src/eThangAgent.CLI`, `src/eThangAgent.Terminal.ACL`
- `tests/eThangAgent.CLI.Tests`, `tests/eThangAgent.Terminal.ACL.Tests`
- Their four entries removed from `eThangAgent.slnx`

### 2 · Test migration

**Ported into `tests/eThangAgent.Desktop.Tests/E2ETests.cs`** — each scenario rebuilt as:
build services via `AddEThangAgentCore` pointed at the mock server → construct
`MainViewModel` wired to the real handler → `SubmitAsync(...)` → `WaitForTurnAsync()` →
assert on `vm.Transcript.Entries`, `mock.LastChatRequestBody` / `RequestBodies`, and
stream events:

1. Happy-path response renders streamed assistant text (exists as smoke test; absorbed)
2. Default model id reaches the provider request body
3. Skills bootstrap injected exactly once per session into the system message
4. State discipline loop certifies with passing evidence
5. State discipline violated on failing evidence (visible error surfaces)
6. Todo-tool writes flow; model state writes to the todo namespace are rejected
7. Nested spawn: child runs and reports (parent/child scripted via `ReturnsForModel`
   and `{{child_id}}` substitution)
8. Memory recall against the mock server
9. `exec` executes end-to-end
10. `exec` parse error feeds back; corrected program succeeds
11. Exec guide present in system prompt
12. Exposed tool surface contains only expected tools

**Dies with the CLI:** `/help`-lists-commands-and-quit-exits — slash commands are a
CLI-only surface with no desktop counterpart by design.

**Relocations:**
- `SkillsBootstrapTests` (pure Composition contracts) → `Composition.Tests`
- `MockOpenRouterServerTests` → stay beside the surviving mock server in
  `Desktop.Tests` (the two identical mock-server copies collapse into one)
- `PipedClarifyChannelTests` deleted — piped clarify was CLI-only

**New fixture:** a headless-agent fixture (shared helper) building services + VM per
test against the mock server, with a temp `ETHANG_AGENT_DB` per fixture instance.
Tests sharing the env-var run in an xUnit collection so parallel classes cannot race
the environment variable.

### 3 · Documentation

**AGENTS.md**
- Interface line: CLI → Desktop (Avalonia)
- ACL table: Terminal.ACL row removed
- Testing conventions: E2E wording becomes "headless desktop host driving the real
  composition against a local mock provider server"
- Stray CLI mentions cleaned everywhere

**README.md**
- Intro and feature list: drop REPL/piped-mode bullets; single-frontend framing
- Getting started: only `dotnet run --project src/eThangAgent.Desktop`
- Commands table and Piped-mode section removed
- Configuration notes that reference the CLI executable updated
- Publish command targets Desktop
- Repository layout and development sections updated

### 4 · Verification

- `dotnet build` green; full `dotnet test` green
- Repo-wide search proves zero lingering references to `eThangAgent.CLI` or
  `Terminal.ACL` in source, docs, and solution file

## Out of scope

- Any change to domains, ACLs, or Composition internals
- New desktop features; anything the CLI had that the desktop lacks (none found)
- A shared multi-frontend E2E harness project (revisit if a second frontend returns)

## Risks / mitigations

- **Coverage dip from losing process-level E2E** — mitigated by porting all 12
  still-meaningful scenarios through the real composition and provider client; the
  lost assertions are pipe/prompt mechanics that no longer exist.
- **Env-var races in ported tests** — mitigated by xUnit collection isolation around
  the `ETHANG_AGENT_DB` fixture.