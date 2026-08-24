# eThang Agent

> **Keep this document current**: AGENTS.md describes how the system works *today*, not how it was built. When a change makes any statement here stale — an ACL replaced, a constraint dropped, a convention changed — update this file in the same change. Verify claims against the code before relying on them.

## What This Project Is

eThang Agent is an AI agent built with .NET, delivered through an Avalonia desktop application. The project follows strict Domain-Driven Design: layered bounded contexts, Specification patterns, and CQRS.

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
| OpenRouter ACL | Translates domain concepts (messages, models, tool calls) to/from OpenRouter's API. The domain knows nothing about OpenRouter-specific types, endpoints, or authentication. | The domain speaks its own message/tool language, so any provider that can express it can be wired in without domain changes. |
| Exec ACL (Roslyn) | Runs model-authored `exec` programs and evidence checks via Roslyn C# scripting (`CSharpScriptExecEngine`, `CSharpEvidenceRunner`). The domain knows nothing about compilation or scripting internals. | The execution engine is an implementation detail of the platform, not of the domain. |
| File System ACL | All file I/O goes through a domain interface (`IFileSystemAccess`). The domain never touches `System.IO` directly. | Storage access is a capability the domain requests, not a technology it depends on. |
| Storage ACL | All persistence goes through this ACL (`AppDatabase`, `IStateStore`, `IAgentStore`, `ILearnedSkillStore`, `ICuratedMemoryStore` — SQLite with versioned migrations + FTS5). | The storage engine is swappable; the domain never knows SQL exists. |

## Technology Stack & Constraints

- **Runtime**: .NET 10
- **Language**: C#
- **Platform**: Windows only — all path handling, process execution, and scripting assume Windows.
- **Exec / Scripting**: Roslyn C# scripting via the Exec ACL (`IExecEngine`) — no PowerShell anywhere in the solution. External processes (e.g., `git`) are spawned directly with native .NET `Process` APIs.
- **AI Provider**: OpenRouter — the domain model speaks in provider-neutral concepts, and only the OpenRouter ACL implements them.
- **Interface**: Desktop (Avalonia)

### Packaging & Wiring

- Each domain and ACL is its own project (package) for composability.
- Packages are designed to be swapped or extended — replacing an ACL implementation must never require touching domain code.
- Dependency injection wires implementations at the composition root; domains depend only on interfaces and contain zero DI container references.

## Domains

Each concern is owned by exactly one bounded context. When adding code, first ask which domain owns the concern — if the answer is "more than one" or "none", the boundaries need attention before code is written.

- **Agent Domain**: the core agent loop, conversation orchestration, tool dispatch, execution flow, and sub-agent spawning/runtime.
- **Conversation Domain**: message history, message shapes (including tool calls and tool results), and conversation state.
- **Tool Domain**: tool contracts, input validation, tool execution, tool result processing, and built-in tools.
- **Model Domain**: model capabilities, provider contracts, and model configuration.
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
- **Release builds while developing**: a running desktop app locks `bin/Debug`, so builds/tests against it fail with MSB3021/MSB3027 — use `-c Release` whenever the app is running.
- **Testing**: xUnit. Three layers: unit, integration, and E2E tests. Aim for 100% coverage; minimum 80% required. Unit tests use fakes only — a domain test must never know PowerShell, HTTP, or OpenRouter exist. Integration tests exercise real ACL implementations against real files / sandbox endpoints. E2E tests drive the desktop app headless — real composition behind the view-model — against a local mock provider server.
- **Session retrospective**: at the end of every session/task, evaluate the session for bugs encountered, improvements worth making, and new tools or skills that could be built into the agent — then act on what is worth acting on (file it, fix it, or build it), rather than letting it evaporate.
- **Every change leaves the build green**: a task is not done if the solution does not build and all tests pass.
- **Dependency injection**: all wiring at the composition root (the Desktop host project).
- **Immutability**: domain models prefer immutability — records, init-only properties, copy constructors.
- **Error handling**: result types, not exceptions, for expected domain failures. Exceptions are for infrastructure/programmer errors.
- **Tool design**: tools demand strictly correct input (see Guiding Philosophy). Tool errors are returned to the model as tool results — an error is feedback for self-correction, never a turn-ending crash. Model-facing output uses explicit format contracts (annotation lines, gutters) documented verbatim in the tool description, so the model never has to guess what it is looking at.
- **Performance**: hot paths (file reads, shell execution) go through in-process hosting where possible; avoid per-call process spawns. Streaming over loading: read only the requested range, then account for the whole. Aim for event-driven flow over polling loops, and keep steady-state memory small — don't hold what you're not using. Maintain high performance, low latency, low memory, and a modular event-driven architecture across every change.
- **Create tools as you work**: when something useful is missing, add a tool for it rather than working around the gap. Tools are the agent's first-class surface — a missing capability is a missing tool, not a one-off script. New tools follow the existing `ITool` / capability-provider contracts: strict input validation, `Result<T>` errors, and verbatim format contracts in the description.
- **Prefer tools over exec scripts**: the Roslyn `exec` engine is for one-off glue, not a substitute for first-class capabilities. When the model keeps writing the same or similar scripts across sessions — repeated file probing, shell-out patterns, query shapes — that is a signal to promote the pattern into a general tool with proper validation and format contracts. The model should spend its effort on decisions, not on re-deriving boilerplate C#.
