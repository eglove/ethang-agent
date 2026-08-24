# CLI + OpenRouter ACL — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET CLI application that connects to OpenRouter for AI chat, with the CLI and provider behind Anti-Corruption Layers.

**Architecture:** Seven .NET projects in DDD layers — SharedKernel, three domain packages (Model, Conversation, Agent), an Agent.Application CQRS layer, an OpenRouter.ACL implementing the IModelProvider seam, and a CLI composition root with a REPL loop. Dependencies flow inward; domains never depend on infrastructure.

**Tech Stack:** .NET 10, C#, xUnit (3 test layers: unit, integration, E2E), Microsoft.Extensions.DependencyInjection, HttpClient (OpenRouter ACL)

**Spec:** `docs/skills/specs/2025-07-15-cli-openrouter-design.md`

## Global Constraints

- .NET 10 (SDK 10.0.400 verified)
- C# with nullable enabled, implicit usings enabled
- All expected failures use `Result<T>`; exceptions only for programmer errors
- Each domain and ACL is its own .csproj
- Tests use xUnit; 3 layers: unit (fake dependencies), integration (mocked HTTP transport via FakeHttpMessageHandler), E2E (process spawn against a local mock HTTP server). Never hit real OpenRouter endpoints in tests.
- Dependency injection wired at CLI composition root only

---

## Task 1: Project Scaffold

**Files:**

- Create: `Directory.Build.props`
- Create: `tests/Directory.Build.props`
- Create: `src/eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj`
- Create: `src/eThangAgent.Model.Domain/eThangAgent.Model.Domain.csproj`
- Create: `src/eThangAgent.Conversation.Domain/eThangAgent.Conversation.Domain.csproj`
- Create: `src/eThangAgent.Agent.Domain/eThangAgent.Agent.Domain.csproj`
- Create: `src/eThangAgent.Agent.Application/eThangAgent.Agent.Application.csproj`
- Create: `src/eThangAgent.OpenRouter.ACL/eThangAgent.OpenRouter.ACL.csproj`
- Create: `src/eThangAgent.CLI/eThangAgent.CLI.csproj`
- Create: `tests/eThangAgent.SharedKernel.Tests/eThangAgent.SharedKernel.Tests.csproj`
- Create: `tests/eThangAgent.Model.Domain.Tests/eThangAgent.Model.Domain.Tests.csproj`
- Create: `tests/eThangAgent.Conversation.Domain.Tests/eThangAgent.Conversation.Domain.Tests.csproj`
- Create: `tests/eThangAgent.Agent.Domain.Tests/eThangAgent.Agent.Domain.Tests.csproj`
- Create: `tests/eThangAgent.Agent.Application.Tests/eThangAgent.Agent.Application.Tests.csproj`
- Create: `tests/eThangAgent.OpenRouter.ACL.Tests/eThangAgent.OpenRouter.ACL.Tests.csproj`
- Create: `tests/eThangAgent.CLI.Tests/eThangAgent.CLI.Tests.csproj`
- Modify: `eThangAgent.slnx`

**Interfaces:**

- Produces: all project directories, `.csproj` files, solution references

- [ ] **Step 1: Create shared Directory.Build.props (src/)**

Create `Directory.Build.props` at repo root:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create tests Directory.Build.props**

Create `tests/Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create all src/ .csproj files**

Create directories with `mkdir -p`, then each `.csproj`.

`src/eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
</Project>
```

`src/eThangAgent.Model.Domain/eThangAgent.Model.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\eThangAgent.SharedKernel\eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

`src/eThangAgent.Conversation.Domain/eThangAgent.Conversation.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\eThangAgent.SharedKernel\eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

`src/eThangAgent.Agent.Domain/eThangAgent.Agent.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\eThangAgent.SharedKernel\eThangAgent.SharedKernel.csproj" />
    <ProjectReference Include="..\eThangAgent.Model.Domain\eThangAgent.Model.Domain.csproj" />
    <ProjectReference Include="..\eThangAgent.Conversation.Domain\eThangAgent.Conversation.Domain.csproj" />
  </ItemGroup>
</Project>
```

`src/eThangAgent.Agent.Application/eThangAgent.Agent.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\eThangAgent.Agent.Domain\eThangAgent.Agent.Domain.csproj" />
  </ItemGroup>
</Project>
```

`src/eThangAgent.OpenRouter.ACL/eThangAgent.OpenRouter.ACL.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\eThangAgent.Model.Domain\eThangAgent.Model.Domain.csproj" />
    <ProjectReference Include="..\eThangAgent.SharedKernel\eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

