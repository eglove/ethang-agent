# Stage 1 — Methodology Port (Superpowers + Hermes Learning Loop + Commit Tooling)

> Status: APPROVED DESIGN (brainstormed 2026-08-21). This spec is the source of truth for Stage 1 implementation plans.

## Goal

Port the superpowers development methodology and the useful core of the Hermes agent into eThang Agent as **native, domain-owned features** — not as loose markdown conventions. The agent gains: a real Skill subsystem with session-start bootstrap injection, safe file-manipulation tools, structured clarification and task-tracking tools, commit-as-a-typed-tool (conventional and gitmoji styles, no npm dependency), and a database-backed learning loop (curated memories, nudges, autonomous skill creation).

## Guiding decisions (agreed during brainstorm)

1. **Skills stay verbatim.** Superpowers skill bodies are tuned behavioral code; the port never rewords them. Harness adaptation happens exclusively through (a) the bootstrap injection and (b) an `ethang-tools.md` action mapping, per superpowers' own porting guide.
2. **Tools over prose wherever a procedure is deterministic.** The user explicitly prefers typed-argument tools over markdown skills (e.g., commit is a tool, not a skill). This principle drives D1–D3.
3. **Memories are a searchable database, not injected markdown.** What enters context is *usage guidance*; content is recalled on demand through tools.
4. **`clarify` is the mandatory mechanism for brainstorming's clarifying questions**, bound via the tool mapping (no skill-body edits).
5. **gitmoji-cli is not ported.** Gitmoji support is a native emoji lookup table inside the commit tool. The markdown `/commit`-style approach is retired in favor of the typed `git_commit` tool.
6. **Full inventory ruled feature-by-feature** (keep / pass / modify). See Appendix A for the complete ledger including deferred and rejected features with reasons.
7. **Prerequisite discovered:** the model currently has no write/edit/search access to files (only `exec` and `read`). Ported skills assume real file manipulation, so file tools come first.

## Sub-project sequence

Each sub-project gets its own spec-derived implementation plan and leaves the build green.

| # | Sub-project | Delivers |
| --- | ------------- | ---------- |
| SP1 | File manipulation tools | `write`, `edit`, `search_files` |
| SP2 | Skill subsystem + superpowers port | Skill Domain, skill tools, bootstrap injection, 14 skills, `clarify`, `todo` |
| SP3 | Git workbench | `git_status`, `working_diff`, `git_commit` |
| SP4 | Learning loop | Memory entries + curation tools, nudges, autonomous skill creation |

---

## SP1 — File manipulation tools

**Owner:** Tool Domain. All I/O through the existing `IFileSystemAccess` seam (FileSystem ACL). No direct `System.IO` in domain code beyond what the ACL already encapsulates.

### `write` tool

Creates or replaces a file.

- Input (all strictly validated): `path` (required, workspace-rooted absolute or relative; traversal outside workspace rejected), `content` (required string), `overwrite` (required boolean — refusing silent replacement is the point).
- Errors returned as tool results: missing parent directory (with hint), exists-without-overwrite, path escapes workspace.
- Writes UTF-8 without BOM. Content written byte-for-byte as provided; no line-ending rewriting.
- Success output contract (annotation lines, documented verbatim in the tool description): bytes written, absolute path, created-vs-overwritten.

### `edit` tool

Literal (non-regex) exact-match editing.

- Input: `path` (required), `old` (required, must exist verbatim in file), `new` (required), exactly one of `occurrences` (integer ≥ 1 — expected replacement count; mismatch is an error naming actual count) or `all` (boolean true — replace every occurrence); providing both is a validation error.
- Errors: anchor not found (with nearest-match hint when cheap), occurrence-count mismatch, binary file detected.
- Success output contract: replacements made, resulting line count, absolute path.

### `search_files` tool

Bounded content search over workspace text files.

