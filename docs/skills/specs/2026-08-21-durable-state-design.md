# P3 Durable State — Design Specification

**Date:** 2026-08-21 · **Status:** Approved in review (sections 1–4) · **Phase:** 3 of 7 (pi-fabric native port)

## Context & Goals

P2 gave scripts a capability registry; the model surface is `exec` alone. P3 adds the discipline layer from pi-fabric's state layer: a durable world-model where the model **claims** work via transitions, attaches **evidence** (PowerShell commands, attached but never run on attach), and only **fail-closed verification** certifies it — plus a CAS key-value store for durable process state.

**Grand-plan alignment (user-directed correction):** the destination is a desktop app managing many projects from one contained space — **no project-level dot-folders or config files; everything lives in app-owned databases, with manual edits blocked by design.** P3's store is therefore the first tenant of shared app database infrastructure, workspace-keyed so the desktop app's future project list and the kanban/supervisor tables land in the same database later.

## Approved Decisions

- **D1 — Scope B (user decision):** durable KV store with CAS **plus** the certification engine (transitions, evidence, fail-closed verify, durable certificates, goal checks). Full enforcement (audit/enforce gating of actions) is **planned future work**, not built now — see Future Work.
- **D2 — In-app SQLite (user decision):** one app-owned database via `Microsoft.Data.Sqlite` in a new `eThangAgent.Storage.ACL` (connection source + versioned migrations). Default path `%LOCALAPPDATA%\eThangAgent\eThangAgent.db`, env override `ETHANG_AGENT_DB`. Never inside project folders.
- **D3 — Workspace-keyed:** every row carries `WorkspaceId` (canonical working directory via `IWorkspaceContext`, CLI-implemented). One database serves many projects.

## Architecture

```text
eThangAgent.State.Domain (NEW — pure logic)
  StateService (CAS, transitions, certification decisions)
  IStateStore / IEvidenceRunner seams
     ▲                                ▲
eThangAgent.Storage.ACL (NEW)    PowerShell.ACL
  AppDatabase (SQLite conn,        PsEvidenceRunner
  versioned migrations)            (fresh runspace per command)
  SqliteStateStore : IStateStore
     ▲ implements provider
eThangAgent.State.Domain
  StateCapabilityProvider ("state")
```

- Storage schema: `state_keys(workspace_id, ns, name, value, version, updated_at)` PK `(workspace_id, ns, name)`; `transitions(id, workspace_id, from_state, to_state, summary, evidence_json, status, created_at)` status `pending → certified | violated`; `state_events(id, workspace_id, kind, payload_json, occurred_at)` append-only.
- CAS is native SQL: `UPDATE … SET value=@v, version=version+1 WHERE version=@expected`; zero rows → fail-closed `VersionConflict` naming the current version. Atomicity via SQL transactions.
- Migrations: versioned runner in Storage.ACL — beachhead for later grand-plan tables (kanban, agent statuses, SDLC tickets).

## Lifecycle & Fail-Closed Certification

```text
state.transition ──► TRANSITION (pending) ──► state.verify ──► all evidence confirms
     (claim: from → to, summary,                       │            ├─► CERTIFIED (event; if head:
      evidence[] attached, never run on attach)        │            │    certificate CAS-persisted)
                                                       └─ any failure ─► VIOLATED (event + reasons;
                                                                            head certificate revoked first)
```

- **Fail-closed rules (ported from pi-fabric):** verification certifies only when at least one transition is selected and every evidence command confirms. Missing targets, empty evidence, exceptions, non-empty error streams, timeouts, cancellations → `Certified: false, Violated: true` with explicit blocking reasons. Failing head re-verification revokes the durable certificate before the violated event.
- **Evidence semantics:** each evidence item is PowerShell text run by `PsEvidenceRunner` in a fresh runspace. Confirmed = no errors written AND `$LASTEXITCODE` is 0 or unset. Per-command timeout 120s (config-only).
- `state.checkGoal` runs the `goal/check` command set — report-only, no certification semantics.
- Enforcement seam: every mutation funnels through `StateService`, so future enforce mode is a decorator over the capability-invocation path plus a mode switch.

## Capability Surface (`state` provider)

| Action | Parameters | Semantics |
| ----- | ----- | ----- |
| `state.get` | `key` | Value or `KeyNotFound` |
| `state.set` | `key`, `value`, `expectedVersion?` | CAS write → new version; stale → `VersionConflict` |
| `state.delete` | `key`, `expectedVersion?` | Same CAS rule |
| `state.list` | `ns?` | `ns/name v<version>` lines |
| `state.transition` | `from`, `to`, `summary`, `evidence: String[]` | Attach claim + evidence; returns id; `pending` |
| `state.verify` | `ids?: String[]` | Default all pending; fail-closed report; statuses + timeline + head certificate |
| `state.checkGoal` | — | Runs `goal/check`; report-only |
| `state.history` | `limit?` (default 20) | Timeline replay |

Summaries explicit; full descriptions carry semantics; generated reference picks them up automatically. Guide v1.2 adds a short durable-state pointer. Workspace identity via `IWorkspaceContext` (CLI: canonical cwd).

## Testing Strategy

- **Unit — State.Domain (fakes):** CAS matrix; transition attach/pending; verify fail-closed matrix (nothing selected, not-confirmed, runner throws/timeout, all-confirm + head certificate persistence, head revocation ordering); CheckGoal; History; provider descriptors + gutters (`KeyNotFound`, `VersionConflict`).
- **Integration — Storage.ACL (real SQLite, temp dbs):** migrations idempotent; CAS affects exactly the expected-version row; transaction rollback; event ordering; workspace isolation; `PsEvidenceRunner` real-runspace matrix (confirmed, `$LASTEXITCODE 1`, error records, syntax error, timeout — all fail-closed).
- **E2E:** generated reference contains `state.*` lines; tools array still exec-only; full discipline loop via exec scripts — set → get → transition → verify **certified**; failing evidence → **violated** with reasons.
- **Definition of done:** build clean under full scan; full suite green; coverage State.Domain ~100%, Storage.ACL ≥ 80%; spec cross-check verbatim.

## Future Work — Full Enforcement (planned, user-required)

Reserved now, built later (trigger: P4 multi-agent callers): `mode` shape `off | audit | enforce` (invalid → off) in app configuration; **audit** publishes would-block events without gating; **enforce** wraps the capability-invocation choke point (`ICapabilityRegistry` invocation path) with a decorator denying non-read actions without a valid certificate, binding certificates to invocation identity; host-owned **trustedCommands** allowlist (never model-supplied); clamps (TTL 1s–10min, file/byte caps) when file-mutation gating arrives with a future `edit` action.

## Out of Scope (P3)

Enforcement modes (future work above), MCP provider, nested exec (P4), additional providers, desktop UI/kanban/supervisor tables (Storage.ACL is their beachhead, not their build).
