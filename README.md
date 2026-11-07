# eThang Agent

eThang Agent is an AI agent harness built with .NET and delivered through an Avalonia desktop application.

This document is the project's constitution. It states what the project is for, how it is meant to be built, and the conventions every change is held to. It describes intent, never implementation: what the code contains, which designs were chosen, and how any subsystem works are answered by the code, which is the only source of truth for them.

## Overall Goal

eThang Agent exists to make model quality stop mattering.

The harness is not a developer tool. It is a stakeholder tool: anyone who can state what should be true can use it, and everyone who uses it becomes a stakeholder in the outcome. Implementation details — decomposition, tool selection, sequencing, verification, recovery — belong to the harness and the model, not to the person directing the work.

Three commitments follow:

1. **Any work is doable by any model.** Complex work is decomposed until none of it requires smartness. The least intelligent model can complete complex tasks because the harness supplies the structure: skills for matched procedures, the scientific method for non-trivial work, verification gates, and tool contracts that make the next step obvious.
2. **Judgment is the only scarce resource.** The one thing the harness cannot supply is judgment about what the constitution should say — what the work is, why it matters, and when it is done. That remains with the user.
3. **The harness is the orchestrator.** Agents do not supervise other agents. A model is asked to do work, not to decide what runs next, in what order, or when another agent should start — that scheduling belongs to the harness, driven by the records the work itself produces. One agent blocking on another's outcome is orchestration by another name, and it is the harness's job instead.

In short: the user decides what should be true. The harness figures out how to make it true. The model does the work the harness hands it, and only that.

## Guiding Philosophy

These principles explain every convention below. When a rule seems arbitrary, it traces back to one of these.

- **Seams over commitments.** Every external dependency — AI provider, shell, file system, UI — sits behind an interface the domain owns. Nothing external is ever allowed to become load-bearing inside a domain. Any implementation behind a seam must be replaceable without touching domain code.
- **Strict correctness at the boundaries.** Inputs crossing into the domain are validated completely: required parameters are required, types are exact, unknown input is rejected, and nothing is silently coerced, defaulted, or clamped. An exception to a strict contract is acceptable only as a named decision that proves the rule rather than a default that quietly widens it, and the change that introduces it carries the reasoning.
- **Errors are information, not crashes.** Expected failures flow through `Result<T>` / error values and are delivered to whoever can act on them — including the model itself, which receives tool errors as tool results so it can self-correct. Exceptions are reserved for programmer error.
- **Reversibility by construction.** The architecture must not preclude alternative frontends, platforms, providers, storage engines, or orchestration topologies. When a choice would foreclose an option, choose the seam instead.
- **The codebase is not the standard.** Existing code records what was done, not what is right. Never treat a current pattern as best practice just because it is present — when work touches code and a better way exists, improve it in the same change. Improve what you touch; do not launch unrelated refactors.
- **Lean, event-driven operations.** Aim for high performance and a small memory footprint. Prefer event-driven designs — react to what happened instead of polling, stream instead of loading, and don't hold what you're not using.
- **Direction: a self-contained agent.** The long-range shape is a persistent desktop application that manages its own world — conversations, sessions, state, configuration — in app-owned structured storage rather than scattered project-level files. Work written today must fit that destination: durable state flows through domain-owned stores, never ad-hoc files.
- **The database is the only artifact.** The harness adds nothing to the user's machine outside its own database. No config files, no marker files, no artifact directories, no scratch files, no caches on disk — nothing the user has to find, understand, clean up, or keep in sync, and nothing that appears in a directory the harness was merely pointed at. A workspace is identified by its directory path and nothing else, so opening one writes nothing into it. Anything that would otherwise become a file is either a row or does not exist: a generated image is delivered to the transcript rather than saved beside the user's work, and a setting is a preference row rather than a file beside the executable. This is a constraint on every feature, not a preference: if a design needs a file on the user's machine, the design is wrong.
- **Mechanical solutions first.** Harness features should prefer mechanical solutions, or mechanical checks as a supplement to model actions, wherever possible. A model action should be the fallback, not the default — when a task can be done by a tool, a validation, a specification, or an automated gate, build that instead of asking the model to do it by hand.

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

### Anti-Corruption Layers

Every external system — AI provider, shell, file system, storage engine, transport, desktop surface, network, document format — sits behind an Anti-Corruption Layer implementing an interface the domain owns. The layer translates between the domain's ubiquitous language and the external system's concepts, so no external SDK type, wire format, or platform API is ever load-bearing inside a domain.