`src/eThangAgent.CLI/eThangAgent.CLI.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.*" />
    <ProjectReference Include="..\eThangAgent.Agent.Application\eThangAgent.Agent.Application.csproj" />
    <ProjectReference Include="..\eThangAgent.OpenRouter.ACL\eThangAgent.OpenRouter.ACL.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create all tests/ .csproj files**

Create directories with `mkdir -p`, then each test `.csproj`. Each references its corresponding src project.

`tests/eThangAgent.SharedKernel.Tests/eThangAgent.SharedKernel.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.SharedKernel\eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Model.Domain.Tests/eThangAgent.Model.Domain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.Model.Domain\eThangAgent.Model.Domain.csproj" />
    <ProjectReference Include="..\..\src\eThangAgent.SharedKernel\eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Conversation.Domain.Tests/eThangAgent.Conversation.Domain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.Conversation.Domain\eThangAgent.Conversation.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Agent.Domain.Tests/eThangAgent.Agent.Domain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.Agent.Domain\eThangAgent.Agent.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Agent.Application.Tests/eThangAgent.Agent.Application.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.Agent.Application\eThangAgent.Agent.Application.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.OpenRouter.ACL.Tests/eThangAgent.OpenRouter.ACL.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.OpenRouter.ACL\eThangAgent.OpenRouter.ACL.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.CLI.Tests/eThangAgent.CLI.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\eThangAgent.CLI\eThangAgent.CLI.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Update .slnx to reference all projects**

Read current `eThangAgent.slnx`, then replace with all project references:

```xml
<Solution>
  <Project Path="src/eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
  <Project Path="src/eThangAgent.Model.Domain/eThangAgent.Model.Domain.csproj" />
  <Project Path="src/eThangAgent.Conversation.Domain/eThangAgent.Conversation.Domain.csproj" />
  <Project Path="src/eThangAgent.Agent.Domain/eThangAgent.Agent.Domain.csproj" />
  <Project Path="src/eThangAgent.Agent.Application/eThangAgent.Agent.Application.csproj" />
  <Project Path="src/eThangAgent.OpenRouter.ACL/eThangAgent.OpenRouter.ACL.csproj" />
  <Project Path="src/eThangAgent.CLI/eThangAgent.CLI.csproj" />
  <Project Path="tests/eThangAgent.SharedKernel.Tests/eThangAgent.SharedKernel.Tests.csproj" />
  <Project Path="tests/eThangAgent.Model.Domain.Tests/eThangAgent.Model.Domain.Tests.csproj" />
  <Project Path="tests/eThangAgent.Conversation.Domain.Tests/eThangAgent.Conversation.Domain.Tests.csproj" />
  <Project Path="tests/eThangAgent.Agent.Domain.Tests/eThangAgent.Agent.Domain.Tests.csproj" />
  <Project Path="tests/eThangAgent.Agent.Application.Tests/eThangAgent.Agent.Application.Tests.csproj" />
  <Project Path="tests/eThangAgent.OpenRouter.ACL.Tests/eThangAgent.OpenRouter.ACL.Tests.csproj" />
  <Project Path="tests/eThangAgent.CLI.Tests/eThangAgent.CLI.Tests.csproj" />
</Solution>
```

- [ ] **Step 6: Verify scaffold builds**

Run: `dotnet build`
Expected: all 14 projects restore and build with 0 errors (warnings OK at this stage since no source files exist)

- [ ] **Step 7: Commit**

```bash
git add Directory.Build.props tests/Directory.Build.props src/ tests/ eThangAgent.slnx
git commit -m "feat: add project scaffold with DDD layer structure"
```

---

## Task 2: SharedKernel — Error and Result<T>

**Files:**

- Create: `src/eThangAgent.SharedKernel/Error.cs`
- Create: `src/eThangAgent.SharedKernel/Result.cs`
- Create: `tests/eThangAgent.SharedKernel.Tests/ErrorTests.cs`
- Create: `tests/eThangAgent.SharedKernel.Tests/ResultTests.cs`

**Interfaces:**

- Produces: `Error` sealed record (`string Code`, `string Message`), `Result<T>` class with `Success` / `Failure` factories and `Match` / `Map` / `Bind` methods

- [ ] **Step 1: Write failing test for Error**

Create `tests/eThangAgent.SharedKernel.Tests/ErrorTests.cs`:

```csharp
namespace eThangAgent.SharedKernel.Tests;

public class ErrorTests
{
    [Fact]
    public void Error_HoldsCodeAndMessage()
    {
        var error = new Error("TEST_CODE", "A test message");
        Assert.Equal("TEST_CODE", error.Code);
        Assert.Equal("A test message", error.Message);
    }

    [Fact]
    public void Error_Equal_WhenCodeAndMessageMatch()
    {
        var a = new Error("X", "msg");
        var b = new Error("X", "msg");
        Assert.Equal(a, b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/eThangAgent.SharedKernel.Tests --filter "FullyQualifiedName~ErrorTests"`
Expected: FAIL (type `Error` not found)

- [ ] **Step 3: Implement Error**

Create `src/eThangAgent.SharedKernel/Error.cs`:

```csharp
namespace eThangAgent.SharedKernel;

public sealed record Error(string Code, string Message);
```

- [ ] **Step 4: Run Error tests to verify pass**

Run: `dotnet test tests/eThangAgent.SharedKernel.Tests --filter "FullyQualifiedName~ErrorTests"`
Expected: 2 passed

- [ ] **Step 5: Write failing tests for Result<T>**

Create `tests/eThangAgent.SharedKernel.Tests/ResultTests.cs`:

```csharp
namespace eThangAgent.SharedKernel.Tests;

public class ResultTests
{
    [Fact]
    public void Success_HoldsValueAndIsSuccess()
    {
        var result = Result<int>.Success(42);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_HoldsErrorAndIsNotSuccess()
    {
        var error = new Error("FAIL", "something went wrong");
        var result = Result<int>.Failure(error);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Match_RoutesSuccess()
    {
        var result = Result<int>.Success(42);
        var output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");
        Assert.Equal("ok:42", output);
    }

    [Fact]
    public void Match_RoutesFailure()
    {
        var result = Result<int>.Failure(new Error("X", "msg"));
        var output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");
        Assert.Equal("err:X", output);
    }

    [Fact]
    public void Map_TransformsSuccess()
    {
        var result = Result<int>.Success(21).Map(v => v * 2);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Map_PassesThroughFailure()
    {
        var error = new Error("X", "msg");
        var result = Result<int>.Failure(error).Map(v => v * 2);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Bind_ChainsSuccess()
    {
        var result = Result<int>.Success(21)
            .Bind(v => Result<int>.Success(v * 2));
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Bind_ShortCircuitsOnFailure()
    {
        var error = new Error("X", "msg");
        var called = false;
        var result = Result<int>.Failure(error)
            .Bind<int>(_ => { called = true; return Result<int>.Success(0); });
        Assert.False(result.IsSuccess);
        Assert.False(called);
    }
}
```

- [ ] **Step 6: Run Result tests to verify they fail**

Run: `dotnet test tests/eThangAgent.SharedKernel.Tests --filter "FullyQualifiedName~ResultTests"`
Expected: 8 failed (type `Result<T>` not found)

- [ ] **Step 7: Implement Result<T>**

Create `src/eThangAgent.SharedKernel/Result.cs`:

```csharp
namespace eThangAgent.SharedKernel;

public class Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess { get; }

    private Result(T value)
    {
        Value = value;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        Error = error;
        IsSuccess = false;
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    public TResult Match<TResult>(Func<T, TResult> success, Func<Error, TResult> failure)
        => IsSuccess ? success(Value!) : failure(Error!);

    public Result<TResult> Map<TResult>(Func<T, TResult> f)
        => IsSuccess ? Result<TResult>.Success(f(Value!)) : Result<TResult>.Failure(Error!);

    public Result<TResult> Bind<TResult>(Func<T, Result<TResult>> f)
        => IsSuccess ? f(Value!) : Result<TResult>.Failure(Error!);
}
```

- [ ] **Step 8: Run all SharedKernel tests**

Run: `dotnet test tests/eThangAgent.SharedKernel.Tests`
Expected: 10 passed

- [ ] **Step 9: Commit**

```bash
git add src/eThangAgent.SharedKernel/ tests/eThangAgent.SharedKernel.Tests/
git commit -m "feat(sharedkernel): add Error and Result<T> types"
```

---

## Task 3: Model.Domain — ModelConfig and IModelProvider

**Files:**

- Create: `src/eThangAgent.Model.Domain/ModelConfig.cs`
- Create: `src/eThangAgent.Model.Domain/IModelProvider.cs`
- Create: `tests/eThangAgent.Model.Domain.Tests/ModelConfigTests.cs`

**Interfaces:**

- Consumes: `Error`, `Result<T>` from SharedKernel
- Produces: `ModelConfig` record (`ModelId`, `MaxTokens`, `Temperature`), `ModelConfig.Create` factory method returning `Result<ModelConfig>`, `IModelProvider` interface with `Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)`

- [ ] **Step 1: Write failing tests for ModelConfig validation**

Create `tests/eThangAgent.Model.Domain.Tests/ModelConfigTests.cs`:

```csharp
namespace eThangAgent.Model.Domain.Tests;

public class ModelConfigTests
{
    [Fact]
    public void Create_ValidArgs_ReturnsSuccess()
    {
        var result = ModelConfig.Create("gpt-4o", 1024, 0.7f);
        Assert.True(result.IsSuccess);
        var config = result.Value!;
        Assert.Equal("gpt-4o", config.ModelId);
        Assert.Equal(1024, config.MaxTokens);
        Assert.Equal(0.7f, config.Temperature);
    }

    [Fact]
    public void Create_EmptyModelId_ReturnsFailure()
    {
        var result = ModelConfig.Create("  ", 100, 0.5f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_MaxTokensZero_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", 0, 0.5f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_MaxTokensNegative_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", -1, 0.5f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_TemperatureBelowZero_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", 100, -0.1f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_TemperatureAboveTwo_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", 100, 2.1f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_TemperatureBoundaries_ReturnSuccess()
    {
        Assert.True(ModelConfig.Create("m", 100, 0f).IsSuccess);
        Assert.True(ModelConfig.Create("m", 100, 2f).IsSuccess);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Model.Domain.Tests`