- Input: `pattern` (required), `mode` (required enum: `Literal | Regex`), `path` (optional scope subdir, default workspace root — documented, not silent), `glob` (optional file filter), `max_results` (required integer, hard-capped; overshoot clamps with visible warning — the one sanctioned leniency), `context_lines` (optional, default 0 when omitted — documented).
- Skips binaries and `.git`. Results streamed/bounded; output ends with a continuation notice when truncated, accounting for the whole.
- Output format contract: guttered matches with `path:line` annotation lines, exactly like the read tool's house style.

---

## SP2 — Skill subsystem + superpowers port

### New bounded context: Skill Domain (`eThangAgent.SkillDomain`)

- **Domain:** `SkillDefinition` record (immutable): `Name`, `Description`, `Body`, `Version` (int), `Source` (`BuiltIn | Learned`), `Provenance` (creating session id, nullable), audit timestamps. Specifications: unique-name, valid-frontmatter, name-charset rules. `IBuiltInSkillCatalog` + `ILearnedSkillStore` interfaces live here.
- **Application:** queries `ListSkills`, `ViewSkill`; commands `CreateSkill`, `UpdateSkill` (bumps version, retains history — see SP4), `DeleteSkill` (requires explicit `confirm:true`), `RecordSkillUsage`. CQRS separation throughout.
- **Infrastructure:** built-ins ship as **embedded resources** (verbatim `SKILL.md` files with frontmatter); learned skills persist via the Storage ACL (SQLite, versioned migrations). Built-in names are authoritative: creating/shadowing a learned skill with a built-in name is rejected.
- **Capability surface:** `skill_list` (name + description table), `skill_view` (full body by name), `skill_manage` (typed create/update/delete actions). Usage rows recorded on `skill_view`.

### Superpowers port

- All 14 skills embedded verbatim (see Appendix B for the list).
- **Bootstrap:** new `ISystemPromptProvider` implementation added to the composite at the composition root. Injects once per session: the `using-superpowers` SKILL.md wrapped in `<EXTREMELY_IMPORTANT>` tags (superpowers convention), the action mapping below, and a note that the bootstrap skill is already active so the model never re-loads it.
- **Tool mapping (`ethang-tools.md`)**, also embedded and referenced by the bootstrap:

| Action (as named by skills) | eThang Agent binding |
| --- | --- |
| Read a file | `read` tool |
| Write / edit files | `write` / `edit` tools |
| Search files | `search_files` tool |
| Run shell commands / tests / git plumbing | `exec` tool (PowerShell) |
| Dispatch a subagent | spawn sub-agent capability |
| Create/update todos | `todo` tool |
| Invoke a skill / load its content | `skill_view` tool (reading raw SKILL.md paths is NOT available; the skill store is the mechanism) |
| List available skills | `skill_list` tool |
| Ask the human partner a clarifying question | `clarify` tool (**mandatory** for brainstorming) |
| Track plan progress | `todo` tool + `docs/superpowers/plans/*.md` checkboxes |
| Commit work | `git_commit` tool once SP3 lands (row added to this mapping by SP3; never raw shell commit) |

### `clarify` tool (Tool Domain; Terminal ACL interaction)

- Input: `question` (required), `options` (optional string array; ≥ 2 when present), `allow_free_text` (required boolean).
- Renders numbered options via the Terminal ACL; returns the chosen option text verbatim or captured free text as the tool result.
- **Piped mode:** deterministic — reads one stdin line; an integer selects that 1-based option, anything else is free text (rejected with error result if `allow_free_text:false`). This makes brainstorming flows testable end to end.

### `todo` tool (Tool Domain; backed by State Domain store)

- Workspace-scoped single active list. `TodoItem`: id, description, status (`Pending | InProgress | Completed`).
- Typed actions: `add`, `update`, `complete`, `remove`, `list`, `clear`. Unknown ids and illegal status transitions are error results, never coerced.
- Replaces markdown-checklist tracking for agent-driven flows while plans keep their checkbox syntax for human review.

