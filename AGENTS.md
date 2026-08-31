# eThang Agent

> **Keep this document current**: AGENTS.md describes how the system works *today*, not how it was built. When a change makes any statement here stale — an ACL replaced, a constraint dropped, a convention changed — update this file in the same change. Verify claims against the code before relying on them.

## What This Project Is

eThang Agent is an AI agent harness built with .NET, delivered through an Avalonia desktop application. The project follows strict Domain-Driven Design: layered bounded contexts, Specification patterns, and CQRS.

> **Read [`README.md`](README.md) first** — it carries the high-level overview and usage instructions. Whenever a change makes either stale, update the README in the same change.

## Guiding Philosophy

These principles explain every convention below. When a rule seems arbitrary, it traces back to one of these.

- **Seams over commitments.** Every external dependency — AI provider, shell, file system, UI — sits behind an interface the domain owns. Nothing external is ever allowed to become load-bearing inside a domain. Any implementation behind a seam must be replaceable without touching domain code.
- **Strict correctness at the boundaries.** Inputs crossing into the domain are validated completely: required parameters are required, types are exact, unknown input is rejected, and nothing is silently coerced, defaulted, or clamped. The one deliberate exception (a benign overshoot that is clamped with a visible warning) proves the rule — leniency is a named, documented decision, never a default.
- **Errors are information, not crashes.** Expected failures flow through `Result<T>` / error values and are delivered to whoever can act on them — including the model itself, which receives tool errors as tool results so it can self-correct. Exceptions are reserved for programmer error.
- **Reversibility by construction.** The architecture must not preclude alternative frontends, platforms, providers, storage engines, or orchestration topologies. When a choice would foreclose an option, choose the seam instead.
- **The codebase is not the standard.** Existing code records what was done, not what is right. Never treat a current pattern as best practice just because it is present — when work touches code and a better way exists, improve it in the same change. Improve what you touch; do not launch unrelated refactors.
- **Lean, event-driven operations.** Aim for high performance and a small memory footprint. Prefer event-driven designs — react to what happened instead of polling, stream instead of loading, and don't hold what you're not using.
- **Direction: a self-contained agent.** The long-range shape (per the grand plan's philosophy, not its plan details) is a persistent desktop application that manages its own world — conversations, sessions, state, configuration — in app-owned structured storage rather than scattered project-level files. Work written today must fit that destination: durable state flows through domain-owned stores, never ad-hoc files.

## Architecture Principles

### Domain-Driven Design

The codebase is organized around business domains, not technical concerns. Each domain owns its models, logic, and boundaries. Domains communicate through well-defined contracts — never by reaching into another domain's internals.

- **Ubiquitous Language**: domain models and code reflect the language of the problem space (agents, conversations, tools, providers).
- **Bounded Contexts**: each domain has clear boundaries and single ownership of its concerns.
- **Aggregates**: consistency boundaries are enforced through aggregate roots.
- **Domain Events**: side effects across domains are communicated through events, not direct calls.

### CQRS (Command Query Responsibility Segregation)

Commands and queries are separated:

- **Commands** mutate state and do not return data. They go through domain logic, validation, and specification evaluation.
- **Queries** return data and have no side effects. They bypass domain logic and use optimized read models where beneficial.

### Specification Pattern

Business rules and validation logic are encapsulated in composable Specification objects. Specifications can be combined (and, or, not) and are evaluated against domain objects before mutations are applied.

### Anti-Corruption Layers (ACLs)

Any external system or platform-specific concern is isolated behind an Anti-Corruption Layer. The domain never depends directly on external SDKs, platform APIs, or infrastructure. ACLs translate between the domain's ubiquitous language and the external system's concepts. An ACL exists because its seam earns its keep — each table row states why the seam exists.

| ACL | Purpose | Why the seam exists |
| ----- | --------- | --------------------- |
| OpenRouter ACL | Translates domain concepts (messages, models, tool calls) to/from OpenRouter's API (`OpenRouterModelProvider`, `OpenRouterCatalogClient`). The catalog client fetches all providers per model via the endpoints API, computes effective prices (after discount), and parses capability scores. The domain knows nothing about OpenRouter-specific types, endpoints, or authentication. | The domain speaks its own message/tool language, so any provider that can express it can be wired in without domain changes. |
| z.ai ACL | Translates domain concepts to z.ai's OpenAI-compatible API (`ZaiModelProvider`, `ZaiModelCatalog`, six capability tools: web search, reader, tokenizer, image generation, OCR, transcription). The endpoint mode (`ZaiEndpointMode`) picks the endpoint family: **CodingPlan** (default) chats through `https://api.z.ai/api/coding/paas/v4` — where GLM Coding Plan keys are entitled and the capability APIs do not exist — and **GeneralApi** chats through `https://api.z.ai/api/paas/v4` with the capability tools wired. (A coding-plan key against the general endpoint is rejected with HTTP 429, which maps to `RateLimited`.) The static catalog (z.ai exposes no models-listing endpoint) IS the session's selectable lineup — the models a user picks between in the host's model picker. Capability tools surface only on z.ai-wired sessions in GeneralApi mode. | z.ai's API and model lineup are implementation details of the platform, not of the domain — any provider that can express the domain's message/tool language can be wired in the same way. |
| Exec ACL (Roslyn) | Runs model-authored `exec` programs and evidence checks via Roslyn C# scripting (`CSharpScriptExecEngine`, `CSharpEvidenceRunner`). The domain knows nothing about compilation or scripting internals; the engine resolves each script's `Workspace` through a host-supplied resolver (the session's workspace identity), never a process-global cwd. | The execution engine is an implementation detail of the platform, not of the domain. |
| File System ACL | All file I/O goes through a domain interface (`IFileSystemAccess`). The domain never touches `System.IO` directly. | Storage access is a capability the domain requests, not a technology it depends on. |
| Storage ACL | All persistence goes through this ACL (`AppDatabase`, `IStateStore`, `IAgentStore`, `ILearnedSkillStore`, `ICuratedMemoryStore`, `IAppPreferenceStore`, `ISelfDatabaseAccess`, `IWatchdogEventStore` — SQLite with versioned migrations + FTS5). `IAgentStore` persists agents, transcripts, and events; root rows carry a workspace + provider binding (discovery metadata for the Sessions catalog and resume — never a conversation scope; a workspace holds many sessions, and each conversation is keyed by its own agent id). `IAppPreferenceStore` holds host preferences — the last-chosen provider, the provider API keys (stored through the Desktop's `IApiKeyProtector`, never in plaintext), and the per-workspace model choice (a `model_choice:{provider}:{workspaceRoot}` key written by the Desktop's model picker); its interface lives in this ACL because app configuration has no bounded context of its own yet. `ISelfDatabaseAccess` (interface in the Tool Domain, like the file-system seams) backs the agent's read-only self-inspection tools `db_schema` / `db_query` — every statement runs on an `AppDatabase.OpenReadOnly()` connection, so no input can mutate the database. `IWatchdogEventStore` persists the per-session watchdog's append-only decision audit (`watchdog_events` table). | The storage engine is swappable; the domain never knows SQL exists. |
| Web ACL | Fetches web resources over HTTP(S) for the `web_fetch` tool (`HttpWebAccess`, `HtmlAgilityMarkdownConverter`). Follows redirects, caps body size, decodes per the declared charset, converts HTML to markdown over HtmlAgilityPack, and rejects binary content. The domain knows nothing about HTTP, HTML, or the converter library. | The web is an external system speaking its own formats; any fetch/conversion implementation can be wired in without domain changes. |


A special seam sits beside the ACLs: `eThangAgent.Provider.Wire`, the shared OpenAI-compatible streaming core (SSE framing, delta/tool-call/usage chunk application) consumed by both provider ACLs. It exists because that logic is byte-identical across OpenAI-compatible providers while living entirely outside the domain. The doctrine this refines: ACLs share no **domain** code; provider wire plumbing may live in a shared infrastructure project.

## Technology Stack & Constraints

- **Runtime**: .NET 10
- **Language**: C#
- **Platform**: Windows only — all path handling, process execution, and scripting assume Windows.
- **Exec / Scripting**: Roslyn C# scripting via the Exec ACL (`IExecEngine`) — no PowerShell anywhere in the solution. External processes (e.g., `git`) are spawned directly with native .NET `Process` APIs.
- **AI Providers**: OpenRouter and z.ai — the domain model speaks in provider-neutral concepts, and each provider's ACL implements them. Sessions are wired exclusively for one provider (chosen per agent in the desktop Open-Agent dialog); switching providers is a different experience by design.
- **Interface**: Desktop (Avalonia)

### Packaging & Wiring

- Each domain and ACL is its own project (package) for composability.
- Packages are designed to be swapped or extended — replacing an ACL implementation must never require touching domain code.
- Dependency injection wires implementations at the composition root; domains depend only on interfaces and contain zero DI container references.

## Domains

Each concern is owned by exactly one bounded context. When adding code, first ask which domain owns the concern — if the answer is "more than one" or "none", the boundaries need attention before code is written.

- **Agent Domain**: the core agent loop, conversation orchestration, tool dispatch, execution flow, and sub-agent spawning/runtime.
 A per-session watchdog (`AgentWatchdog`, application layer; pure policy in the Agent Domain) sweeps spawned children of its root each tick (default 60 s): a loop heartbeat (`IAgentHeartbeat`, beaten per iteration and around tool calls) drives hung detection; a first idle breach (default 15 min) cancels the run and restarts the SAME child id on a wrap-up nudge with its partial transcript preserved and re-hydrated; a second breach marks the child Failed(Hung); every decision is an append-only row in the `watchdog_events` table (`IWatchdogEventStore`, Storage ACL), which also carries observe-only app-RSS breach records (`IProcessMetrics`). The Desktop hosts one `WatchdogLoop` per process, attaching/detaching per open tab. Sessions ARE the domain's root agents: every turn persists its full message slice (user, assistant tool-call messages, tool results, nudges) losslessly through `IAgentStore`, and the host shell's **Sessions** menu (Desktop left-menu entry) lists persisted root sessions via the session catalog (`SessionCatalogQueryHandler`), newest first, greying out ones already open in a tab. Confirming a row resumes that session by id — the factory rebuilds the container on the session's ORIGINAL provider and workspace and hydrates the conversation from the persisted transcript, so the conversation continues where it stopped. Opening a workspace always mints a NEW session — resume is manual via the menu, never automatic for a directory. `AgentRecord`'s workspace/provider columns are discovery metadata only: they never scope conversation content, and unrelated sessions in one workspace share nothing conversational (only the deliberately workspace-scoped durable stores — state keys, curated memories, failover exclusions, picker preferences — carry across a workspace's sessions). The agent loop carries two optional collaborators wired via `AgentOptions`: `IContextMonitor` (per-provider-call usage accounting) and `IContextCompactor` (auto-compaction at the 80% utilization threshold — the oldest exchange groups are replaced by an LLM handoff summary; a compacted root turn persists by transcript replacement, not the append slice). Absent collaborators mean byte-identical legacy behavior.
- **Conversation Domain**: message history, message shapes (including tool calls and tool results), and conversation state. A `Conversation` can be seeded from a persisted transcript (resume hydration). `Conversation.Compact` replaces the whole message list behind aggregate invariants (non-empty, tool-call/result pairs intact, no dangling ids) — compaction summaries are System-role messages flagged `IsSummary`, serialized as ordinary system messages by both ACLs.
- **Tool Domain**: tool contracts, input validation, tool execution, tool result processing, and built-in tools — including the agent's read-only self-database inspection (`db_schema` / `db_query` over the `ISelfDatabaseAccess` seam, enforced by the Storage ACL's read-only connection).
- **Model Domain**: model capabilities, provider contracts, model configuration, and model+provider selection (`IModelSelector`, `IModelCatalog`, `IntelligentModelSelector`, `RootAgentResolver`, `ProviderFailoverResolver`, `SessionModelPreferences`). Context accounting lives here too: both ACLs report provider-scored `TokenUsage` on every response, `ModelConfig` carries the catalog's `ContextWindow` (resolved through `IContextWindowSource`; OpenRouter's catalog-less `openrouter/auto` routing pseudo-model uses a curated floor), and `ContextAccountant` (`IContextMonitor`) keeps utilization against that window — provider usage reports are the only decision input; character-based numbers are estimates for UI breakdowns only. Selection is per provider by design: OpenRouter runs automatic selection — a two-stage LLM pipeline picking on cost (after discount), latency, throughput, and capability scores (intelligence, coding, agentic), deferred to the first user prompt (not startup) and re-run every 10 user messages; z.ai runs none — its catalog is the static `glm-5.3` / `glm-5.3-flash` lineup and the resolver serves the fallback (`glm-5.3-flash`) every turn. The user overrides selection through the host's model picker (a Desktop left-menu entry: an **Auto** row plus a searchable catalog list on OpenRouter, the static lineup on z.ai); the choice lands in `SessionModelPreferences` and outranks automatic selection from the next turn, root and children alike, and the Desktop persists it per workspace + provider so reopening a directory restores it. There is no configured model pin — model choice is runtime-only. Failover re-selection excludes failed model+provider pairs via a SQLite-backed exclusion store with TTL. `SessionModelPreferences` carries runtime-adjustable knobs (reasoning effort and model, both via the left-menu pickers) that the root resolver and child spawner overlay onto resolved configs; the effort choice — model default or one of seven levels — works identically on both providers (each ACL maps it to its wire format: z.ai's `reasoning_effort`, OpenRouter's unified `reasoning.effort`) and is persisted + restored per workspace + provider the same way.
- **Capability Domain**: the registry that merges providers and exposes tools and capabilities to the model.
- **Memory Domain**: recall and search over persisted sessions (lexical and bounded-regex query planning), plus the curated-memory learning loop (categorized, tagged, full-text searchable, versioned).
- **Skill Domain**: the methodology-skill subsystem — embedded built-in skills (shipped verbatim) and agent-created learned skills, with version history and usage tracking.
- **State Domain**: durable, workspace-scoped key-value state, evidence-carrying transitions, and state events.