Expected: 7 failed

- [ ] **Step 3: Implement ModelConfig**

Create `src/eThangAgent.Model.Domain/ModelConfig.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain;

public sealed record ModelConfig(string ModelId, int MaxTokens, float Temperature)
{
    public static Result<ModelConfig> Create(string modelId, int maxTokens, float temperature)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return Result<ModelConfig>.Failure(new Error("InvalidModel", "Model ID is required."));
        if (maxTokens < 1)
            return Result<ModelConfig>.Failure(new Error("InvalidModel", "MaxTokens must be positive."));
        if (temperature < 0f || temperature > 2f)
            return Result<ModelConfig>.Failure(new Error("InvalidModel", "Temperature must be between 0 and 2."));
        return Result<ModelConfig>.Success(new ModelConfig(modelId, maxTokens, temperature));
    }
}
```

- [ ] **Step 4: Implement IModelProvider**

Create `src/eThangAgent.Model.Domain/IModelProvider.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain;

public interface IModelProvider
{
    Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct = default);
}
```

- [ ] **Step 5: Run all Model.Domain tests**

Run: `dotnet test tests/eThangAgent.Model.Domain.Tests`
Expected: 7 passed

- [ ] **Step 6: Commit**

```bash
git add src/eThangAgent.Model.Domain/ tests/eThangAgent.Model.Domain.Tests/
git commit -m "feat(model): add ModelConfig and IModelProvider domain types"
```

---

## Task 4: Conversation.Domain — Message, Conversation, Repository

**Files:**

- Create: `src/eThangAgent.Conversation.Domain/Role.cs`
- Create: `src/eThangAgent.Conversation.Domain/Message.cs`
- Create: `src/eThangAgent.Conversation.Domain/Conversation.cs`
- Create: `src/eThangAgent.Conversation.Domain/IConversationRepository.cs`
- Create: `tests/eThangAgent.Conversation.Domain.Tests/ConversationTests.cs`

**Interfaces:**

- Produces: `Role` enum, `Message` record, `Conversation` aggregate (`Messages`, `AddUserMessage`, `AddAssistantMessage`), `IConversationRepository` interface

- [ ] **Step 1: Write failing tests for Conversation**

Create `tests/eThangAgent.Conversation.Domain.Tests/ConversationTests.cs`:

```csharp
namespace eThangAgent.Conversation.Domain.Tests;

public class ConversationTests
{
    [Fact]
    public void NewConversation_HasNoMessages()
    {
        var c = new Conversation();
        Assert.Empty(c.Messages);
    }

    [Fact]
    public void AddUserMessage_AppendsUserMessage()
    {
        var c = new Conversation();
        c.AddUserMessage("Hello");
        Assert.Single(c.Messages);
        var msg = c.Messages[0];
        Assert.Equal(Role.User, msg.Role);
        Assert.Equal("Hello", msg.Content);
        Assert.NotEqual(default, msg.Timestamp);
    }

    [Fact]
    public void AddAssistantMessage_AppendsAssistantMessage()
    {
        var c = new Conversation();
        c.AddAssistantMessage("Hi back");
        Assert.Single(c.Messages);
        var msg = c.Messages[0];
        Assert.Equal(Role.Assistant, msg.Role);
        Assert.Equal("Hi back", msg.Content);n    }

    [Fact]
    public void Messages_AreTrackedInOrder()
    {
        var c = new Conversation();
        c.AddUserMessage("Q1");
        c.AddAssistantMessage("A1");
        c.AddUserMessage("Q2");
        Assert.Equal(3, c.Messages.Count);
        Assert.Equal(Role.User, c.Messages[0].Role);
        Assert.Equal(Role.Assistant, c.Messages[1].Role);
        Assert.Equal(Role.User, c.Messages[2].Role);
        Assert.Equal("Q1", c.Messages[0].Content);
        Assert.Equal("A1", c.Messages[1].Content);
        Assert.Equal("Q2", c.Messages[2].Content);
    }

    [Fact]
    public void Messages_IsReadOnly()
    {
        var c = new Conversation();
        c.AddUserMessage("test");
        Assert.IsAssignableFrom<IReadOnlyList<Message>>(c.Messages);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Conversation.Domain.Tests`
Expected: 5 failed

- [ ] **Step 3: Implement Role, Message, Conversation, IConversationRepository**