An ACL exists because its seam earns its keep. ACLs share no domain code with each other. Infrastructure that several providers genuinely share — provider wire plumbing, for instance — may live in its own infrastructure project, because shared infrastructure is not shared domain.

## Technology Stack & Constraints

- **Runtime**: .NET 10.
- **Language**: C#.
- **Platform**: Windows only — all path handling, process execution, and scripting assume Windows.
- **Exec / Scripting**: Roslyn C# scripting. There is no PowerShell anywhere in the solution, and external processes are spawned directly with native .NET `Process` APIs rather than through a shell.
- **AI Providers**: OpenRouter. The domain model speaks in provider-neutral concepts and each provider's ACL implements them; a session is wired to one provider, and switching providers is a different experience by design.
- **Interface**: Desktop (Avalonia).

### Packaging & Wiring

- Each domain and each ACL is its own project, so any implementation can be swapped without touching domain code.
- Dependency injection wires implementations at the composition root. Domains depend only on interfaces and contain no DI container references.

## Development Conventions

- **No shell intermediary**: repo automation is plain `dotnet` CLI invocations. Do not commit `.ps1`/`.sh`/`.cmd`/`.bat` scripts; when a task needs scripting, write C#.
- **Build**: `dotnet build`.
- **Formatting & analyzers**: enforced mechanically. `.editorconfig`, `Directory.Build.props`, and the CI lint job are the source of truth, and this document deliberately does not restate their rules. Apply with `dotnet format whitespace eThangAgent.slnx`, then verify with `dotnet format eThangAgent.slnx --verify-no-changes --severity warn` before pushing — that verify run is a strict superset of the whitespace subcommand, and some analyzer families only execute in the format host and never surface on `dotnet build`.
- **Testing**: xUnit, in three layers. Unit tests use fakes only — a domain test must never know that HTTP or a provider exists. Integration tests exercise real ACL implementations against real files and sandbox endpoints. E2E tests drive the desktop app headless, with the real composition behind the view-model, against a local mock provider server. Aim for 100% coverage; 80% is the minimum.
- **Test selection**: the runner is Microsoft.Testing.Platform, not VSTest. `dotnet test --filter` is rejected — use `--filter-class`, `--filter-method`, `--filter-trait`, or `--filter-query`.
- **The fast loop is a documented command**: the suite is split by trait, and the split is what makes an edit cheap. A test that starts a process, opens a listener, or builds a composed container carries `[Trait("Category", "Process")]`; everything else does not, so the fast command is a negative filter over the whole solution:

  ```
  dotnet test eThangAgent.slnx --max-parallel-test-modules 4 --filter-not-trait Category=Process
  ```

  Measured on a 24-core machine with the build warm: 5,882 tests in 24 s, against 2 m 37 s for the whole suite. The tier is a boundary, not a label of convenience — a process test that lacks the trait runs on every edit, and a pure test that carries it silently stops running in the fast loop at all. `DoctrineTests` rejects both directions, so the boundary cannot drift: adding a test that spawns something fails the build until it is tagged, and tagging a test that spawns nothing fails it too.
- **The loaded shape is a documented command**: the suite runs as three commands, and CI runs each as its own job. `eThangAgent.Batch.slnf` is every test project except the two heavyweight ones, which run on their own so they stop competing with the other 24 modules for the machine:

  ```
  dotnet test --solution eThangAgent.Batch.slnf --max-parallel-test-modules 4
  dotnet test tests/eThangAgent.Composition.Tests
  dotnet test tests/eThangAgent.Storage.ACL.Tests
  ```

  Module parallelism is pinned explicitly rather than left at the platform default, so the load a test's ceiling has to survive is a property of the harness instead of a guess inside each test. Measured on a 24-core machine, the whole suite in one process costs 2m 34s; the split's longest command is the batch, at 2m 00s, and Composition costs 1m 20s and Storage 9s on their own. The gain is not the arithmetic — three processes sharing one saturated machine measured no faster than one — it is that each command runs on its own runner in CI, where the two heavyweight projects stop competing with 24 other modules for cores. `DoctrineTests` holds the filter to the solution, so a new test project cannot join the suite without joining one of the three commands.