Configuration concerns live with their consumers until a real Configuration context earns its own boundary.

## Project Structure

The solution follows a layered DDD structure. Each bounded context has its own project with:

- `Domain` — aggregates, entities, value objects, domain events, specifications, repository interfaces
- `Application` — commands, queries, handlers, application services
- `Infrastructure` — repository implementations, ACL implementations, external service adapters
- `Contracts` — public interfaces, DTOs, and contracts shared across boundaries (kept minimal)

Shared kernel (cross-cutting concerns shared across domains):

- `SharedKernel` — base types, common value objects, guard clauses, result types

ACLs live in an `ACL` project each, implementing domain-owned interfaces.

### Naming Conventions

- Domain namespaces omit the dot: `eThangAgent.ToolDomain`, `eThangAgent.ConversationDomain`.
- ACL namespaces keep it: `eThangAgent.OpenRouter.ACL`, `eThangAgent.FileSystem.ACL`.

## Development Conventions

- **No shell intermediary**: repo automation is plain `dotnet` CLI invocations; do not commit `.ps1`/`.sh`/`.cmd`/`.bat` scripts. If a task needs scripting, prefer C# (the same language as the codebase).

- **Build**: `dotnet build`.
- **Formatting & analyzer severity**: enforced mechanically — `.editorconfig`, `Directory.Build.props`, and the CI lint job are the source of truth; this document deliberately does not restate their rules. Apply with `dotnet format whitespace eThangAgent.slnx` before committing (apply analyzer fixes only per rule, never in bulk — `dotnet format` without a subcommand also applies semantic analyzer fixes and can rewrite signatures), then verify with the exact CI lint gate before pushing: `dotnet format eThangAgent.slnx --verify-no-changes --severity warn`. That verify command is a strict superset of the whitespace subcommand — it also runs style and third-party analyzers (the JSON001–005 family, e.g. `Probable JSON string detected`), which never surface on `dotnet build` even with `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors`, because the family executes only in the format/IDE host and not the batch compiler; the verify run is their only automated net. (The signature-rewrite hazard is apply-direction only — `--verify-no-changes` writes nothing.) Sonar rules (SXXXX) are enforced at build time via `SonarAnalyzer.CSharp` (referenced in `Directory.Build.props`), mirroring the SonarQube Cloud profile: a finding must surface on `dotnet build`, not only after push. xUnit rules (xUXXX) are enforced at build time via `xunit.analyzers` (pinned in `tests/Directory.Build.props`; xunit.v3 3.2.x already pulls ≥ 1.27.0 transitively, so the pin exists to take 2.0.0's rule set ahead of the xunit.v3 4.x bump, which requires ≥ 2.0.0 and therefore conflicts with nothing). Named deviations live in `.editorconfig` (S1135, S125 off — their heuristics collide with this repo's prose) or targeted `#pragma warning` pairs at by-design sites (deliberate empty base records, provider base-URL constants, best-effort cleanup catches). Two carve-outs are named decisions, not leniency: CA1707 (xUnit `Method_Scenario_Expected` names) is suppressed in test projects only; CA2007 (`ConfigureAwait`) is suppressed in `eThangAgent.Desktop` only, because the UI SynchronizationContext is load-bearing — view-model continuations must resume on the UI thread. In test projects, awaits of same-assembly tasks use `ConfigureAwait(true)` in xUnit test methods and `ConfigureAwait(false)` in fake/helper classes (xUnit1030 forbids `false` in test methods).
- **Testing**: xUnit. Three layers: unit, integration, and E2E tests. Aim for 100% coverage; minimum 80% required. Unit tests use fakes only — a domain test must never know PowerShell, HTTP, or OpenRouter exist. Integration tests exercise real ACL implementations against real files / sandbox endpoints. E2E tests drive the desktop app headless — real composition behind the view-model — against a local mock provider server. Test selection runs on the xunit.v3 / Microsoft.Testing.Platform runner, not VSTest: `dotnet test --filter` is rejected (exit 5, help dump) — use `--filter-class` (fully-qualified class name), `--filter-method`, `--filter-trait`, or `--filter-query`; the runner also rejects `--no-build` and `--nologo` (exit 5, help dump) — plain `dotnet test <project> --filter-class <FQN>` is the working form; exit 2 means failures, exit 8 means zero tests matched.
- **Session retrospective**: at the end of every session/task, evaluate how *the agent itself* handled the session — bugs in its behavior, gaps in its tool surface, missing skills or workflow support — and include that evaluation in the summary to the user rather than letting it evaporate. The retrospective evaluates and targets the harness itself — a general agent that can be used on any repository, in any language, for multiple users — never the host repo: suggestions must target the agent (new tools, capabilities, prompts, reliability), never one-off fixes baked into a specific repo. A pattern that recurs across sessions or repos is a signal to promote it into a first-class agent capability.
- **Deadlock vigilance in tests**: a test that can wait forever eventually will. Any test exercising an unbounded or externally-settled await (channels, gates, `TaskCompletionSource`) must have a reachable completion path under the code under test's *current* contract — before running the suite, trace what settles the await. When a contract changes from bounded to unbounded (e.g. the clarify tool's no-timeout human wait), update every test that relied on the old bound in the same change, asserting the new contract directly rather than scripting the old one. A hung test blocks the whole suite and the agent itself; after fixing the cause, kill the orphaned `testhost` process before rerunning.
- **Every change leaves the build green**: a task is not done if the solution does not build and all tests pass.
- **Green includes a clean tree**: a task is not done while `git status --porcelain` shows output. Intended changes are committed; unintended ones are reverted or surfaced. Uncommitted work is unfinished work.
- **Dependency injection**: all wiring at the composition root — the host-agnostic `eThangAgent.Composition` library (`AgentComposition`/`AgentSessionFactory`), consumed by the Desktop host. Domains depend only on interfaces and contain zero DI container references.
- **Immutability**: domain models prefer immutability — records, init-only properties, copy constructors.
- **Error handling**: result types, not exceptions, for expected domain failures. Exceptions are for infrastructure/programmer errors.
- **Tool design**: tools demand strictly correct input (see Guiding Philosophy). Tool errors are returned to the model as tool results — an error is feedback for self-correction, never a turn-ending crash. Model-facing output uses explicit format contracts (annotation lines, gutters) documented verbatim in the tool description, so the model never has to guess what it is looking at.
- **Performance**: hot paths (file reads, shell execution) go through in-process hosting where possible; avoid per-call process spawns. Streaming over loading: read only the requested range, then account for the whole. Aim for event-driven flow over polling loops, and keep steady-state memory small — don't hold what you're not using. Maintain high performance, low latency, low memory, and a modular event-driven architecture across every change.
- **Create tools as you work**: when something useful is missing, add a tool for it rather than working around the gap. Tools are the agent's first-class surface — a missing capability is a missing tool, not a one-off script. New tools follow the existing `ITool` / capability-provider contracts: strict input validation, `Result<T>` errors, and verbatim format contracts in the description.
- **Prefer tools over exec scripts**: the Roslyn `exec` engine is for one-off glue, not a substitute for first-class capabilities. When the model keeps writing the same or similar scripts across sessions — repeated file probing, shell-out patterns, query shapes — that is a signal to promote the pattern into a general tool with proper validation and format contracts. The model should spend its effort on decisions, not on re-deriving boilerplate C#.