Create `src/eThangAgent.Conversation.Domain/Role.cs`:

```csharp
namespace eThangAgent.Conversation.Domain;

public enum Role { User, Assistant }
```

Create `src/eThangAgent.Conversation.Domain/Message.cs`:

```csharp
namespace eThangAgent.Conversation.Domain;

public sealed record Message(Role Role, string Content, DateTimeOffset Timestamp);
```

Create `src/eThangAgent.Conversation.Domain/Conversation.cs`:

```csharp
namespace eThangAgent.Conversation.Domain;

public class Conversation
{
    private readonly List<Message> _messages = [];

    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    public void AddUserMessage(string text)
        => _messages.Add(new Message(Role.User, text, DateTimeOffset.UtcNow));

    public void AddAssistantMessage(string text)
        => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow));n}


Create `src/eThangAgent.Conversation.Domain/IConversationRepository.cs`:

```csharp
namespace eThangAgent.Conversation.Domain;

public interface IConversationRepository
{
    Conversation GetCurrent();
    void Save(Conversation conversation);
}
```

- [ ] **Step 4: Run all Conversation tests**

Run: `dotnet test tests/eThangAgent.Conversation.Domain.Tests`
Expected: 5 passed

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.Conversation.Domain/ tests/eThangAgent.Conversation.Domain.Tests/
git commit -m "feat(conversation): add Conversation aggregate with Message and Role"
```

---

## Task 5: Agent.Domain — Agent Aggregate

**Files:**

- Create: `src/eThangAgent.Agent.Domain/Agent.cs`
- Create: `tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs`

**Interfaces:**

- Consumes: `Result<T>`, `Error`, `ModelConfig`, `IModelProvider`, `Conversation`, `Message`, `Role`
- Produces: `Agent` class (`Conversation` property, `Config` property, `Task<Result<string>> SendMessage(string text, CancellationToken ct)`)

- [ ] **Step 1: Write failing tests for Agent**

Create `tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs`:

```csharp
using eThangAgent.Model.Domain;
using eThangAgent.Conversation.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Domain.Tests;

public class AgentTests
{
    private static ModelConfig DefaultConfig =>
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    [Fact]
    public async Task SendMessage_OnSuccess_AddsBothMessages()
    {
        var provider = new FakeModelProvider(Result<string>.Success("Hello back"));
        var conversation = new Conversation();
        var agent = new Agent(provider, conversation, DefaultConfig);

        var result = await agent.SendMessage("Hi");

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(Role.User, conversation.Messages[0].Role);
        Assert.Equal("Hi", conversation.Messages[0].Content);
        Assert.Equal(Role.Assistant, conversation.Messages[1].Role);
        Assert.Equal("Hello back", conversation.Messages[1].Content);
    }

    [Fact]
    public async Task SendMessage_OnFailure_DoesNotAddAssistantMessage()
    {
        var error = new Error("Test", "fail");
        var provider = new FakeModelProvider(Result<string>.Failure(error));
        var conversation = new Conversation();
        var agent = new Agent(provider, conversation, DefaultConfig);

        var result = await agent.SendMessage("Hi");

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
        Assert.Single(conversation.Messages);
        Assert.Equal(Role.User, conversation.Messages[0].Role);
        Assert.Equal("Hi", conversation.Messages[0].Content);
    }

    [Fact]
    public async Task SendMessage_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new FakeModelProvider(Result<string>.Success("ok"));
        var agent = new Agent(provider, new Conversation(), DefaultConfig);

        var result = await agent.SendMessage("Hi", cts.Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Constructor_ExposesConversationAndConfig()
    {
        var provider = new FakeModelProvider(Result<string>.Success("ok"));
        var conversation = new Conversation();
        var config = DefaultConfig;
        var agent = new Agent(provider, conversation, config);

        Assert.Same(conversation, agent.Conversation);
        Assert.Same(config, agent.Config);
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly Result<string> _result;
        public FakeModelProvider(Result<string> result) => _result = result;

        public Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult(Result<string>.Failure(new Error("Cancelled", "Cancelled")));
            return Task.FromResult(_result);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Agent.Domain.Tests`
Expected: 4 failed

- [ ] **Step 3: Implement Agent**

Create `src/eThangAgent.Agent.Domain/Agent.cs`:

```csharp
using eThangAgent.Model.Domain;
using eThangAgent.Conversation.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Domain;

public class Agent
{
    private readonly IModelProvider _provider;

    public Conversation Conversation { get; }
    public ModelConfig Config { get; }

    public Agent(IModelProvider provider, Conversation conversation, ModelConfig config)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<Result<string>> SendMessage(string text, CancellationToken ct = default)
    {
        Conversation.AddUserMessage(text);
        var result = await _provider.SendAsync(Config, text, ct);
        if (result.IsSuccess)
            Conversation.AddAssistantMessage(result.Value!);
        return result;
    }
}
```

