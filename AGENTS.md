# eThang Agent

## Project Overview

eThang Agent is an AI agent built with .NET. It will initially be a CLI-only application with a web UI planned for a future phase. The project follows strict Domain-Driven Design (DDD) with proper layers, Specification patterns, and CQRS.

## Architecture Principles

### Domain-Driven Design

The codebase is organized around business domains, not technical concerns. Each domain owns its models, logic, and boundaries. Domains communicate through well-defined contracts — never by reaching into another domain's internals.

- **Ubiquitous Language**: domain models and code reflect the language of the problem space (agents, conversations, tools, providers, etc.).
- **Bounded Contexts**: each domain has clear boundaries and ownership.
- **Aggregates**: consistency boundaries are enforced through aggregate roots.
- **Domain Events**: side effects across domains are communicated through events, not direct calls.

### CQRS (Command Query Responsibility Segregation)

Commands and queries are separated:

- **Commands** mutate state and do not return data. They go through domain logic, validation, and specification evaluation.
- **Queries** return data and have no side effects. They bypass domain logic and use optimized read models where beneficial.

### Specification Pattern

Business rules and validation logic are encapsulated in composable Specification objects. Specifications can be combined (and, or, not) and are evaluated against domain objects before mutations are applied.

### Anti-Corruption Layers (ACLs)

Any external system or platform-specific concern is isolated behind an Anti-Corruption Layer. The domain never depends directly on external SDKs, platform APIs, or infrastructure. ACLs translate between the domain's ubiquitous language and the external system's concepts.

**Planned ACLs:**

| ACL | Purpose | Future Alternatives |
| ----- | --------- | --------------------- |
| OpenRouter ACL | Translate domain concepts (messages, models, tokens) to/from OpenRouter's API. The domain knows nothing about OpenRouter-specific types, endpoints, or authentication. | Anthropic direct, OpenAI direct, local models |
| PowerShell ACL | All shell execution goes through this ACL. The domain never calls Process.Start, System.Management.Automation, or shell commands directly. | Bash/Linux support, direct process management |
| File System ACL | Abstract file I/O behind a domain interface. | Different storage backends, cloud storage |
| Web UI ACL (future) | Translate between HTTP/WebSocket concerns and the domain's command/query/event contracts. | Different frontend frameworks, API protocols |

## Technology Stack & Constraints

### Current

- **Runtime**: .NET 10
- **Language**: C#
- **Platform**: Windows only
- **Shell**: PowerShell (all scripting, automation, and tool execution)
- **AI Provider**: OpenRouter only — leverage OpenRouter-specific APIs and features (model routing, cost tracking, provider fallback, etc.)
- **Interface**: CLI only

### Packages

- Each domain and ACL is its own package (project) for high composability.
- Packages are designed to be swapped or extended — e.g., the OpenRouter ACL can be replaced with an Anthropic ACL without touching any domain code.
- Dependency injection wires implementations at composition root; domains depend only on interfaces.

### Constraints

- No Linux support yet. All path handling, process execution, and scripting assumes Windows.
- PowerShell is the only shell. No bash scripts, no cmd fallbacks.
- OpenRouter is the only provider. The domain model may anticipate multi-provider concepts, but only OpenRouter is implemented.

## Domain Boundaries (Initial)

These are the anticipated domains — not a final plan, but a sketch of the bounded contexts:

- **Agent Domain**: the core agent loop, conversation orchestration, tool selection, and execution flow.
- **Conversation Domain**: message history, context windows, token management, and conversation state.
- **Tool Domain**: tool definitions, tool execution, tool result processing, and built-in tools.
- **Model Domain**: model capabilities, pricing, routing preferences, and provider selection (OpenRouter-specific initially).
- **Configuration Domain**: agent configuration, model settings, tool enablement, and user preferences.

## Project Structure

The solution follows a layered DDD structure. Each bounded context has its own project with:

- `Domain` — aggregates, entities, value objects, domain events, specifications, repository interfaces
- `Application` — commands, queries, handlers, application services
- `Infrastructure` — repository implementations, ACL implementations, external service adapters
- `Contracts` — public interfaces, DTOs, and contracts shared across boundaries (kept minimal)

Shared kernel (cross-cutting concerns shared across domains):

- `SharedKernel` — base types, common value objects, guard clauses, result types, maybe monads

ACLs are placed in an `ACL` folder/namespace, each in its own project.

## Development Conventions

- **All scripts are PowerShell** (`.ps1`). No `.sh`, no `.cmd`, no `.bat`.
- **Build**: `dotnet build` from PowerShell.
- **Testing**: xUnit. Three layers: unit, integration, and E2E tests. Aim for 100% coverage; minimum 80% coverage required. Integration tests should exercise real ACL implementations against sandbox/test endpoints. E2E tests exercise full CLI workflows end-to-end.
- **Dependency injection**: all wiring at the composition root (host/CLI project). Domain projects have zero DI container references.
- **Immutability**: domain models prefer immutability. Use records, init-only properties, and copy constructors.
- **Error handling**: use result types, not exceptions, for expected domain failures. Exceptions are for infrastructure/programmer errors.

## Future Directions

These are not planned yet — but the architecture must not preclude them:

- **Web UI**: a web frontend that consumes the same command/query/event contracts as the CLI.
- **Linux support**: the PowerShell ACL and File System ACL are designed to be swapped for Linux-compatible implementations.
- **Additional AI providers**: the OpenRouter ACL's interface is generic enough to support direct Anthropic, OpenAI, or local model providers.
- **Persistence**: conversation history, agent state, and configuration stored in a database.
- **Multi-agent orchestration**: multiple agents collaborating on tasks.
