# Desktop UI (Avalonia) with Pluggable Frontends

**Date:** 2026-08-23
**Status:** Draft
**Branch:** (to be created)

## Summary

Give the agent a second frontend: an Avalonia desktop application with strict feature parity with the CLI — nothing more, nothing less. The work restructures hosting so frontends are pluggable:

1. **Shared composition** — extract the host-agnostic DI wiring from `eThangAgent.CLI/Program.cs` into a new `eThangAgent.Composition` project consumed by every frontend. Adding or removing a frontend means adding or removing one host project that calls `AddEThangAgentCore`; nothing else changes.
2. **Unrooted paths** — the desktop has **no workspace concept**. File and git tools accept whatever path the model passes as-is. A new `UnrootedPathResolver` behind an extracted `IPathResolver` seam replaces workspace-containment resolution in the desktop host.
3. **Avalonia host** — new `eThangAgent.Desktop` project: streaming transcript, status bar, command input with autocomplete, interactive clarify — the exact feature surface of today's REPL.

The CLI remains functional and byte-for-byte behavior-identical. Multi-workspace support is explicitly deferred as a future problem.

## Architecture

### Dependency graph after

```text
                 eThangAgent.Composition  (host-agnostic wiring: domains, ACLs, agent loop,
                     ^         ^           capability registry, stores, nudge policy,
          CLI uses   |         |           prompt providers, session lifecycle)
             +-------+         +-------+
             |                         |
      eThangAgent.CLI           eThangAgent.Desktop
   (Terminal.ACL rendering)    (Avalonia rendering)
```

- `Composition` references all domain projects and ACLs. It never references Terminal.ACL, CLI, or Desktop.
- `Desktop` references Composition plus Avalonia packages. It never references CLI or Terminal.ACL.
- Domain projects gain zero new dependencies (Tool.Domain gains only the `IPathResolver` abstraction).

## Work Stream 1: Path Resolution Without a Workspace

### Overview

`WriteTool`, `EditTool`, `SearchTool`, `GitStatusTool`, `WorkingDiffTool`, and `GitCommitTool` depend on concrete `WorkspacePathResolver`, which enforces containment inside a workspace root. Extract the seam; add the unrooted flavor.

### IPathResolver (new, Tool.Domain)

```csharp
public interface IPathResolver
{
    Result<string> Resolve(string path);
}
```

- `WorkspacePathResolver` implements it unchanged (containment enforced) and keeps its class API for existing callers/tests.
- `UnrootedPathResolver` (new): absolute paths pass through verbatim; relative paths resolve against the process working directory; **never rejects** for being outside anything. Malformed paths fail with the same `InvalidPath` error contract as today.

Tools switch constructor parameters from `WorkspacePathResolver` to `IPathResolver`. Error contracts otherwise unchanged.

### Files

| Action | Path |
| ------ | ---- |
| Create | `src/eThangAgent.Tool.Domain/IPathResolver.cs` |
| Modify | `src/eThangAgent.Tool.Domain/WorkspacePathResolver.cs` (implement interface) |
| Create | `src/eThangAgent.Tool.Domain/UnrootedPathResolver.cs` |
| Modify | `src/eThangAgent.Tool.Domain/WriteTool.cs`, `EditTool.cs`, `SearchTool.cs`, `GitStatusTool.cs`, `WorkingDiffTool.cs`, `GitCommitTool.cs` (depend on `IPathResolver`) |
| Modify | `tests/eThangAgent.Tool.Domain.Tests/` (interface-based construction; new `UnrootedPathResolverTests`) |

## Work Stream 2: Shared Composition

### Overview

New project `eThangAgent.Composition` owns every registration that is not presentation:

- OpenRouter ACL (configuration, model provider, provider factory)
- FileSystem ACL, Roslyn ACL, Storage ACL (AppDatabase, state/skill/memory stores)
- Conversation aggregate + repository (`InMemoryConversationRepository` moves here)
- Agent loop (`Ag`), `SendMessageCommandHandler`, nudge policy, memory write counter
- Capability registry (agent tools, sub-agents, state, memory providers)
- System-prompt providers (`SuperpowersBootstrapPromptProvider`, `ExecGuidePromptProvider`, `CuratedMemoryGuidePromptProvider`, static prompt — moved from CLI)
- `RootSessionLifecycle` helper: `AppendExchangeAsync` / `CompleteRootSessionAsync` logic extracted from `Program.cs` so both hosts share identical session persistence semantics