---

## SP3 — Git workbench

All git operations run through the PowerShell ACL (git is a shell command; no new ACL is warranted — the seam exists). Queries and commands are CQRS-separated.

### Queries

- `git_status`: parses porcelain output into structured entries; formatted per output contract.
- `working_diff`: `scope` required enum (`Staged | Unstaged | All`), optional path filter; bounded output with continuation notice (same discipline as `read`).

### `git_commit` command

- Required args: `style` (`Conventional | Gitmoji | None`) — stated explicitly, no silent default; `description` (single line, non-empty, ≤ 72 chars after trim — violation is a validation error, not a clamp); `type` (required iff style=Conventional; validated against the fixed conventional set: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert); `emoji_key` (required iff style=Gitmoji; validated against the embedded gitmoji table); optional `scope` (lowercase alphanumeric + `-`), optional `body` (multi-line).
- Message assembly is deterministic and shown in the result: final subject line, body, resolved emoji where applicable.
- Refuses to commit when nothing is staged — error result with actionable next step (stage via `exec git add …`). Never auto-stages.
- Returns commit hash + rendered message via the output contract.
- The markdown `/commit`-skill approach is retired; usage guidance lives in the tool description.

---

## SP4 — Learning loop

### Curated memories (Memory Domain extension)

- New aggregate `MemoryEntry` (immutable): `Id`, `Category` (fixed enum: `Convention | Preference | Insight | Failure | Reference`), `Tags` (free-form, charset-validated, deduped), `Content` (required, bounded length), `UsageHint` ("when to reach for this", optional), `Scope` (`Workspace | Global`), `Provenance` (source session, nullable), timestamps + row version for optimistic updates.
- Persistence via Storage ACL; SQLite FTS index over content/tags; category/scope filters. Versioned migrations.
- Tools: `memory_search` (`query?`, `category?`, `tags?`, `scope?`, bounded `limit` — ranked hits include usage hints), `memory_add`, `memory_update` (version-checked; stale writes rejected), `memory_remove` (`confirm:true` required).
- **System prompt carries usage guidance only** — when to search, when to write — never bulk memory content.

### Periodic nudges

- `INudgePolicy` interface (domain-owned) with a default heuristic implementation wired at the composition root: after a turn with significant tool activity and zero memory writes this session, append a one-line reminder prompting curation. Event-driven at turn boundaries; no polling, no background threads.

### Autonomous skill creation & self-improvement

- Post-complex-task reflection is prompted via system-prompt guidance (trigger conditions documented there: completed plan execution, or repeated manual corrections on a theme). The agent proposes skills through `skill_manage` with `Source: Learned` and session provenance — no hidden background writer.
- Self-improvement (`skill_manage update`) always creates **version N+1**; history rows are retained and viewable; provenance records the mutating session. Silent mutation is impossible.
- Security machinery for third-party skills (AST audits, threat scans, hubs/sync) is explicitly deferred until third-party skills exist (Appendix A, C3).

---

## Testing strategy

Per repository conventions (AGENTS.md):

- **Unit (fakes only):** specification evaluation, message assembly, validation edges, nudge policy, registry merge/collision rules. Domain tests never see PowerShell, HTTP, SQLite, or OpenRouter.
- **Integration (real ACL implementations):** SQLite stores + migrations, embedded catalog loading, FTS search quality, file-tool behavior against real temp directories, git tools against real scratch repositories.
- **E2E (piped CLI vs mock OpenRouter server):**
  - Bootstrap assertion: the mock provider captures the outgoing request and asserts the system prompt contains the wrapped `using-superpowers` content + mapping markers — deterministic, no live model.
  - Acceptance flow: fresh piped session driven through scripted mock turns exercises brainstorm → clarify (stdin-driven) → todo → skill_view → plan doc creation.
  - Commit flow: staged changes → `git_commit` conventional and gitmoji variants asserted on real scratch repos.