- **A wait is a ceiling, never a latency contract**: `TestCeiling.Value` (Tests.TestSupport) is the only wall-clock bound a test may hold, it is set high enough that a loaded machine cannot reach it, and it exists so a broken test fails instead of hanging. A test whose subject really is latency declares it with `[Trait("Category", "LatencyContract")]`. `DoctrineTests` enforces both halves.
- **A test runs or is deleted**: a skipped test reports green while asserting nothing. The one exception is an opt-in test — `[Trait("Category", "OptIn")]` plus `OptInTest.SkipUnlessEnabled`, which names the environment variable that turns it on. `DoctrineTests` rejects any other skip.
- **Deadlock vigilance in tests**: a test that can wait forever eventually will. Any test exercising an unbounded or externally-settled await (channels, gates, `TaskCompletionSource`) must have a reachable completion path under the code under test's *current* contract — before running the suite, trace what settles the await. When a contract changes from bounded to unbounded, update every test that relied on the old bound in the same change, asserting the new contract directly rather than scripting the old one. A hung test blocks the whole suite and the agent itself; after fixing the cause, kill the orphaned `testhost` process before rerunning.
- **Never kill a running `eThangAgent.ChildHost` process.** Every agent session runs in its own conversation host, so a host process is a live session — the user's, another agent's, or the one running the command. Killing by image name takes all of them, and no situation makes that the right move. A host reported by `ChildHostLeakGuardFixture` is alive at teardown, and the report names its pid; it is either a test that failed to dispose its session (the fix belongs in that test) or a session that was disposed whose host outlived the bounded teardown wait on a loaded machine. The check observes a presence, not a cause. A host that blocks a rerun is surfaced to the user, who decides.
- **Every change leaves the build green**: a task is not done if the solution does not build and all tests pass.
- **Green includes a clean tree**: a task is not done while `git status --porcelain` shows output. Intended changes are committed; unintended ones are reverted or surfaced. Uncommitted work is unfinished work.
- **Comments describe code, never decisions.** A comment may state what the code does and what it guarantees — preconditions, units, invariants, non-obvious mechanics. It must never record why a design was chosen, what was rejected, which issue or incident prompted a change, or what a future editor is required to do. Design decisions live in the project's history; directives live in the build, where a test or an analyzer enforces them. Comments already in the codebase that carry a decision, a rationale, or a directive are legacy: they are removed, not preserved and not updated.
- **Immutability**: domain models prefer immutability — records, init-only properties, copy constructors.
- **Error handling**: result types, not exceptions, for expected domain failures. Exceptions are for infrastructure and programmer error.
- **Tool design**: tools demand strictly correct input, and a tool error comes back to the model as a tool result so it can self-correct — never as a turn-ending crash. Model-facing output uses explicit format contracts documented verbatim in the tool's description, so the model never has to guess what it is looking at.
- **Performance**: hot paths go through in-process hosting where possible and per-call process spawns are avoided. Stream over loading: read the requested range, then account for the whole. Prefer event-driven flow to polling, and keep steady-state memory small — don't hold what you're not using.
- **Create tools as you work**: when something useful is missing, add a tool for it rather than working around the gap. Tools are the agent's first-class surface, and a missing capability is a missing tool, not a one-off script.
- **Prefer tools over scripts**: one-off glue is what scripting is for. When the same or a similar script keeps being written across sessions — file probing, shell-out patterns, query shapes — that is a signal to promote the pattern into a tool with proper validation and format contracts. The model should spend its effort on decisions, not on re-deriving boilerplate.
- **Session retrospective**: at the end of every session, evaluate how *the agent itself* handled the session — bugs in its behavior, gaps in its tool surface, missing skills or workflow support — and include that evaluation in the summary to the user rather than letting it evaporate. The retrospective targets the harness, which is a general agent for any repository in any language, never the host repo: suggestions must name a capability, not a one-off fix. It also places the session against this document — how the work and the agent's own conduct moved the project toward the [Overall Goal](#overall-goal), honored or strained the [Guiding Philosophy](#guiding-philosophy), and followed or bypassed the [Architecture Principles](#architecture-principles) — and names the concrete site wherever the work drifted: a seam bypassed for an implementation detail, a mechanic left to the model where a tool or a gate belongs, state written to an ad-hoc file where an owned store belongs, a comment that promises what the code does not do. Restating a principle is not a finding; a principle with nothing to say this session gets one line saying so.
