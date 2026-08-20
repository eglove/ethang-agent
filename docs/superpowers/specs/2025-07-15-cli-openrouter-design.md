# CLI + OpenRouter ACL — Design Specification

**Date:** 2025-07-15
**Status:** Draft
**Milestone:** 1 — Prompt→Response REPL

## Goal

A CLI application that connects to OpenRouter for AI chat. The CLI and OpenRouter are behind Anti-Corruption Layers (ACLs) so the domain knows nothing about console I/O or OpenRouter's HTTP API.

## Project Structure

```
src/
├── eThangAgent.SharedKernel/          # Result<T>, Error, base types
├── eThangAgent.Conversation.Domain/   # Message, Conversation aggregate
├── eThangAgent.Model.Domain/          # ModelConfig, IModelProvider interface
├── eThangAgent.Agent.Domain/          # Agent aggregate
├── eThangAgent.Agent.Application/     # SendMessageCommand + Handler (CQRS)
├── eThangAgent.OpenRouter.ACL/        # IModelProvider implementation (HTTP)
└── eThangAgent.CLI/                   # Composition root, DI, REPL loop
```

### Dependency graph (top depends on below)

```
CLI → Agent.Application → Agent.Domain → { Conversation.Domain, Model.Domain }
CLI → OpenRouter.ACL → Model.Domain
CLI → SharedKernel (transitive)
```

### What's deliberately excluded (future milestones)

- Tool domain, Configuration domain, PowerShell ACL, FileSystem ACL
- Persistence — conversation is in-memory only
- Conversation history across sessions
- Multi-turn context (every prompt is standalone for now; messages accumulate in the Conversation aggregate but aren't sent as history to the model yet)

## Core Types

### SharedKernel

- **`Error`** — sealed record with `string Code` and `string Message`. Examples: `"ProviderTimeout"`, `"InvalidModel"`, `"RateLimited"`.
- **`Result<T>`** — discriminated union: `Success(T Value)` | `Failure(Error Error)`. Provides `Match`, `Map`, `Bind`. No exceptions for expected failures.

### Model.Domain

- **`ModelConfig`** — value object: `string ModelId`, `int MaxTokens`, `float Temperature`. Validates on construction.
- **`IModelProvider`** — domain interface (the ACL seam):

  ```csharp
  public interface IModelProvider
  {
      Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct);
  }
  ```

### Conversation.Domain

- **`Role`** — enum: `User`, `Assistant`.
- **`Message`** — value object: `Role Role`, `string Content`, `DateTimeOffset Timestamp`.
- **`Conversation`** — aggregate root:
  - `IReadOnlyList<Message> Messages`
  - `void AddUserMessage(string text)`
  - `void AddAssistantMessage(string text)`
- **`IConversationRepository`** — interface (in-memory for now, interface exists for future persistence).

### Agent.Domain

- **`Agent`** — aggregate root:
  - Holds a `Conversation` and a `ModelConfig`
  - `IModelProvider` injected via constructor
  - `Task<Result<string>> SendMessage(string text)`:
    1. `Conversation.AddUserMessage(text)`
    2. `IModelProvider.SendAsync(config, text)`
    3. On success: `Conversation.AddAssistantMessage(response)`, return `Result.Success(response)`
    4. On failure: propagate the `Result.Failure`

### Agent.Application

- **`SendMessageCommand`** — record: `string Text`.
- **`SendMessageCommandHandler`** — loads the `Agent` aggregate, calls `SendMessage`, returns `Result<string>`. No business logic — pure delegation.

### OpenRouter.ACL

- Implements `IModelProvider`.
- Uses `HttpClient` to POST to OpenRouter's `/api/v1/chat/completions`.
- Maps the OpenAI-compatible chat completion response to the raw completion text.
- Maps HTTP errors to domain `Error` codes: `"ProviderTimeout"`, `"RateLimited"`, `"ProviderError"`.
- Reads API key and base URL from configuration (injected, not hardcoded). Defaults to `https://openrouter.ai`; `OPENROUTER_BASE_URL` overrides it for testing with a mock server.

### CLI

- Composition root — wires all DI registrations.
- Reads OpenRouter API key from environment variable or config.
- A simple REPL loop: prompt → command → handler → print response or error → repeat.
- `/exit` to quit.

## Flow (one REPL interaction)

```
1. CLI reads user input
2. CLI → SendMessageCommandHandler.Handle(SendMessageCommand(text))
3. Handler → Agent.SendMessage(text)
4. Agent → Conversation.AddUserMessage(text)
5. Agent → IModelProvider.SendAsync(config, text)
6. [OpenRouter ACL → HTTP POST → OpenRouter API]
7. Agent → Conversation.AddAssistantMessage(response)
8. Handler returns Result<string>
9. CLI displays response or error
```

## Error Handling

- All expected failures use `Result<T>`, never exceptions.
- OpenRouter ACL catches `HttpRequestException` / non-2xx → `Result.Failure` with domain error codes.
- `Agent.SendMessage` propagates results — no catch.
- CLI displays errors inline: `Error [ProviderTimeout]: The request timed out.` and continues the REPL.
- Exceptions reserved for programmer errors only (null refs, DI misconfigurations) → crash.

## Testing Strategy

| Layer | What | How |
| ------- | ------ | ----- |
| SharedKernel | Result<T> match/map/bind | Unit tests |
| Model.Domain | ModelConfig validation | Unit tests |
| Conversation.Domain | Message ordering, AddUserMessage | Unit tests |
| Agent.Domain | SendMessage with fake IModelProvider: success, failure, conversation state | Unit tests |
| Agent.Application | Handler delegation — returns result from agent | Unit tests with fakes |
| OpenRouter.ACL | Real HTTP to OpenRouter with cheap model | Integration tests |
| CLI | `dotnet run` — type a prompt, see response | E2E test |

Key invariant: Agent.Domain tests never know OpenRouter exists — they use a fake `IModelProvider`. The OpenRouter ACL is tested with a fake HttpMessageHandler — authentic HTTP pipeline, no network. The E2E test runs a local mock HTTP server in-process so the CLI exercises its full HTTP path but never hits real OpenRouter.