- **Coverage:** aim 100%, floor 80%, enforced per existing Directory.Build.props setup.
- **Manual gate (documented, not CI):** one live-model run of "Let's make a react todo list" confirming brainstorming triggers before any code — superpowers' own definition-of-done check.

## Global constraints

- .NET 10, C#, Windows-only, PowerShell as the only shell. No new external dependencies (gitmoji table is embedded data; skills are embedded resources).
- Every sub-project leaves `dotnet build` + `dotnet test` green before it is done.
- New projects follow packaging/naming conventions: `eThangAgent.SkillDomain` (+ Application/Infrastructure layers as it earns them), namespace without dot.
- DI wiring only at the composition root; domains depend on interfaces.
- Model-facing outputs use explicit format contracts documented verbatim in tool descriptions.
- README updated in the same change set whenever user-facing behavior lands.

## Acceptance criteria (Stage 1 done means)

1. Fresh session system prompt contains the superpowers bootstrap and mapping (E2E-asserted).
2. All 14 skills listed by `skill_list` and loadable verbatim via `skill_view`.
3. `write`/`edit`/`search_files` pass contract + safety tests; workspace escape rejected.
4. Brainstorming's clarifying questions flow through `clarify`; piped-mode behavior deterministic.
5. `todo` persists workspace-scoped items across sessions via State Domain.
6. `git_commit` produces correct conventional/gitmoji messages, rejects invalid input strictly, refuses empty staging area.
7. Memories persist across sessions; `memory_search` ranks by relevance with category/tag filters; stale `memory_update` rejected.
8. Learned-skill updates create retained versions; built-in names cannot be shadowed.
9. Manual acceptance run recorded: brainstorming triggers before code on a fresh live session.
10. Coverage floor met; README current.

## Appendix A — Feature ledger (rulings from brainstorm)

### Kept

| Item | Ruling |
| --- | --- |
| A1–A14: all 14 superpowers skills | Verbatim port |
| B1 memory curation | DB-backed (this spec reshapes it per decision #3) |
| B2 periodic nudges | Heuristic policy, event-driven |
| B3 autonomous skill creation | Via skill_manage, provenance tracked |
| B4 skills self-improve | Modify → versioned updates, never silent |
| C1 skill store + list/view/manage tools | Native Skill Domain |
| C2 skill usage tracking | Recorded on view/use |
| D1 todo tool | State-backed, typed actions |
| D2 clarify tool | Mandatory for brainstorming via mapping |
| D3 working_diff | Query-side git tool |
| F commit-as-tool | Conventional + Gitmoji + None styles, native table |

### Passed for now (deferred)

A15 visual companion (Node server; future enhancement) · G-list: browser automation, vision/multimodal, MCP, cron scheduler, kanban multi-agent, MoA, context compaction, approval gates, model routing/metadata, checkpointing · C3 third-party-skill security machinery (arrives with third-party skills) · E-gap partially: write/edit/search land in SP1, remaining file ergonomics later as needed.

### Rejected

H-list: messaging gateways (Telegram/Discord/Slack/WhatsApp/Signal), voice stack (TTS/transcription/wake word), image/video generation, X search, Feishu/Drive integrations, computer-use (macOS-gated), Home Assistant, pet/onboarding/tour, trajectory-compression research tooling, Hermes' TUI/desktop affordances. Reasons: different product surfaces than a Windows-native coding agent heading toward a desktop app; revisit only if the roadmap changes.

## Appendix B — Embedded superpowers skills (verbatim)

using-superpowers, brainstorming, writing-plans, executing-plans, subagent-driven-development, dispatching-parallel-agents, test-driven-development, systematic-debugging, verification-before-completion, requesting-code-review, receiving-code-review, finishing-a-development-branch, using-git-worktrees, writing-skills.