- [ ] **Step 4: Run Agent tests**

Run: `dotnet test tests/eThangAgent.Agent.Domain.Tests`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.Agent.Domain/ tests/eThangAgent.Agent.Domain.Tests/
git commit -m "feat(agent): add Agent aggregate with SendMessage"
```

---

## Task 6: Agent.Application — CQRS Command + Handler

**Files:**

- Create: `src/eThangAgent.Agent.Application/SendMessageCommand.cs`
- Create: `src/eThangAgent.Agent.Application/SendMessageCommandHandler.cs`
- Create: `tests/eThangAgent.Agent.Application.Tests/SendMessageCommandHandlerTests.cs`

**Interfaces:**

- Consumes: `Agent`, `Result<T>`, `Error`
- Produces: `SendMessageCommand` record, `SendMessageCommandHandler` class with `Task<Result<string>> Handle(SendMessageCommand, CancellationToken)`

- [ ] **Step 1: Write failing tests for the handler**

Create `tests/eThangAgent.Agent.Application.Tests/SendMessageCommandHandlerTests.cs`:

```csharp
using eThangAgent.Agent.Domain;
using eThangAgent.Conversation.Domain;
using eThangAgent.Model.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Handle_DelegatesToAgentAndReturnsResult()
    {
        var provider = new StubModelProvider(Result<string>.Success("response"));
        var agent = new Agent(provider, new Conversation(),
            ModelConfig.Create("m", 100, 0.5f).Value!);
        var handler = new SendMessageCommandHandler(agent);

        var result = await handler.Handle(new SendMessageCommand("hello"));

        Assert.True(result.IsSuccess);
        Assert.Equal("response", result.Value);
    }

    [Fact]
    public async Task Handle_PropagatesFailure()
    {
        var error = new Error("FAIL", "bad");
        var provider = new StubModelProvider(Result<string>.Failure(error));
        var agent = new Agent(provider, new Conversation(),
            ModelConfig.Create("m", 100, 0.5f).Value!);
        var handler = new SendMessageCommandHandler(agent);

        var result = await handler.Handle(new SendMessageCommand("hello"));

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        private readonly Result<string> _result;
        public StubModelProvider(Result<string> result) => _result = result;

        public Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)
            => Task.FromResult(_result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Agent.Application.Tests`
Expected: 2 failed

- [ ] **Step 3: Implement Command and Handler**

Create `src/eThangAgent.Agent.Application/SendMessageCommand.cs`:

```csharp
namespace eThangAgent.Agent.Application;

public sealed record SendMessageCommand(string Text);
```

Create `src/eThangAgent.Agent.Application/SendMessageCommandHandler.cs`:

```csharp
using eThangAgent.Agent.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

public class SendMessageCommandHandler
{
    private readonly Agent _agent;

    public SendMessageCommandHandler(Agent agent)
        => _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public Task<Result<string>> Handle(SendMessageCommand command, CancellationToken ct = default)
        => _agent.SendMessage(command.Text, ct);
}
```

- [ ] **Step 4: Run handler tests**

Run: `dotnet test tests/eThangAgent.Agent.Application.Tests`
Expected: 2 passed

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.Agent.Application/ tests/eThangAgent.Agent.Application.Tests/
git commit -m "feat(agent): add SendMessageCommand and handler"
```

---

## Task 7: OpenRouter.ACL — Provider Implementation

**Files:**

- Create: `src/eThangAgent.OpenRouter.ACL/OpenRouterConfiguration.cs`
- Create: `src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs`
- Create: `tests/eThangAgent.OpenRouter.ACL.Tests/FakeHttpMessageHandler.cs`
- Create: `tests/eThangAgent.OpenRouter.ACL.Tests/OpenRouterModelProviderTests.cs`

**Interfaces:**

- Consumes: `IModelProvider`, `ModelConfig`, `Result<T>`, `Error`
- Produces: `OpenRouterConfiguration` record (`string ApiKey`, `Uri BaseUrl`), `OpenRouterModelProvider` implementing `IModelProvider`

All tests in this task use a fake `HttpMessageHandler` that returns canned OpenRouter responses. No real network access, no API key, no skip logic.

- [ ] **Step 1: Write the ACL implementation**

Create `src/eThangAgent.OpenRouter.ACL/OpenRouterConfiguration.cs`:

```csharp
namespace eThangAgent.OpenRouter.ACL;

public sealed record OpenRouterConfiguration(string ApiKey, Uri BaseUrl);
```

Create `src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.Model.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.OpenRouter.ACL;

public class OpenRouterModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly OpenRouterConfiguration _config;

    public OpenRouterModelProvider(HttpClient http, OpenRouterConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)
    {
        try
        {
            var requestBody = new
            {
                model = config.ModelId,
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens = config.MaxTokens,
                temperature = config.Temperature
            };

            var requestUri = new Uri(_config.BaseUrl, "/api/v1/chat/completions");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(requestBody)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

            using var response = await _http.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return statusCode switch
                {
                    429 => Result<string>.Failure(new Error("RateLimited",
                        "OpenRouter rate limit exceeded.")),
                    408 => Result<string>.Failure(new Error("ProviderTimeout",
                        "Request timed out.")),
                    _ => Result<string>.Failure(new Error("ProviderError",
                        $"OpenRouter returned HTTP {statusCode}."))
                };
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var content = body.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString();
            return Result<string>.Success(content ?? string.Empty);
        }
        catch (TaskCanceledException)
        {
            return Result<string>.Failure(new Error("ProviderTimeout",
                "Request timed out."));
        }
        catch (HttpRequestException ex)
        {
            return Result<string>.Failure(new Error("ProviderError", ex.Message));
        }
    }
}
```

- [ ] **Step 2: Write the fake HttpMessageHandler**

Create `tests/eThangAgent.OpenRouter.ACL.Tests/FakeHttpMessageHandler.cs`:

```csharp
namespace eThangAgent.OpenRouter.ACL.Tests;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_respond(request));
}
```

- [ ] **Step 3: Write ACL tests against the fake transport**

Create `tests/eThangAgent.OpenRouter.ACL.Tests/OpenRouterModelProviderTests.cs`:

```csharp
using System.Net;
using System.Text;
using eThangAgent.Model.Domain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderTests
{
    private static readonly Uri BaseUrl = new("https://openrouter.test");
    private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsContent()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"Hello back"}}]}"""));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var modelConfig = ModelConfig.Create("openai/gpt-4o-mini", 256, 0.7f).Value!;

        var result = await provider.SendAsync(modelConfig, "hi", default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value);
    }

    [Fact]
    public async Task SendAsync_SendsBearerTokenModelAndPath()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("openai/gpt-4o-mini", 128, 0.7f).Value!, "hi", default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
        Assert.Equal("https://openrouter.test/api/v1/chat/completions", captured!.RequestUri!.ToString());
        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("openai/gpt-4o-mini", body);
    }

    [Fact]
    public async Task SendAsync_OnRateLimit_ReturnsRateLimitedError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!, "hi", default);

        Assert.False(result.IsSuccess);
        Assert.Equal("RateLimited", result.Error!.Code);
    }

    [Fact]
    public async Task SendAsync_OnTimeout_ReturnsProviderTimeoutError()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!, "hi", default);

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderTimeout", result.Error!.Code);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
        => new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
```

- [ ] **Step 4: Build and run ACL tests**

Run: `dotnet test tests/eThangAgent.OpenRouter.ACL.Tests`
Expected: 4 passed (no network, no API key needed, no skips)

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.OpenRouter.ACL/ tests/eThangAgent.OpenRouter.ACL.Tests/
git commit -m "feat(openrouter): add OpenRouter ACL with mocked-transport tests"
```

---

## Task 8: CLI — Composition Root and REPL

**Files:**

- Create: `src/eThangAgent.CLI/InMemoryConversationRepository.cs`
- Create: `src/eThangAgent.CLI/Program.cs`
- Create: `tests/eThangAgent.CLI.Tests/E2ETests.cs`
- Create: `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs`

**Interfaces:**

- Consumes: all projects
- Produces: working `dotnet run` REPL loop; configurable base URL for tests

The CLI reads `OPENROUTER_BASE_URL` (defaults to `https://openrouter.ai`). The E2E test runs a local mock HTTP server and points the CLI at it — a genuine end-to-end run through the real process and HTTP boundary, but against a mock, never real OpenRouter.

- [ ] **Step 1: Implement InMemoryConversationRepository**

Create `src/eThangAgent.CLI/InMemoryConversationRepository.cs`:

```csharp
using eThangAgent.Conversation.Domain;

namespace eThangAgent.CLI;

public class InMemoryConversationRepository : IConversationRepository
{
    private Conversation _current = new();

    public Conversation GetCurrent() => _current;
    public void Save(Conversation conversation) => _current = conversation;
}
```

- [ ] **Step 2: Implement Program.cs with DI and REPL**

Create `src/eThangAgent.CLI/Program.cs`:

```csharp
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Domain;
using eThangAgent.Conversation.Domain;
using eThangAgent.Model.Domain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
    ?? throw new InvalidOperationException(
        "OPENROUTER_API_KEY environment variable not set. " +
        "Get a key at https://openrouter.ai/keys");

var baseUrlEnv = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
var baseUrl = string.IsNullOrWhiteSpace(baseUrlEnv)
    ? new Uri("https://openrouter.ai")
    : new Uri(baseUrlEnv);

var services = new ServiceCollection()
    .AddSingleton(new OpenRouterConfiguration(apiKey, baseUrl))
    .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(120);
    })
    .Services
    .AddSingleton(_ => ModelConfig.Create("openai/gpt-4o-mini", 1024, 0.7f).Value!)
    .AddSingleton<Conversation>()
    .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
    .AddSingleton<Agent>(sp =>
    {
        var provider = sp.GetRequiredService<IModelProvider>();
        var conversation = sp.GetRequiredService<Conversation>();
        var config = sp.GetRequiredService<ModelConfig>();
        return new Agent(provider, conversation, config);
    })
    .AddSingleton<SendMessageCommandHandler>()
    .BuildServiceProvider();

var handler = services.GetRequiredService<SendMessageCommandHandler>();

Console.WriteLine("eThang Agent - type /exit to quit");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;
    if (input is "/exit" or "/quit")
        break;
    if (string.IsNullOrWhiteSpace(input))
        continue;

    var result = await handler.Handle(new SendMessageCommand(input));
    Console.WriteLine(result.Match(
        success: response => response,
        failure: error => $"Error [{error.Code}]: {error.Message}"));
    Console.WriteLine();
}
```

- [ ] **Step 3: Build and verify it compiles**

Run: `dotnet build`
Expected: 0 errors across all projects

- [ ] **Step 4: Manual smoke test (human, against real OpenRouter — optional)**

Run: `$env:OPENROUTER_API_KEY="your-key"; dotnet run --project src/eThangAgent.CLI`
Expected: REPL prompt appears, type a message, get a response.

Optional: set `$env:OPENROUTER_BASE_URL` to point at a different endpoint. This step is for a human verifying the real provider; automated tests never hit the network.

- [ ] **Step 5: Write the mock OpenRouter server helper**

Create `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace eThangAgent.CLI.Tests;

public sealed class MockOpenRouterServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string BaseUrl { get; private set; } = "";

    public void Start()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            if (ctx.Request.Url!.AbsolutePath == "/api/v1/chat/completions")
            {
                var body = """{"choices":[{"message":{"content":"pineapple"}}]}""";
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
```

- [ ] **Step 6: Write the E2E test**

Create `tests/eThangAgent.CLI.Tests/E2ETests.cs`:

```csharp
using System.Diagnostics;

namespace eThangAgent.CLI.Tests;

public class E2ETests
{
    [Fact]
    public async Task Repl_RespondsToPrompt_AgainstMockServer()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        var projectDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "eThangAgent.CLI"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --no-build",
            WorkingDirectory = projectDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.EnvironmentVariables["OPENROUTER_API_KEY"] = "test-key";
        startInfo.EnvironmentVariables["OPENROUTER_BASE_URL"] = mock.BaseUrl;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var reader = process.StandardOutput;
        var banner = await ReadUntil(reader, "> ");
        Assert.Contains("eThang Agent", banner);

        await process.StandardInput.WriteLineAsync("Say 'pineapple' and nothing else.");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("pineapple", response, StringComparison.OrdinalIgnoreCase);

        await process.StandardInput.WriteLineAsync("/exit");
        await process.WaitForExitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<string> ReadUntil(StreamReader reader, string delimiter)
    {
        var output = new List<char>();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, 1);
            if (read == 0) break;
            output.Add(buffer[0]);
            var tail = new string(output.ToArray()[
                Math.Max(0, output.Count - delimiter.Length)..]);
            if (tail == delimiter) break;
        }
        return new string(output.ToArray());
    }
}
```

- [ ] **Step 7: Build and run E2E test**

Run: `dotnet build`
Expected: 0 errors

Run: `dotnet test tests/eThangAgent.CLI.Tests`
Expected: 1 passed (against local mock server, no network, no API key)

- [ ] **Step 8: Commit**

```bash
git add src/eThangAgent.CLI/ tests/eThangAgent.CLI.Tests/
git commit -m "feat(cli): add composition root, DI wiring, REPL loop, and mock-server E2E"
```

---

## Task 9: Final Verification

**No new files.** Verify everything builds and all tests pass.

- [ ] **Step 1: Clean build**

Run: `dotnet clean && dotnet build`
Expected: 0 errors, 0 warnings

- [ ] **Step 2: Run all tests**

Run: `dotnet test`
Expected: all unit tests pass (~30 tests); integration and E2E tests pass against mocked endpoints (no API key required, no real network)

- [ ] **Step 3: Verify the dependency graph**

Run: `dotnet list src/eThangAgent.CLI/eThangAgent.CLI.csproj reference`
Verify: CLI → Agent.Application and OpenRouter.ACL, no direct ref to Model.Domain or Conversation.Domain

Run: `dotnet list src/eThangAgent.Agent.Domain/eThangAgent.Agent.Domain.csproj reference`
Verify: Agent.Domain → Model.Domain, Conversation.Domain, SharedKernel

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "chore: final verification and cleanup"
```