### AgentHostOptions

The host supplies exactly three presentation-scoped decisions:

| Option | CLI binds | Desktop binds |
| ------ | --------- | ------------- |
| `ClarifyChannel` | `PipedClarifyChannel` when stdin redirected, else `InteractiveClarifyChannel(AnsiTerminal)` | `AvaloniaClarifyChannel` |
| `WorkspaceContext` | `CwdWorkspaceContext` | `FixedWorkspaceContext("app")` (memory scoping only; temporary until multi-workspace) |
| `PathResolver` | `WorkspacePathResolver(CWD)` | `UnrootedPathResolver` |

Bound configuration values (`SubAgentOptions`, `MaxToolIterations`, `OpenRouterConfiguration`) are parameters of `AddEThangAgentCore`. A shared `AgentConfiguration.Load()` helper (env vars + optional `appsettings.json`, strict validation, abort-with-reason on invalid) moves into Composition so both hosts read config identically.

Default model stays `stealth/ox-alpha`, declared by each host (parity).

### Files

| Action | Path |
| ------ | ---- |
| Create | `src/eThangAgent.Composition/eThangAgent.Composition.csproj` |
| Create | `src/eThangAgent.Composition/AgentComposition.cs` (`AddEThangAgentCore`) |
| Create | `src/eThangAgent.Composition/AgentHostOptions.cs` |
| Create | `src/eThangAgent.Composition/AgentConfiguration.cs` |
| Create | `src/eThangAgent.Composition/FixedWorkspaceContext.cs` |
| Create | `src/eThangAgent.Composition/RootSessionLifecycle.cs` |
| Move | `SuperpowersBootstrapPromptProvider.cs`, `ExecGuidePromptProvider.cs`, `InMemoryConversationRepository.cs` from CLI |
| Modify | `src/eThangAgent.CLI/Program.cs` (thin: load config → AddEThangAgentCore → terminal bits → REPL loops) |
| Modify | `eThangAgent.slnx` |
| Create | `tests/eThangAgent.Composition.Tests/` |

## Work Stream 3: Avalonia Desktop Host

### Overview

New project `eThangAgent.Desktop`: .NET 10, Avalonia 11.x (latest stable), FluentTheme (dark), MVVM via CommunityToolkit.Mvvm.

### Parity mapping

| CLI surface | Desktop equivalent |
| ----------- | ------------------- |
| Transcript pane (user ›, streamed text, reasoning blocks, tool call w/ args, tool result, errors/notices) | Scrolling transcript list; one DataTemplate per entry type; auto-scroll |
| Stream callbacks → ConcurrentQueue drained ~12 fps frame loop | Same callbacks → unbounded `Channel<UiStreamEvent>` → consumer task marshals onto UI thread via Dispatcher (event-driven, no polling timer) |
| Line editor modal during turn (input blocked) | Input TextBox disabled while a turn is in flight |
| PrefixAutoCompleter over `/commands` | Autocomplete popup on leading `/`; Enter accepts, Esc dismisses |
| `/help`, `/exit`, `/quit` | Typed commands: `/help` prints command list into transcript; `/exit`, `/quit`, and window close all exit gracefully |
| StatusLine (model id, message count, spinner phase Thinking/Streaming/Ready) | Status bar with identical fields, animated phase indicator |
| InteractiveClarifyChannel (question + numbered options on input row, Ctrl+C cancels) | Input area swaps to clarify mode: question text, numbered option buttons, free-text field, Cancel button (same cancellation Result) |
| Piped mode | None — desktop is inherently interactive; E2E coverage continues through the CLI piped suite |

### Key components

- `Program.cs` — AppBuilder entry; startup failures (missing `OPENROUTER_API_KEY`, failed root-session persistence, invalid config) surface as an error dialog, then exit non-zero.
- `MainViewModel` — owns turn orchestration: builds `SendMessageCommand`, subscribes stream callbacks into the channel, disables input, restores state on completion; appends exchange via `RootSessionLifecycle`; persistence errors become transcript entries (session continues).
- `TranscriptEntry` hierarchy + templates — user message, assistant text (streamed append), reasoning block, tool call (name + arguments), tool result (name + summary), error/notice.
- `AvaloniaClarifyChannel` — `AskAsync` runs on the agent thread; marshals onto UI thread, shows clarify mode, completes a `TaskCompletionSource<Result<string>>` on answer/cancel. Cancel yields the same `Cancelled` error as Ctrl+C.
- Graceful shutdown — window close runs the same completion path as `/exit` before teardown.

