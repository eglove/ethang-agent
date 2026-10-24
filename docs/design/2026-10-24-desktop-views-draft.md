# Desktop views (realtime) — lofi draft

Issue: [#3](https://github.com/eglove/ethang-agent/issues/3) · Status: **draft for reaction** · 2026-10-24

Nothing here is decided. This is the shape of the thing, drawn cheaply so we can
argue about it before anything is built.

**Look at it:** open [`mockups/desktop-views.html`](mockups/desktop-views.html) in a
browser (or the per-frame PNGs beside it) — seven rendered frames covering today and
the three placements, drawn with real data from this workspace.

## 1. What we have now

One window. A 48px icon rail, a resource bar, a tab strip, and one `AgentView` per
tab (keep-alive). Inside a tab: the chat, plus a subagent accordion that appears
only while children exist.

```
+--+------------------------------------------------------------------+
|  | Memory 412 MB [====      ] normal   Details                      |
|[]+------------------------------------------------------------------+
|  | [ workspace-a x] [ workspace-b x]                               |
|  +------------------------------------------------------------------+
|  |                                                                  |
|  |   transcript                                    | subagents      |
|  |   > read src/foo.cs                             |  child-1 done  |
|  |   ...                                           |  child-2 run   |
|  |                                                                  |
|  |------------------------------------------------------------------|
|  | > input                                                 [stop]  |
|  | ready  ctx 41%  $0.004  main  model  effort: high               |
+--+------------------------------------------------------------------+
```

Everything the agent knows is in that transcript, and the transcript is a
scrollback. Plans, todos, git state, and the file tree all exist — as text the
model printed, already scrolled away. You cannot see the *current* state of
anything without asking the model to print it again.

## 2. The views the issue names

| View | Question it answers | Where the data already lives | Freshness |
| ---- | ------------------- | ---------------------------- | --------- |
| Plans | what is this session trying to do, and how far along | `IPlanStore` (`plans`, `plan_steps`) | event |
| Todos | what is on the list right now | state key `todo/list` (one JSON blob) | event |
| Git status | what is dirty, on what branch | `IGitQueryAccess.GetStatusAsync` | file watch |
| Diff | what changed in this file | `IGitQueryAccess.GetDiffAsync` | file watch |
| PRs | what is in flight upstream | **nothing yet — no GitHub seam** | poll |
| CI | did the branch go green | **nothing yet — no GitHub seam** | poll |
| File tree | what is in the workspace | the file system | file watch |
| Kanban | every plan's steps as cards, across sessions | `IPlanStore` (all plans) | event |
| Agent progress | what are the children doing right now | `IAgentEvents` child stream + `ChildProgressLedger` | event |
| Full transcript | what did that child actually do | `IAgentStore.GetTranscriptAsync` | poll today |
| App database | what does the harness itself record | `ISelfDatabaseAccess` (`db_query`) | on demand |

Eleven rows for nine names — "live agent progress" and "full transcripts" are
two different panes on the same child, and "app database" is really a query
console. Worth splitting or merging deliberately rather than by accident.

## 3. Three kinds of realtime, and they are not interchangeable

**A. File watch** — `FileSystemWatcher` + debounce. Already proven twice in this
repo: `GitBranchWatcher` (watches all of `.git`, 200 ms debounce) and
`SkillDirectoryWatcher`. Cheap, no polling, and the only source that sees work the
agent did not do (you editing in another editor, `git checkout` in a terminal).

**B. App events** — the in-process buses that already exist: `IAgentEvents`
(child started / progress / settled), `IStateChangeBus` (state writes, so
`todo/list` and every `sci.*` ledger), `PlanService.Created/StepChanged/StatusChanged`.
Instant and precise, but blind to anything outside the app.

**C. Database change** — for views whose truth is a table (`plans`, `agents`,
`agent_events`), there is no bus across the process boundary: the conversation
runs in the ChildHost, so a plan the agent writes is written by *another process*.

### One verified finding worth acting on

`PRAGMA data_version` is the cheap cross-process change signal. Measured in this
workspace just now:

| Action | Reader's `data_version` |
| ------ | ----------------------- |
| initial | 1 |
| another connection inserts | 2 |
| another connection inserts again | 3 |
| the reader's own connection inserts | **3 (unchanged)** |

So a single `PRAGMA data_version` poll — one integer, no query plan, no I/O worth
measuring — tells a view whether *any other process* changed the database, and
deliberately ignores its own writes. That is the whole cross-process realtime story
for the SQLite-backed views, at a poll rate we get to choose (1 s is generous).

## 4. Where the panes go

Three placements are on the table. My recommendation is the third.

**(1) Tabs beside the chat** — a second tab strip inside the tab. Simple, but it
hides the transcript while you look at a plan, and the transcript is the thing you
are cross-checking against.

**(2) Dockable panes, IDE style** — full control, drag anywhere, save layouts.
Months of work in Avalonia (docking library or hand-rolled), and it makes every
future view pay a layout tax.

**(3) A right-hand workbench, one view at a time** — the subagent accordion
generalizes: it is already a right column with a splitter that appears when it has
content. Make that column permanent, give it a small icon strip of its own, and
each view is a `UserControl` swapped in. Nothing drags; nothing overlaps; the chat
keeps the width it has today when the workbench is collapsed.

```
+--+---------------------------------------------+---+---------------+
|  | transcript                                  | s | PLANS         |
|[]|  > read src/foo.cs                          | p |---------------|
|  |  ...                                        | l | #42 active    |
|  |                                             | a |  [x] step 1   |
|  |                                             | n |  [>] step 2   |
|  |                                             | s |  [ ] step 3   |
|  |                                             |---|               |
|  |                                             | t |               |
|  |                                             | o |               |
|  |                                             | d |               |
|  |                                             | o |               |
|  |                                             | s |               |
|  |                                             |---|               |
|  |                                             | g |               |
|  |                                             | i |               |
|  |                                             | t |               |
|  +---------------------------------------------+---+---------------+
|  | > input                              [stop]                     |
+--+------------------------------------------------------------------+
```

Three of these views (plans/todos, git, progress) answer questions you ask *while
watching a turn run*, which is exactly why they belong beside the transcript and
not behind a tab switch.

## 5. What already exists behind the seam

More than expected. Every read view below needs **no new domain code** — only a
view-model over a seam that is already wired:

| Need | Seam | Status |
| ---- | ---- | ------ |
| git status / diff / branch | `IGitQueryAccess` | wired in every session container |
| worktrees | `IGitWorktreeAccess` | wired |
| plans + steps | `IPlanStore` | wired, but see the data hazard below |
| todos | state key `todo/list` via `IStateStore` | wired, but see below |
| state writes | `IStateChangeBus` | wired |
| child progress | `IAgentEvents` + `ChildProgressLedger` | wired |
| child transcripts | `IAgentStore.GetTranscriptAsync` | wired |
| usage / spend | `IUsageQueries` | wired |
| watchdog decisions | `IWatchdogEventStore` | wired |
| `!` command history | `ICommandRunStore` | wired |
| the database itself | `ISelfDatabaseAccess` | wired |

The genuine gaps: **GitHub** (PRs, CI — no seam, no client, no token storage),
and **file tree** (no seam; `IFileSystemAccess` reads files, it does not walk a
tree).

## 6. Two data hazards the views will expose immediately

Found while surveying, both real today:

1. **`todo/list` is one JSON blob of 684 items** (489 completed), 77,170 characters,
   rewritten whole on every change. A todo view renders that fine; a *kanban* view
   or any per-item interaction will want rows, not a blob. Filed as **#280**
   (blocking for the kanban view).
2. **Six `plan_steps` rows carry status `Completed`**, which is not a member of
   `PlanStepStatus` (`Pending | InProgress | Done`). `plan.index` throws on them
   today: `corrupt plan row: unknown step status 'Completed'` — and because it
   throws while materializing the *list*, no plan is readable at all. Filed as
   **#279** (blocking for the plans view).

## 7. Suggested first slice

Pick the views that (a) need no new external seam and (b) prove all three realtime
sources at once:

- **Plans + todos** — proves app events and the database-change poll.
- **Git status + diff** — proves the file watch (and reuses `GitBranchWatcher`'s
  exact shape).
- **Live agent progress** — proves the child-event stream, and replaces the
  500 ms transcript poll with something honest.

GitHub/CI and the kanban board come after, because they each need a new seam
first.

## 8. Decisions (settled 2026-10-24)

| # | Decision | Choice |
| - | -------- | ------ |
| D1 | Placement | **Right-hand workbench** — a permanent column with its own icon strip, one view at a time, chat keeps full width when collapsed. Generalizes the subagent accordion. |
| D2 | First slice | **Plans + todos, git status + diff, live agent progress.** Proves all three realtime sources and needs no new external seam. |
| D3 | Scope | **Per-tab** — each workspace tab remembers its own view and scroll position, matching how the subagent panel already behaves. |
| D4 | Interactivity | **Read-only**, with one exception: clicking a plan step or todo scrolls the transcript to where it was last touched (navigation, not mutation). |
| D5 | The panel becomes permanent | The workbench does not appear and disappear on content. It is always present; the auto-open-on-subagents mechanism is retired. |
| D6 | GitHub reach | **Drive the `gh` CLI as a subprocess** — the user must have `gh` installed and authenticated. No GitHub API client, no token storage, no OAuth flow. Same shape as `DirectGitAccess` driving `git`. |
| D7 | File tree | **A view in the right panel** (not a separate picker). Read-only in v1. |
| D8 | Icon grouping | **Six icons for nine views**, grouped: Plans (todos + kanban inside), Git, Files, Progress, GitHub (PRs + CI inside), Database. No menu, no scrolling strip. |
| D9 | No transcript view | The chat window **is** the transcript surface. A separate live transcript renderer would duplicate it with two chances to drift (#15 closed). |

### Consequence of D5: the panel bug stops being load-bearing

The subagent panel never appearing today (#281) is a real defect — the host's root
child-event forwarder attaches only as a side effect of steering — but the workbench
removes the auto-open mechanism it breaks. The **progress view** still needs child
events, so #281 (or its equivalent) must be fixed for that view to show anything.

## 9. Still open

- **`gh` missing or unauthenticated**: D6 makes `gh` a prerequisite. The views must
  degrade to a named, actionable empty state ("gh is not installed" / "gh is not
  authenticated — run gh auth login"), never a blank pane or a crash.
- **Poll interval** for the database-change path and for `gh`-backed views (1 s and
  10–30 s proposed respectively).
- **File tree click behaviour**: preview in the pane, or open in an external editor?
- **Kanban column naming**: `PlanStepStatus` uses `Done`; todo statuses use `Completed`.
- **Transcript search across sessions**: noted for later, not part of this parent. The
  app database's FTS5 indexes cover `state_keys` and `tool_output_archive` — **not**
  `agent_messages` — so it needs a migration plus triggers, or a scan. `memory.recall`
  already searches transcripts and is the precedent to follow.

## 10. Why there is no transcript view (D9)

The original sub-issue list had a "session transcript view". It is closed, because the
chat window already is that surface — and better: streaming, tool cards, images, and
reasoning, none of which a second renderer would get for free. Two live renderers of the
same conversation means two chances to drift.

A related finding worth keeping: **the chat and the stored transcript are not the same
content.** `TranscriptViewModel` renders entry types with no `agent_messages` row behind
them — `ReasoningEntry` (telemetry-only by design, #263), `NoticeEntry` (host health,
orphan repair, skill reloads), and `BootstrapEntry` (the system prompt display). A
resumed session does not show them, because they were never persisted. Any future
"read the stored transcript" surface should expect to look *sparser* than the chat.
