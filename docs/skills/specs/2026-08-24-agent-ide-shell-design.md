# Agent IDE Shell — Design

**Date:** 2026-08-24
**Status:** Approved design (brainstormed with user; sections 1–3 approved individually)
**Scope:** Desktop frontend restructure — from single-agent window to a multi-agent IDE shell.

## 1. Problem & Goal

Today the desktop app is a single-agent experience: startup forces a folder-picker
*before any window exists*, then one `MainWindow` *is* the app. The goal is the first
step toward a central agent **IDE**: a persistent main window with a left-hand sidebar
whose "Open Agent" action picks a workspace directory and opens that agent as a tab.
Multiple agents can be open and running at once.

### Decisions locked with the user

| Decision | Choice |
| --- | --- |
| Launch behavior | **A** — Empty shell on launch; no forced picker; zero open tabs is a valid steady state. |
| Concurrency | **A** — Fully concurrent: every tab streams independently, including background tabs. |
| Duplicate workspaces | **A** — Picking an already-open workspace focuses its existing tab (one agent per directory). |

## 2. Architecture Overview

The application becomes a **shell + sessions** structure:

```
ShellWindow (the only top-level window)
├── Sidebar (~170px): "Open Agent" button pinned at top
└── TabControl (agent session per tab; header = folder name, tooltip = full path)
    ├── [empty state when 0 tabs: hint text + "Open Agent" button]
    └── AgentSessionView (per-tab UserControl = today's MainWindow content)
        └── transcript / input / status bar / clarify UI (carried over unchanged)
```

- `ShellViewModel` owns the tab collection and the Open-Agent flow.
- Each tab wraps a per-agent view-model (today's `MainViewModel`, renamed conceptually
  to the per-session VM) inside an `AgentSessionView` UserControl.
- The old `WorkspaceStartupFlow`, the "working directory required" dialog, and the
  shutdown-deferral machinery (`DeferShutdownDuringStartup` / `EnableWindowCloseShutdown`)
  are deleted — their reason to exist disappears with the forced-picker gate.

### Startup (new)

`App.OnFrameworkInitializationCompleted` builds `ShellViewModel` + `ShellWindow`
directly on the UI thread and shows it. No background bootstrap at launch; no windows
before the shell exists. The folder picker opens modally over the shell.

Default `ShutdownMode.OnLastWindowClose` is correct again: closing the shell exits.

## 3. Open-Agent Flow

Implemented in `ShellViewModel`; picker, dialogs, and bootstrap are injected delegates,
keeping the flow unit-testable headless:

1. Folder picker over the shell window → **cancel = silent no-op**.
2. Full-path duplicate check (normalize via `Path.GetFullPath`) → already open?
   **Focus that tab**, done.
3. Validate config/API key → failure = error dialog, no tab created.
4. Insert placeholder tab ("starting…") immediately, select it, run bootstrap
   **off the UI thread** (per-tab DI container + root-session persistence).
5. Success → swap in the real session view, keep the tab selected.
6. Bootstrap failure → remove the placeholder, surface error dialog. No zombie tabs.

Duplicate detection compares full paths case-insensitively (Windows paths).

## 4. Per-Tab Composition & Concurrency

Each open agent gets its own everything-except-the-database:

- Own DI container built by the existing `AddEThangAgentCore(settings, apiKey, model, host)`
  with *its* `FixedWorkspaceContext(workspacePath)` / `WorkspacePathResolver(workspacePath)`
  / `WorkspaceInstructionsPromptProvider(workspacePath)`.
- Own `AvaloniaClarifyChannel` whose presenter targets **that tab's** view-model.
- Own `RootSessionLifecycle` + root-session persistence, exactly today's mechanics.
- Config (`AgentConfiguration`) loads **once at shell level**; each tab-open reuses it.
- **Shared:** one process-wide `AppDatabase` instance, injected into every per-tab
  container. `AddEThangAgentCore` gains an optional parameter to accept an existing
  database instead of always creating its own.

### The exec cwd fix (concurrency enabler)

`CSharpScriptExecEngine` currently captures `Environment.CurrentDirectory` into
`ScriptGlobals.Workspace` at execution time. That ambient dependency dies:

- Mechanism (pinned): the exec ACL receives its workspace root **through constructor
  injection from per-tab composition** — a small read-only workspace-root accessor
  (exact type name at implementation discretion) supplied alongside the existing
  `ExecOptions` at the `AgentComposition` registration site. Each tab's container
  therefore hands its own engine its own root. The domain passes the workspace
  explicitly; the ACL never reads ambient process state.
- `DesktopHost.PrepareAsync` stops setting `Environment.CurrentDirectory`.

Verified during brainstorming: external child processes are already workspace-anchored
(`ScriptGlobals.Shell` sets `WorkingDirectory = Workspace`; git access anchors to repo
path), so this single seam change fully removes cross-agent interference.

### Turns & threads (unchanged mechanics)

Turns run off the UI thread via the existing `OffUiThread` wrapper (execution-context
suppression included); stream events marshal back through the dispatcher sink. Multiple
tabs turning simultaneously are independent tasks; nothing polls.

## 5. Tab Lifecycle

- **Close (✕ on tab header):** completes that agent's root session gracefully
  (`RootSessionLifecycle.CompleteAsync`), disposes its container, removes the tab.
  Bootstrap-failure cleanup reuses this path minus lifecycle completion.
- **Background clarify indicator:** small dot on the tab header while that tab's agent
  awaits a clarify answer.
- **Shell close:** gracefully complete ALL open sessions (parity with today's
  `MainWindow.Closed` handling for one), then exit.

## 6. Error Handling

- **Open Agent failures** (missing API key, config load, bootstrap/persistence errors):
  error dialog; placeholder removed; no zombie tabs ever visible.
- **Picker cancelled:** silent no-op.
- **Turn-level errors:** unchanged — tool errors return to the model as tool results;
  turn failures surface as transcript notices inside their own tab. One tab failing
  never touches another.
- **Shell close mid-turn:** graceful completion of all sessions, then exit.

## 7. Testing

- **Unit (headless, `Desktop.Tests`):** `ShellViewModel` driven entirely through
  injected delegates — open-success adds & selects a tab; duplicate focuses instead of
  adding; bootstrap failure removes placeholder + calls error-dialog delegate;
  picker-cancel is a no-op; tab close invokes lifecycle-complete and disposal; shell
  close fans out to all sessions. `WorkspaceStartupFlow` tests deleted with the class.
- **ACL unit:** Roslyn exec engine honors an explicit workspace while
  `Environment.CurrentDirectory` points elsewhere — proving the ambient dependency is
  gone.
- **Composition/integration:** `AddEThangAgentCore` accepts a shared `AppDatabase`;
  two containers built for different workspaces resolve distinct workspace context /
  resolvers but one DB instance.
- **E2E (headless, mock provider server):** empty shell renders; simulated Open-Agent
  → tab appears with live transcript; a submitted turn streams into the right tab only.

## 8. Documentation

README.md and AGENTS.md updated in the same change (startup-flow description changes
materially).

## 9. Non-Goals (this step)

- Session restore across app restarts (Q1 option C) — later step.
- Docking/MDI layouts, split views, floating panels.
- One-process-per-agent isolation (noted escape hatch if in-process concurrency
  proves unsound).
- Per-workspace model configuration UI.