### Files

| Action | Path |
| ------ | ---- |
| Create | `src/eThangAgent.Desktop/eThangAgent.Desktop.csproj` |
| Create | `src/eThangAgent.Desktop/Program.cs`, `App.axaml(.cs)` |
| Create | `src/eThangAgent.Desktop/Views/MainWindow.axaml(.cs)` |
| Create | `src/eThangAgent.Desktop/ViewModels/MainViewModel.cs`, `TranscriptViewModel.cs`, `ClarifyViewModel.cs`, `StatusViewModel.cs` |
| Create | `src/eThangAgent.Desktop/Streaming/UiStreamEvent.cs`, `StreamBridge.cs` |
| Create | `src/eThangAgent.Desktop/AvaloniaClarifyChannel.cs` |
| Create | `tests/eThangAgent.Desktop.Tests/` |
| Modify | `eThangAgent.slnx`, `README.md` |

## Cross-Cutting Concerns

### Thread safety

All mutations of observable collections happen on the UI thread through the bridge consumer or Dispatcher posts. Agent-side callbacks only write to the channel. The clarify TCS is completed exactly once (answer xor cancel).

### Memory

The stream channel is unbounded but lives only for the duration of a turn; the consumer drains continuously, matching the CLI queue-drain pattern. No transcript retention beyond what is rendered plus persisted messages.

### Frontend reversibility

Nothing in any domain knows Avalonia exists. A future third frontend (or removal of either current one) is a host-project change plus one `AgentHostOptions` binding.

## Testing

| Layer | Coverage |
| ----- | -------- |
| Unit (fakes only, no Avalonia types) | ViewModel behaviors: command routing (/help, /exit, unknown passthrough), clarify state machine (options, free text, cancel, double-complete guard), stream-bridge event ordering (deltas, reasoning, iteration boundaries, tool call/result pairs), input-disabled-during-turn, exchange bookkeeping on success/failure |
| Integration | `Composition.Tests`: provider built from `AddEThangAgentCore` (fake clarify channel) resolves every registered service — permanent wiring-drift guard for both hosts. `UnrootedPathResolver`: absolute passthrough, relative-to-CWD, malformed rejection |
| Headless UI | `Avalonia.Headless.XUnit`: send-message flow against stub handler, autocomplete popup show/accept/dismiss, clarify interaction end-to-end in the visual tree |
| E2E | Existing piped-mode CLI suite remains the contract gate, untouched. One headless-desktop smoke test drives a real turn through the real pipeline against the existing mock OpenRouter server. If headless hosting proves disproportionate, fall back to VM-level integration and say so explicitly in the implementation plan |

## Acceptance Criteria

1. `dotnet build` succeeds; `dotnet test` passes all test projects.
2. Every README capability bullet is exercisable through the desktop UI: streamed assistant text, reasoning tokens, tool calls/results visible; all registered tools callable by the model; clarify answerable and cancelable; sub-agents spawnable; sessions persisted, appended per turn, and Completed on exit.
3. CLI behavior unchanged: existing CLI tests pass without modification to their assertions.
4. No domain project references Avalonia; `eThangAgent.Desktop` references neither `eThangAgent.CLI` nor `eThangAgent.Terminal.ACL`.
5. README documents the desktop app and how to run it.
6. Missing API key, invalid config, or failed root-session persistence produce a visible dialog and non-zero exit — never a silent start.

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
| ---- | ---------- | ------ | ---------- |
| Avalonia 11.x compatibility friction on .NET 10 | Medium | Medium | Verify package restore + headless smoke in the first plan task; pin exact versions |
| Clarify deadlock (UI waits on agent thread, agent waits on UI) | Medium | High | Channel/TCS pattern: agent thread never blocks on UI; TCS completed from UI thread only; dedicated unit + headless tests |
| Cross-thread ObservableCollection corruption | Medium | High | Single rule — UI-thread-only mutation via bridge consumer/Dispatcher; enforced in review and covered by headless tests |
| Wiring drift between hosts after extraction | Low | High | Composition integration guard test resolves the full graph for both option sets |
| Relative-path ambiguity without a workspace root confuses the model | Medium | Low | Documented in this spec: absolute paths expected; resolver accepts relatives against process CWD; multi-workspace design will replace this |
| Headless E2E flakiness | Medium | Low | Fallback documented: VM-level integration retains the behavioral gate |
