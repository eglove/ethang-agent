# Tool Domain + `read` Tool — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Tool Domain with its first built-in tool (`read`), wire end-to-end tool calling through OpenRouter, and enforce strict-input validation philosophy.

**Architecture:** New `eThangAgent.Tool.Domain` project defines all tool contracts; `eThangAgent.FileSystem.ACL` provides a PowerShell-runspace-backed `IFileSystemAccess`. The Conversation and Model domains gain tool-call/tool-result message shapes, the OpenRouter ACL translates them to wire format, and the Agent aggregate gains a tool-execution loop (max 10 iterations, sequential). ReadTool validates strictly (all params required, only `endLine` clamps past EOF) and returns line-numbered output with bracketed annotations.

**Tech Stack:** .NET 10, C#, xUnit, System.Management.Automation (PowerShell 7.x in-process), System.Text.Json

**Spec:** `docs/skills/specs/2026-08-20-tool-domain-read-tool-design.md`

## Global Constraints

- .NET 10, C#, Windows-only; no Linux paths
- PowerShell 7.x via System.Management.Automation NuGet for FileSystem ACL
- Result<T>/Error for all expected failures; exceptions only for programmer errors
- Records with init-only properties; immutability preferred
- No DI container references in domain projects
- Namespaces: domains use `eThangAgent.XDomain` (no dot); ACLs use `eThangAgent.X.ACL` (with dot)
- Tool input: all params mandatory, no defaults, no type coercion, reject unknown params; only `endLine` > file length clamps (with visible warning)
- `read` range cap: 1000 lines; larger rejected with chunking advice
- Agent tool loop: `MaxToolIterations = 10`; tool errors fed back as tool results
- Output format: bracketed header, line-number gutter (right-aligned, `→` separator), warning line last
- Line numbers: 1-based, inclusive

---

## File Structure

```text
src/eThangAgent.Tool.Domain/                     NEW — tool contracts + ReadTool
  eThangAgent.Tool.Domain.csproj
  ToolParameterType.cs
  ToolParameter.cs
  ToolDefinition.cs
  RawToolInput.cs
  ToolResult.cs
  ITool.cs
  FileRead.cs
  IFileSystemAccess.cs
  IToolRegistry.cs
  ToolRegistry.cs
  ReadToolInput.cs
  ReadTool.cs

src/eThangAgent.FileSystem.ACL/                  NEW — PowerShell-runspace IFileSystemAccess
  eThangAgent.FileSystem.ACL.csproj
  PowerShellFileSystemAccess.cs

tests/eThangAgent.Tool.Domain.Tests/             NEW
  eThangAgent.Tool.Domain.Tests.csproj
  GlobalUsings.cs
  ToolRegistryTests.cs
  ReadToolTests.cs

tests/eThangAgent.FileSystem.ACL.Tests/          NEW
  eThangAgent.FileSystem.ACL.Tests.csproj
  GlobalUsings.cs
  PowerShellFileSystemAccessTests.cs

src/eThangAgent.Conversation.Domain/             MODIFY
  Role.cs
  Message.cs
  Conversation.cs

tests/eThangAgent.Conversation.Domain.Tests/     MODIFY
  ConversationTests.cs

src/eThangAgent.Model.Domain/                    MODIFY
  eThangAgent.Model.Domain.csproj
  IModelProvider.cs
  (new) ModelRequest.cs
  (new) ModelResponse.cs
  (new) ToolCallRequest.cs

src/eThangAgent.OpenRouter.ACL/                  MODIFY
  OpenRouterModelProvider.cs

tests/eThangAgent.OpenRouter.ACL.Tests/          MODIFY
  OpenRouterModelProviderTests.cs

src/eThangAgent.Agent.Domain/                    MODIFY
  Agent.cs

tests/eThangAgent.Agent.Domain.Tests/            MODIFY
  AgentTests.cs

src/eThangAgent.CLI/                             MODIFY
  eThangAgent.CLI.csproj
  Program.cs

tests/eThangAgent.CLI.Tests/                     MODIFY
  MockOpenRouterServer.cs
  E2ETests.cs

eThangAgent.slnx                                 MODIFY
```

---

### Task 1: Tool.Domain project + core contracts

**Files:**

- Create: `src/eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj`
- Create: `src/eThangAgent.Tool.Domain/ToolParameterType.cs`
- Create: `src/eThangAgent.Tool.Domain/ToolParameter.cs`
- Create: `src/eThangAgent.Tool.Domain/ToolDefinition.cs`
- Create: `src/eThangAgent.Tool.Domain/RawToolInput.cs`
- Create: `src/eThangAgent.Tool.Domain/ToolResult.cs`
- Create: `src/eThangAgent.Tool.Domain/ITool.cs`
- Create: `src/eThangAgent.Tool.Domain/FileRead.cs`
- Create: `src/eThangAgent.Tool.Domain/IFileSystemAccess.cs`
- Create: `src/eThangAgent.Tool.Domain/IToolRegistry.cs`
- Create: `src/eThangAgent.Tool.Domain/ToolRegistry.cs`
- Create: `tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj`
- Create: `tests/eThangAgent.Tool.Domain.Tests/GlobalUsings.cs`
- Create: `tests/eThangAgent.Tool.Domain.Tests/ToolRegistryTests.cs`
- Modify: `eThangAgent.slnx`

**Interfaces:**

- Produces: `ToolParameterType` enum `{ String, Integer }`, `ToolParameter(Name, Type, Description, int? Minimum=null)`, `ToolDefinition(Name, Description, IReadOnlyList<ToolParameter> Parameters)`, `RawToolInput(Name, JsonArguments)`, `ToolResult(Content, IsError)`, `ITool` with `Definition` property and `ExecuteAsync(RawToolInput, CancellationToken) → Task<ToolResult>`, `FileRead(Lines, LastLineRead, TotalLines)`, `IFileSystemAccess` with `ReadLinesAsync(string path, int startLine, int endLine, CancellationToken) → Task<Result<FileRead>>`, `IToolRegistry.Find(string name) → ITool?` and `Definitions → IReadOnlyList<ToolDefinition>`, `ToolRegistry(IEnumerable<ITool>)`

- [ ] **Step 1: Create Tool.Domain project and add to solution**

```bash
cd C:/Users/glove/projects/ethang-agent
mkdir "src\eThangAgent.Tool.Domain"
```

Create `src/eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

Edit `eThangAgent.slnx` — add after the Terminal.ACL line:

```xml
  <Project Path="src/eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Create the test project**

```bash
mkdir "tests\eThangAgent.Tool.Domain.Tests"
```

Create `tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/eThangAgent.Tool.Domain.Tests/GlobalUsings.cs`:

```csharp
global using Xunit;
```

Add to `eThangAgent.slnx` (after the last `<Project Path="tests/` line):

```xml
  <Project Path="tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj" />
```

- [ ] **Step 4: Write the contract types**

Create `src/eThangAgent.Tool.Domain/ToolParameterType.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public enum ToolParameterType { String, Integer }
```

Create `src/eThangAgent.Tool.Domain/ToolParameter.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed record ToolParameter(string Name, ToolParameterType Type, string Description, int? Minimum = null);
```

Create `src/eThangAgent.Tool.Domain/ToolDefinition.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed record ToolDefinition(string Name, string Description, IReadOnlyList<ToolParameter> Parameters);
```

Create `src/eThangAgent.Tool.Domain/RawToolInput.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed record RawToolInput(string Name, string JsonArguments);
```

Create `src/eThangAgent.Tool.Domain/ToolResult.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed record ToolResult(string Content, bool IsError);
```

Create `src/eThangAgent.Tool.Domain/ITool.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default);
}
```

Create `src/eThangAgent.Tool.Domain/FileRead.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed record FileRead(IReadOnlyList<string> Lines, int LastLineRead, int TotalLines);
```

Create `src/eThangAgent.Tool.Domain/IFileSystemAccess.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileSystemAccess
{
    Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default);
}
```

Create `src/eThangAgent.Tool.Domain/IToolRegistry.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public interface IToolRegistry
{
    ITool? Find(string name);
    IReadOnlyList<ToolDefinition> Definitions { get; }
}
```

Create `src/eThangAgent.Tool.Domain/ToolRegistry.cs`:

```csharp
namespace eThangAgent.ToolDomain;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
    }

    public ITool? Find(string name)
        => _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<ToolDefinition> Definitions
        => _tools.Values.Select(t => t.Definition).ToList();
}
```

- [ ] **Step 5: Verify build again**

Run: `dotnet build eThangAgent.slnx`
Expected: Build succeeded.

- [ ] **Step 6: Write the failing test — ToolRegistryTests**

Create `tests/eThangAgent.Tool.Domain.Tests/ToolRegistryTests.cs`:

```csharp
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class ToolRegistryTests
{
    private static ITool FakeTool(string name) => new FakeTool(new ToolDefinition(name, "desc", []));

    [Fact]
    public void Find_KnownName_ReturnsTool()
    {
        var tool = FakeTool("read");
        var registry = new ToolRegistry([tool]);

        var found = registry.Find("read");

        Assert.NotNull(found);
        Assert.Same(tool, found);
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        var registry = new ToolRegistry([FakeTool("read")]);

        var found = registry.Find("nope");

        Assert.Null(found);
    }

    [Fact]
    public void Find_MatchesCaseSensitive()
    {
        var registry = new ToolRegistry([FakeTool("read")]);

        var found = registry.Find("READ");

        Assert.Null(found);
    }

    [Fact]
    public void Definitions_ReturnsAll()
    {
        var a = FakeTool("read");
        var b = FakeTool("grep");
        var registry = new ToolRegistry([a, b]);

        var defs = registry.Definitions;

        Assert.Equal(2, defs.Count);
        Assert.Contains(defs, d => d.Name == "read");
        Assert.Contains(defs, d => d.Name == "grep");
    }

    [Fact]
    public void Registry_WithDuplicateName_ThrowsArgumentsException()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([FakeTool("read"), FakeTool("read")]));
    }

    private sealed class FakeTool : ITool
    {
        public ToolDefinition Definition { get; }
        public FakeTool(ToolDefinition def) => Definition = def;
        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
```

- [ ] **Step 7: Verify tests fail**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --nologo -v q`
Expected: Tests run and fail — `FakeTool`/`ToolRegistry` not found.

- [ ] **Step 8: Implementation already done in Step 4**

All types were created in Step 4. Run tests to confirm they pass:

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --nologo -v q`
Expected: 5 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add eThangAgent.slnx src/eThangAgent.Tool.Domain/ tests/eThangAgent.Tool.Domain.Tests/
git commit -m "feat: add Tool.Domain project with core contracts and ToolRegistry"
```

---

### Task 2: Conversation.Domain — tool-call messages

**Files:**

- Modify: `src/eThangAgent.Conversation.Domain/Role.cs`
- Modify: `src/eThangAgent.Conversation.Domain/Message.cs`
- Modify: `src/eThangAgent.Conversation.Domain/Conversation.cs`
- Modify: `tests/eThangAgent.Conversation.Domain.Tests/ConversationTests.cs`

**Interfaces:**

- Produces: `Role.Tool`, `ToolCall(Id, Name, Arguments)`, `Message(Role, Content, Timestamp, IReadOnlyList<ToolCall>? ToolCalls=null, string? ToolCallId=null)`, `Conversation.AddAssistantMessage(string text, IReadOnlyList<ToolCall>? toolCalls=null)`, `Conversation.AddToolResult(string toolCallId, string content)`

- [ ] **Step 1: Read existing test file**

Read `tests/eThangAgent.Conversation.Domain.Tests/ConversationTests.cs` so we preserve existing tests.

- [ ] **Step 2: Write the failing tests**

Append these tests to `tests/eThangAgent.Conversation.Domain.Tests/ConversationTests.cs` (inside the existing class, before the closing `}`):

```csharp
    [Fact]
    public void AddToolResult_AppendsToolMessage()
    {
        var conv = new Conversation();
        conv.AddToolResult("call_1", "file content here");

        Assert.Single(conv.Messages);
        var msg = conv.Messages[0];
        Assert.Equal(Role.Tool, msg.Role);
        Assert.Equal("file content here", msg.Content);
        Assert.Equal("call_1", msg.ToolCallId);
        Assert.Null(msg.ToolCalls);
    }

    [Fact]
    public void AddAssistantMessage_WithToolCalls_StoresThem()
    {
        var conv = new Conversation();
        var calls = new List<ToolCall> { new("call_1", "read", "{\"path\":\"f\"}") };
        conv.AddAssistantMessage("", calls);

        var msg = conv.Messages[0];
        Assert.Equal(Role.Assistant, msg.Role);
        Assert.Single(msg.ToolCalls!);
        Assert.Equal("call_1", msg.ToolCalls![0].Id);
        Assert.Equal("read", msg.ToolCalls[0].Name);
    }

    [Fact]
    public void AddAssistantMessage_WithoutToolCalls_StoresNull()
    {
        var conv = new Conversation();
        conv.AddAssistantMessage("hello");

        var msg = conv.Messages[0];
        Assert.Null(msg.ToolCalls);
        Assert.Null(msg.ToolCallId);
    }

    [Fact]
    public void AddToolResult_AfterUser_OrderIsPreserved()
    {
        var conv = new Conversation();
        conv.AddUserMessage("read file.md");
        var calls = new List<ToolCall> { new("c1", "read", "{}") };
        conv.AddAssistantMessage(null!, calls);
        conv.AddToolResult("c1", "contents");
        conv.AddAssistantMessage("file.md says hello");

        Assert.Equal(4, conv.Messages.Count);
        Assert.Equal(Role.User, conv.Messages[0].Role);
        Assert.Equal(Role.Assistant, conv.Messages[1].Role);
        Assert.Equal(Role.Tool, conv.Messages[2].Role);
        Assert.Equal(Role.Assistant, conv.Messages[3].Role);
    }
```

- [ ] **Step 3: Run tests — verify new ones fail**

Run: `dotnet test tests/eThangAgent.Conversation.Domain.Tests --nologo -v q --filter "FullyQualifiedName~AddToolResult|FullyQualifiedName~AddAssistantMessage_WithToolCalls|FullyQualifiedName~AddAssistantMessage_WithoutToolCalls|FullyQualifiedName~AddToolResult_AfterUser"`
Expected: Compile error — `Role.Tool` does not exist, `ToolCall` does not exist.

- [ ] **Step 4: Implement Role.cs**

Replace `src/eThangAgent.Conversation.Domain/Role.cs`:

```csharp
namespace eThangAgent.ConversationDomain;

public enum Role { User, Assistant, Tool }
```

- [ ] **Step 5: Implement ToolCall + update Message.cs**

Create `src/eThangAgent.Conversation.Domain/ToolCall.cs`:

```csharp
namespace eThangAgent.ConversationDomain;

public sealed record ToolCall(string Id, string Name, string Arguments);
```

Replace `src/eThangAgent.Conversation.Domain/Message.cs`:

```csharp
namespace eThangAgent.ConversationDomain;

public sealed record Message(
    Role Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);
```

- [ ] **Step 6: Update Conversation.cs**

Replace `src/eThangAgent.Conversation.Domain/Conversation.cs`:

```csharp
namespace eThangAgent.ConversationDomain;

public class Conversation
{
    private readonly List<Message> _messages = [];

    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    public void AddUserMessage(string text)
        => _messages.Add(new Message(Role.User, text, DateTimeOffset.UtcNow));

    public void AddAssistantMessage(string text)
        => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow));

    public void AddAssistantMessage(string text, IReadOnlyList<ToolCall>? toolCalls)
        => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow, toolCalls));

    public void AddToolResult(string toolCallId, string content)
        => _messages.Add(new Message(Role.Tool, content, DateTimeOffset.UtcNow, ToolCallId: toolCallId));
}
```

- [ ] **Step 7: Run full suite for Conversation tests**

Run: `dotnet test tests/eThangAgent.Conversation.Domain.Tests --nologo -v q`
Expected: All pass — new 4 + existing tests.

- [ ] **Step 8: Commit**

```bash
git add src/eThangAgent.Conversation.Domain/Role.cs src/eThangAgent.Conversation.Domain/ToolCall.cs src/eThangAgent.Conversation.Domain/Message.cs src/eThangAgent.Conversation.Domain/Conversation.cs tests/eThangAgent.Conversation.Domain.Tests/ConversationTests.cs
git commit -m "feat: add tool-call and tool-result message support to Conversation domain"
```

---

### Task 3: Model.Domain tool-aware contract + OpenRouter.ACL rewrite

**Files:**

- Create: `src/eThangAgent.Model.Domain/ModelRequest.cs`
- Create: `src/eThangAgent.Model.Domain/ModelResponse.cs`
- Create: `src/eThangAgent.Model.Domain/ToolCallRequest.cs`
- Modify: `src/eThangAgent.Model.Domain/IModelProvider.cs`
- Modify: `src/eThangAgent.Model.Domain/eThangAgent.Model.Domain.csproj`
- Modify: `src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs`
- Modify: `tests/eThangAgent.OpenRouter.ACL.Tests/OpenRouterModelProviderTests.cs`
- Modify: `src/eThangAgent.Agent.Domain/Agent.cs` (mechanical call-site fix only)
- Modify: `tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs` (fake signature update only)

**Interfaces:**

- Produces: `ModelRequest(IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition>? Tools=null)`, `ToolCallRequest(string Id, string Name, string Arguments)`, `ModelResponse(string? Content, IReadOnlyList<ToolCallRequest> ToolCalls)`, `IModelProvider.SendAsync(ModelConfig, ModelRequest, CancellationToken) → Task<Result<ModelResponse>>`
- Consumes: `Message`, `Role`, `ToolCall`, `ToolDefinition`, `ToolParameter`, `ToolParameterType` (from Task 1 & 2)

- [ ] **Step 1: Update Model.Domain csproj**

Replace `src/eThangAgent.Model.Domain/eThangAgent.Model.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
    <ProjectReference Include="../eThangAgent.Conversation.Domain/eThangAgent.Conversation.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create ModelRequest, ModelResponse, ToolCallRequest**

Create `src/eThangAgent.Model.Domain/ModelRequest.cs`:

```csharp
using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.ModelDomain;

public sealed record ModelRequest(IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition>? Tools = null);
```

Create `src/eThangAgent.Model.Domain/ToolCallRequest.cs`:

```csharp
namespace eThangAgent.ModelDomain;

public sealed record ToolCallRequest(string Id, string Name, string Arguments);
```

Create `src/eThangAgent.Model.Domain/ModelResponse.cs`:

```csharp
namespace eThangAgent.ModelDomain;

public sealed record ModelResponse(string? Content, IReadOnlyList<ToolCallRequest> ToolCalls);
```

- [ ] **Step 3: Replace IModelProvider**

Replace `src/eThangAgent.Model.Domain/IModelProvider.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public interface IModelProvider
{
    Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 4: Rewrite OpenRouterModelProvider**

Replace `src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

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

    public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct)
    {
        try
        {
            var bodyDict = new Dictionary<string, object?>
            {
                ["model"] = config.ModelId,
                ["messages"] = request.Messages.Select(TranslateMessage).ToArray(),
                ["max_tokens"] = config.MaxTokens,
                ["temperature"] = config.Temperature,
            };
            if (request.Tools is { Count: > 0 })
                bodyDict["tools"] = request.Tools.Select(TranslateTool).ToArray();

            var requestUri = new Uri(_config.BaseUrl, "/api/v1/chat/completions");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(bodyDict)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

            using var response = await _http.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return statusCode switch
                {
                    429 => Result<ModelResponse>.Failure(new Error("RateLimited",
                        "OpenRouter rate limit exceeded.")),
                    408 => Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                        "Request timed out.")),
                    _ => Result<ModelResponse>.Failure(new Error("ProviderError",
                        $"OpenRouter returned HTTP {statusCode}."))
                };
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var message = body.GetProperty("choices")[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var toolCalls = new List<ToolCallRequest>();
            if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in tc.EnumerateArray())
                {
                    var fn = call.GetProperty("function");
                    toolCalls.Add(new ToolCallRequest(
                        call.GetProperty("id").GetString()!,
                        fn.GetProperty("name").GetString()!,
                        fn.GetProperty("arguments").GetString() ?? ""));
                }
            }

            return Result<ModelResponse>.Success(new ModelResponse(content, toolCalls));
        }
        catch (TaskCanceledException)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                "Request timed out."));
        }
        catch (HttpRequestException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError", ex.Message));
        }
    }

    private static object TranslateMessage(Message m) => m.Role switch
    {
        Role.User => new { role = "user", content = m.Content },
        Role.Assistant when m.ToolCalls is { Count: > 0 } => new
        {
            role = "assistant",
            content = m.Content,
            tool_calls = m.ToolCalls.Select(t => new
            {
                id = t.Id,
                type = "function",
                function = new { name = t.Name, arguments = t.Arguments }
            }).ToArray()
        },
        Role.Assistant => new { role = "assistant", content = m.Content },
        Role.Tool => new { role = "tool", content = m.Content, tool_call_id = m.ToolCallId },
        _ => throw new ArgumentOutOfRangeException(nameof(m), m.Role, "Unknown role.")
    };

    private static object TranslateTool(ToolDefinition t) => new Dictionary<string, object?>
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = t.Parameters.ToDictionary(
                    p => p.Name,
                    p =>
                    {
                        var props = new Dictionary<string, object?>
                        {
                            ["type"] = p.Type == ToolParameterType.String ? "string" : "integer",
                            ["description"] = p.Description,
                        };
                        if (p.Minimum is { } min) props["minimum"] = min;
                        return props;
                    }),
                ["required"] = t.Parameters.Select(p => p.Name).ToArray(),
                ["additionalProperties"] = false,
            },
        }
    };
}
```

- [ ] **Step 5: Mechanical Agent fix — call site**

Replace `src/eThangAgent.Agent.Domain/Agent.cs`:

```csharp
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

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
        var request = new ModelRequest(Conversation.Messages);
        var result = await _provider.SendAsync(Config, request, ct);
        if (!result.IsSuccess)
            return Result<string>.Failure(result.Error!);
        Conversation.AddAssistantMessage(result.Value!.Content ?? "");
        return Result<string>.Success(result.Value.Content ?? "");
    }
}
```

- [ ] **Step 6: Update AgentTests — fake provider new signature**

Replace `tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs`:

```csharp
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.AgentDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentTests
{
    private static ModelConfig DefaultConfig =>
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    [Fact]
    public async Task SendMessage_OnSuccess_AddsBothMessages()
    {
        var provider = new FakeModelProvider(
            Result<ModelResponse>.Success(new ModelResponse("Hello back", [])));
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
        var provider = new FakeModelProvider(Result<ModelResponse>.Failure(error));
        var conversation = new Conversation();
        var agent = new Agent(provider, conversation, DefaultConfig);

        var result = await agent.SendMessage("Hi");

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
        Assert.Single(conversation.Messages);
        Assert.Equal(Role.User, conversation.Messages[0].Role);
    }

    [Fact]
    public async Task SendMessage_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new FakeModelProvider(Result<ModelResponse>.Success(new ModelResponse("ok", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig);

        var result = await agent.SendMessage("Hi", cts.Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Constructor_ExposesConversationAndConfig()
    {
        var provider = new FakeModelProvider(Result<ModelResponse>.Success(new ModelResponse("ok", [])));
        var conversation = new Conversation();
        var config = DefaultConfig;
        var agent = new Agent(provider, conversation, config);

        Assert.Same(conversation, agent.Conversation);
        Assert.Same(config, agent.Config);
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly Result<ModelResponse> _result;
        public FakeModelProvider(Result<ModelResponse> result) => _result = result;

        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult(Result<ModelResponse>.Failure(new Error("Cancelled", "Cancelled")));
            return Task.FromResult(_result);
        }
    }
}
```

- [ ] **Step 7: Verify Agent tests pass**

Run: `dotnet test tests/eThangAgent.Agent.Domain.Tests --nologo -v q`
Expected: All 4 pass.

- [ ] **Step 8: Rewrite OpenRouter ACL tests**

Replace `tests/eThangAgent.OpenRouter.ACL.Tests/OpenRouterModelProviderTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderTests
{
    private static readonly Uri BaseUrl = new("https://openrouter.test");
    private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

    private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsContent()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"Hello back"}}]}""")));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var config = ModelConfig.Create("openai/gpt-4o-mini", 256, 0.7f).Value!;

        var result = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]));

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value!.Content);
        Assert.Empty(result.Value.ToolCalls);
    }

    [Fact]
    public async Task SendAsync_SendsBearerTokenAndModel()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            captured = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("openai/gpt-4o-mini", 128, 0.7f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
        Assert.Equal("https://openrouter.test/api/v1/chat/completions", captured!.RequestUri!.ToString());
        Assert.Contains("openai/gpt-4o-mini", capturedBody);
    }

    [Fact]
    public async Task SendAsync_WhenToolsPresent_SerializesRequiredAndAdditionalProperties()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var tools = new List<ToolDefinition>
        {
            new("read", "desc",
            [
                new ToolParameter("path", ToolParameterType.String, "file path"),
                new ToolParameter("startLine", ToolParameterType.Integer, "start", Minimum: 1),
                new ToolParameter("endLine", ToolParameterType.Integer, "end", Minimum: 1),
            ])
        };

        await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")], tools));

        Assert.Contains("\"required\":[\"path\",\"startLine\",\"endLine\"]", capturedBody);
        Assert.Contains("\"additionalProperties\":false", capturedBody);
        Assert.Contains("\"minimum\":1", capturedBody);
    }

    [Fact]
    public async Task SendAsync_ParsesToolCallsFromResponse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"read","arguments":"{\\"path\\":\\"test.txt\\"}"}}]}}]}""")));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Content);
        Assert.Single(result.Value.ToolCalls);
        Assert.Equal("call_1", result.Value.ToolCalls[0].Id);
        Assert.Equal("read", result.Value.ToolCalls[0].Name);
        Assert.Contains("test.txt", result.Value.ToolCalls[0].Arguments);
    }

    [Fact]
    public async Task SendAsync_SendsToolMessageWithToolCallId()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"final"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var messages = new List<Message>
        {
            UserMsg("hi"),
            new(Role.Assistant, "", DateTimeOffset.UtcNow,
                [new ToolCall("call_1", "read", "{}")]),
            new(Role.Tool, "result content", DateTimeOffset.UtcNow, ToolCallId: "call_1"),
        };

        await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest(messages));

        Assert.Contains("\"role\":\"tool\"", capturedBody);
        Assert.Contains("\"tool_call_id\":\"call_1\"", capturedBody);
        Assert.Contains("result content", capturedBody);
    }

    [Fact]
    public async Task SendAsync_OnRateLimit_ReturnsRateLimitedError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.False(result.IsSuccess);
        Assert.Equal("RateLimited", result.Error!.Code);
    }

    [Fact]
    public async Task SendAsync_OnTimeout_ReturnsProviderTimeoutError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderTimeout", result.Error!.Code);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
```

- [ ] **Step 9: Run OpenRouter ACL tests**

Run: `dotnet test tests/eThangAgent.OpenRouter.ACL.Tests --nologo -v q`
Expected: 0 failed.

- [ ] **Step 10: Run full solution build + test suite**

```bash
dotnet build eThangAgent.slnx
dotnet test eThangAgent.slnx --nologo -v q
```

Expected: Build green. All existing tests pass.

- [ ] **Step 11: Commit**

```bash
git add src/eThangAgent.Model.Domain/ src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs src/eThangAgent.Agent.Domain/Agent.cs
git add tests/eThangAgent.OpenRouter.ACL.Tests/OpenRouterModelProviderTests.cs tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs
git commit -m "feat: tool-aware model provider contract, OpenRouter wire format for tools/tool_calls"
```

---

### Task 4: Agent tool loop

**Files:**

- Modify: `src/eThangAgent.Agent.Domain/Agent.cs`
- Modify: `tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs`

**Interfaces:**

- Consumes: `IModelProvider`, `Conversation`, `ModelConfig`, `IToolRegistry` (from Tasks 1-3)
- Produces: `Agent(IModelProvider, Conversation, ModelConfig, IToolRegistry, int maxToolIterations=10)`, `SendMessage` with tool loop

- [ ] **Step 1: Write failing tests for tool loop**

Replace `tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs`:

```csharp
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.AgentDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentTests
{
    private static ModelConfig DefaultConfig =>
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    [Fact]
    public async Task SendMessage_OnSuccess_AddsBothMessages()
    {
        var provider = new ScriptedModelProvider(
            Result<ModelResponse>.Success(new ModelResponse("Hello back", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

        var result = await agent.SendMessage("Hi");

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value);
        Assert.Equal(2, agent.Conversation.Messages.Count);
        Assert.Equal(Role.Assistant, agent.Conversation.Messages[1].Role);
    }

    [Fact]
    public async Task SendMessage_ProviderFailure_Propagates()
    {
        var err = new Error("Test", "fail");
        var provider = new ScriptedModelProvider(Result<ModelResponse>.Failure(err));
        var agent = new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

        var result = await agent.SendMessage("Hi");

        Assert.False(result.IsSuccess);
        Assert.Equal(err, result.Error);
    }

    [Fact]
    public async Task SendMessage_ToolCall_ExecutesAndFeedsResultBack()
    {
        var fakeTool = new FakeTool("read", "file content");
        var provider = new ScriptedModelProvider(
            Result<ModelResponse>.Success(new ModelResponse(null,
                [new ToolCallRequest("call_1", "read", "{\"p\":\"f\"}")])),
            Result<ModelResponse>.Success(new ModelResponse("done", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([fakeTool]));

        var result = await agent.SendMessage("read file");

        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.Value);
        Assert.Equal(4, agent.Conversation.Messages.Count);
        Assert.Equal(Role.User, agent.Conversation.Messages[0].Role);
        Assert.Equal(Role.Assistant, agent.Conversation.Messages[1].Role);
        Assert.Equal(Role.Tool, agent.Conversation.Messages[2].Role);
        Assert.Equal("file content", agent.Conversation.Messages[2].Content);
        Assert.Equal(Role.Assistant, agent.Conversation.Messages[3].Role);
        Assert.Equal("done", agent.Conversation.Messages[3].Content);
    }

    [Fact]
    public async Task SendMessage_UnknownTool_ReturnsErrorToolResult()
    {
        var provider = new ScriptedModelProvider(
            Result<ModelResponse>.Success(new ModelResponse(null,
                [new ToolCallRequest("call_1", "nope", "{}")])),
            Result<ModelResponse>.Success(new ModelResponse("final", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([]));

        var result = await agent.SendMessage("hi");

        Assert.True(result.IsSuccess);
        var toolMsg = agent.Conversation.Messages[2];
        Assert.Equal(Role.Tool, toolMsg.Role);
        Assert.Contains("Unknown tool", toolMsg.Content);
    }

    [Fact]
    public async Task SendMessage_MaxIterationsExhausted_ReturnsFailure()
    {
        var provider = new ScriptedModelProvider(
            Enumerable.Repeat(
                Result<ModelResponse>.Success(new ModelResponse(null,
                    [new ToolCallRequest("c1", "loopy", "{}")])),
                10).ToArray());
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([new FakeTool("loopy", "again")]), maxToolIterations: 10);

        var result = await agent.SendMessage("hi");

        Assert.False(result.IsSuccess);
        Assert.Equal("MaxToolIterations", result.Error!.Code);
    }

    private sealed class ScriptedModelProvider : IModelProvider
    {
        private readonly Queue<Result<ModelResponse>> _responses;
        public ScriptedModelProvider(params Result<ModelResponse>[] responses)
            => _responses = new Queue<Result<ModelResponse>>(responses);

        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct)
            => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
                : Result<ModelResponse>.Success(new ModelResponse("fin", [])));
    }

    private sealed class FakeTool : ITool
    {
        private readonly string _resultContent;
        public ToolDefinition Definition { get; }
        public FakeTool(string name, string resultContent)
        {
            Definition = new ToolDefinition(name, "desc", []);
            _resultContent = resultContent;
        }
        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
            => Task.FromResult(new ToolResult(_resultContent, false));
    }
}
```

- [ ] **Step 2: Run tests — verify build failure**

Run: `dotnet test tests/eThangAgent.Agent.Domain.Tests --nologo -v q`
Expected: Compile error — `Agent` ctor no longer matches (missing `IToolRegistry`), `ToolRegistry` unknown.

- [ ] **Step 3: Implement Agent tool loop**

Replace `src/eThangAgent.Agent.Domain/Agent.cs`:

```csharp
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public class Agent
{
    private readonly IModelProvider _provider;
    private readonly IToolRegistry _tools;
    private readonly int _maxToolIterations;

    public Conversation Conversation { get; }
    public ModelConfig Config { get; }

    public Agent(IModelProvider provider, Conversation conversation, ModelConfig config,
        IToolRegistry tools, int maxToolIterations = 10)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _maxToolIterations = maxToolIterations;
    }

    public async Task<Result<string>> SendMessage(string text, CancellationToken ct = default)
    {
        Conversation.AddUserMessage(text);
        for (var i = 0; i < _maxToolIterations; i++)
        {
            var request = new ModelRequest(Conversation.Messages, _tools.Definitions);
            var result = await _provider.SendAsync(Config, request, ct);
            if (!result.IsSuccess)
                return Result<string>.Failure(result.Error!);

            var response = result.Value!;
            if (response.ToolCalls.Count == 0)
            {
                var content = response.Content ?? "";
                Conversation.AddAssistantMessage(content);
                return Result<string>.Success(content);
            }

            Conversation.AddAssistantMessage(response.Content ?? "",
                response.ToolCalls
                    .Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments))
                    .ToList());

            foreach (var call in response.ToolCalls)
            {
                var tool = _tools.Find(call.Name);
                var toolResult = tool is null
                    ? new ToolResult($"Error [UnknownTool]: Unknown tool: {call.Name}.", true)
                    : await tool.ExecuteAsync(new RawToolInput(call.Name, call.Arguments), ct);
                Conversation.AddToolResult(call.Id, toolResult.Content);
            }
        }

        return Result<string>.Failure(new Error("MaxToolIterations",
            $"Tool loop did not converge after {_maxToolIterations} iterations."));
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/eThangAgent.Agent.Domain.Tests --nologo -v q`
Expected: All 6 pass.

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.Agent.Domain/Agent.cs tests/eThangAgent.Agent.Domain.Tests/AgentTests.cs
git commit -m "feat: add tool-execution loop to Agent aggregate"
```

---

### Task 5: ReadTool — validation + execution + formatting

**Files:**

- Create: `src/eThangAgent.Tool.Domain/ReadToolInput.cs`
- Create: `src/eThangAgent.Tool.Domain/ReadTool.cs`
- Create: `tests/eThangAgent.Tool.Domain.Tests/ReadToolTests.cs`

**Interfaces:**

- Consumes: `ToolParameter`, `ToolDefinition`, `RawToolInput`, `ToolResult`, `ITool`, `IFileSystemAccess`, `FileRead`, `Result<T>`, `Error` (from Task 1)
- Produces: `ReadToolInput.Create(string json) → Result<ReadToolInput>`, `ReadTool : ITool` with full validation matrix and line-numbered output formatting

- [ ] **Step 1: Read existing test file to preserve**

Read `tests/eThangAgent.Tool.Domain.Tests/ToolRegistryTests.cs` — confirm it exists and is unchanged.

- [ ] **Step 2: Write the validation-matrix tests**

Create `tests/eThangAgent.Tool.Domain.Tests/ReadToolTests.cs`:

```csharp
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Xunit.Abstractions;

namespace eThangAgent.ToolDomain.Tests;

public class ReadToolTests
{
    private readonly ITestOutputHelper _out;
    public ReadToolTests(ITestOutputHelper @out) => _out = @out;

    private static ReadTool MakeTool(Result<FileRead> readResult) =>
        new(new FakeFileSystemAccess(readResult));

    private static ReadTool MakeTool(FileRead success) =>
        new(new FakeFileSystemAccess(Result<FileRead>.Success(success)));

    // ---- JSON parsing ----

    [Fact]
    public async Task RawArguments_NotJson_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read", "not json"));
        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content);
    }

    [Fact]
    public async Task RawArguments_NotObject_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read", "[1,2,3]"));
        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    // ---- Missing parameters ----

    [Fact]
    public async Task MissingPath_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"startLine":1,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("Missing required", result.Content);
        Assert.Contains("path", result.Content);
    }

    [Fact]
    public async Task MissingStartLine_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
    }

    [Fact]
    public async Task MissingEndLine_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1}"""));
        Assert.True(result.IsError);
        Assert.Contains("endLine", result.Content);
    }

    // ---- Wrong types ----

    [Fact]
    public async Task StartLineIsString_ReturnsTypeError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":"1","endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartLineIsFloat_ReturnsTypeError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1.5,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PathIsNumber_ReturnsTypeError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":123,"startLine":1,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("path", result.Content);
        Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Unknown parameters ----

    [Fact]
    public async Task ExtraParameter_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1,"endLine":5,"encoding":"utf16"}"""));
        Assert.True(result.IsError);
        Assert.Contains("encoding", result.Content);
        Assert.Contains("Unknown parameter", result.Content);
    }

    // ---- Value constraints ----

    [Fact]
    public async Task StartLine_LessThan1_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":0,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("≥ 1", result.Content);
    }

    [Fact]
    public async Task StartLine_GreaterThanEndLine_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":10,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("must not exceed", result.Content);
    }

    // ---- Range cap ----

    [Fact]
    public async Task RangeExceeds1000_ReturnsError()
    {
        var tool = MakeTool(null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1,"endLine":2000}"""));
        Assert.True(result.IsError);
        Assert.Contains("1000", result.Content);
        Assert.Contains("chunks", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Execution (happy path) ----

    [Fact]
    public async Task SuccessfulRead_ReturnsFormattedContent()
    {
        var fileRead = new FileRead(["alpha", "beta", "gamma"], 3, 5);
        var tool = MakeTool(fileRead);

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"doc.txt","startLine":1,"endLine":3}"""));

        Assert.False(result.IsError);
        Assert.StartsWith("[read doc.txt lines 1-3 of 5 total]", result.Content);
        Assert.Contains("1→ alpha", result.Content);
        Assert.Contains("2→ beta", result.Content);
        Assert.Contains("3→ gamma", result.Content);
    }

    [Fact]
    public async Task Gutter_RightAlignsToLastLineNumberWidth()
    {
        var fileRead = new FileRead(["a", "b"], 10, 100);
        var tool = MakeTool(fileRead);

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":9,"endLine":10}"""));

        // line 9 → 1 digit, line 10 → 2 digits, gutter width = 2
        Assert.Contains(" 9→ a", result.Content);  // space + 9 + arrow
        Assert.Contains("10→ b", result.Content);  // no leading space
    }

    // ---- Clamp (endLine past EOF) ----

    [Fact]
    public async Task EndLinePastEof_ClampsAndWarns()
    {
        var fileRead = new FileRead(["one", "two", "three"], 3, 3);
        var tool = MakeTool(fileRead);

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"small.txt","startLine":1,"endLine":100}"""));

        Assert.False(result.IsError);
        Assert.StartsWith("[read small.txt lines 1-3 of 3 total]", result.Content);
        Assert.EndsWith("clamped", result.Content);
        Assert.Contains("[warning]", result.Content);
        Assert.Contains("100", result.Content);  // the requested endLine in warning
    }

    // ---- StartLine beyond EOF ----

    [Fact]
    public async Task StartLineBeyondEof_ReturnsError()
    {
        var fileRead = new FileRead([], 0, 10);
        var tool = MakeTool(Result<FileRead>.Success(fileRead));

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"short.txt","startLine":20,"endLine":25}"""));

        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("20", result.Content);
        Assert.Contains("10", result.Content);  // file length
    }

    // ---- Empty file ----

    [Fact]
    public async Task EmptyFile_StartLine1_ReturnsError()
    {
        var fileRead = new FileRead([], 0, 0);
        var tool = MakeTool(Result<FileRead>.Success(fileRead));

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"empty.txt","startLine":1,"endLine":1}"""));

        Assert.True(result.IsError);
        Assert.Contains("file length (0 lines)", result.Content);
    }

    // ---- File not found ----

    [Fact]
    public async Task FileNotFound_ReturnsError()
    {
        var tool = MakeTool(Result<FileRead>.Failure(new Error("FileNotFound", "File not found: nope.txt")));

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"nope.txt","startLine":1,"endLine":5}"""));

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content);
    }

    // ---- ToolDefinition ----

    [Fact]
    public void Definition_HasCorrectNameAndThreeParams()
    {
        var tool = new ReadTool(new FakeFileSystemAccess(null!));

        Assert.Equal("read", tool.Definition.Name);
        Assert.Equal(3, tool.Definition.Parameters.Count);
        Assert.Contains(tool.Definition.Parameters, p => p.Name == "path");
        Assert.Contains(tool.Definition.Parameters, p => p.Name == "startLine" && p.Minimum == 1);
        Assert.Contains(tool.Definition.Parameters, p => p.Name == "endLine" && p.Minimum == 1);
    }

    // ---- Helpers ----

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        private readonly Result<FileRead> _result;
        public FakeFileSystemAccess(Result<FileRead> result) => _result = result;
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
            => Task.FromResult(_result);
    }
}
```

- [ ] **Step 3: Run tests — verify they fail (ReadTool not found)**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --nologo -v q`
Expected: Compile error — `ReadTool`, `ReadToolInput` do not exist.

- [ ] **Step 4: Implement ReadToolInput**

Create `src/eThangAgent.Tool.Domain/ReadToolInput.cs`:

```csharp
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ReadToolInput(string Path, int StartLine, int EndLine)
{
    public const int MaxRangeLines = 1000;

    public static Result<ReadToolInput> Create(string jsonArguments)
    {
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Failure(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }

        if (json.ValueKind != JsonValueKind.Object)
            return Failure(new Error("InvalidJsonArguments",
                "Arguments must be a JSON object."));

        var known = new HashSet<string>(["path", "startLine", "endLine"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Failure(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, startLine, endLine."));

        if (!json.TryGetProperty("path", out var pathEl))
            return Missing("path");
        if (pathEl.ValueKind != JsonValueKind.String)
            return WrongType("path", "string", pathEl.ValueKind);
        var path = pathEl.GetString()!;
        if (path.Length == 0)
            return Failure(new Error("InvalidParameterValue",
                "'path' must be a non-empty string."));

        if (!json.TryGetProperty("startLine", out var startEl))
            return Missing("startLine");
        if (startEl.ValueKind != JsonValueKind.Number || !startEl.TryGetInt32(out var startLine))
            return WrongType("startLine", "integer", startEl.ValueKind);

        if (!json.TryGetProperty("endLine", out var endEl))
            return Missing("endLine");
        if (endEl.ValueKind != JsonValueKind.Number || !endEl.TryGetInt32(out var endLine))
            return WrongType("endLine", "integer", endEl.ValueKind);

        if (startLine < 1)
            return Failure(new Error("InvalidParameterValue",
                $"'startLine' must be ≥ 1 (got {startLine})."));
        if (endLine < 1)
            return Failure(new Error("InvalidParameterValue",
                $"'endLine' must be ≥ 1 (got {endLine})."));
        if (startLine > endLine)
            return Failure(new Error("InvalidParameterValue",
                $"'startLine' ({startLine}) must not exceed 'endLine' ({endLine})."));

        var span = (long)endLine - startLine + 1;
        if (span > MaxRangeLines)
            return Failure(new Error("RangeTooLarge",
                $"Range spans {span} lines; maximum is {MaxRangeLines}. " +
                $"Read in chunks (e.g. {startLine}-{startLine + MaxRangeLines - 1}, " +
                $"{startLine + MaxRangeLines}-{Math.Min(startLine + 2 * MaxRangeLines - 1, endLine)})."));

        return Result<ReadToolInput>.Success(new ReadToolInput(path, startLine, endLine));
    }

    private static Result<ReadToolInput> Missing(string name) =>
        Result<ReadToolInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{name}'. This tool requires path, startLine, and endLine."));

    private static Result<ReadToolInput> WrongType(string name, string expected, JsonValueKind actual) =>
        Result<ReadToolInput>.Failure(new Error("InvalidParameterType",
            $"'{name}' must be a {expected}, but got {actual}."));

    private static Result<ReadToolInput> Failure(Error error) =>
        Result<ReadToolInput>.Failure(error);
}
```

- [ ] **Step 5: Implement ReadTool**

Create `src/eThangAgent.Tool.Domain/ReadTool.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ReadTool : ITool
{
    private readonly IFileSystemAccess _files;

    public ToolDefinition Definition { get; } = new(
        "read",
        "Read a range of lines from a text file. path, startLine, and endLine are all mandatory; line numbers are 1-based and inclusive. Output begins with an annotation line in [brackets] — it is metadata, not file content. Each content line is prefixed with its line number and →; the number and arrow are never part of the file. Never reproduce line numbers or arrows when creating or editing files. Cite line numbers as shown when referencing locations. If endLine exceeds the file length it is clamped and a [warning] is appended. Maximum range: 1000 lines per call.",
        [
            new ToolParameter("path", ToolParameterType.String, "Path to the file to read."),
            new ToolParameter("startLine", ToolParameterType.Integer, "First line to read (1-based, inclusive).", Minimum: 1),
            new ToolParameter("endLine", ToolParameterType.Integer, "Last line to read (1-based, inclusive).", Minimum: 1),
        ]);

    public ReadTool(IFileSystemAccess files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = ReadToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Error(parsed.Error!);

        var read = await _files.ReadLinesAsync(parsed.Value!.Path, parsed.Value.StartLine, parsed.Value.EndLine, ct);
        if (!read.IsSuccess)
            return Error(read.Error!);

        var file = read.Value!;
        if (file.LastLineRead == 0)
            return Error(new Error("StartLineBeyondEof",
                $"'startLine' {parsed.Value.StartLine} exceeds file length ({file.TotalLines} lines)."));

        var clamped = file.TotalLines < parsed.Value.EndLine;
        var last = clamped ? file.TotalLines : parsed.Value.EndLine;
        var width = last.ToString().Length;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[read {parsed.Value.Path} lines {parsed.Value.StartLine}-{last} of {file.TotalLines} total]");
        foreach (var (text, i) in file.Lines.Select((t, i) => (t, i)))
            sb.AppendLine($"{(parsed.Value.StartLine + i).ToString().PadLeft(width)}→ {text}");
        if (clamped)
            sb.Append($"[warning] endLine {parsed.Value.EndLine} exceeded file length ({file.TotalLines}); clamped");
        else
            sb.Length -= Environment.NewLine.Length;  // trim trailing newline

        return new ToolResult(sb.ToString(), false);
    }

    private static ToolResult Error(Error error) => new(
        $"Error [{error.Code}]: {error.Message}", true);
}
```

- [ ] **Step 6: Run ReadTool tests**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --nologo -v q`
Expected: 22 passed (5 ToolRegistry + 17 ReadTool).

- [ ] **Step 7: Commit**

```bash
git add src/eThangAgent.Tool.Domain/ReadToolInput.cs src/eThangAgent.Tool.Domain/ReadTool.cs tests/eThangAgent.Tool.Domain.Tests/ReadToolTests.cs
git commit -m "feat: add ReadTool with strict input validation and line-numbered output"
```

---

### Task 6: FileSystem.ACL — PowerShell runspace

**Files:**

- Create: `src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj`
- Create: `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs`
- Create: `tests/eThangAgent.FileSystem.ACL.Tests/eThangAgent.FileSystem.ACL.Tests.csproj`
- Create: `tests/eThangAgent.FileSystem.ACL.Tests/GlobalUsings.cs`
- Create: `tests/eThangAgent.FileSystem.ACL.Tests/PowerShellFileSystemAccessTests.cs`
- Modify: `eThangAgent.slnx`

**Interfaces:**

- Consumes: `IFileSystemAccess`, `FileRead`, `Result<T>`, `Error` (from Task 1)
- Produces: `PowerShellFileSystemAccess : IFileSystemAccess, IDisposable` (in-process PS runspace)

- [ ] **Step 1: Create projects**

```bash
mkdir "src\eThangAgent.FileSystem.ACL"
mkdir "tests\eThangAgent.FileSystem.ACL.Tests"
```

Create `src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="System.Management.Automation" Version="7.4.*" />
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/eThangAgent.FileSystem.ACL.Tests/eThangAgent.FileSystem.ACL.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/eThangAgent.FileSystem.ACL.Tests/GlobalUsings.cs`:

```csharp
global using Xunit;
```

Edit `eThangAgent.slnx` — add after the Tool.Domain project line:

```xml
  <Project Path="src/eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj" />
```

And after the last `<Project Path="tests/` line:

```xml
  <Project Path="tests/eThangAgent.FileSystem.ACL.Tests/eThangAgent.FileSystem.ACL.Tests.csproj" />
```

- [ ] **Step 2: Install NuGet package and verify build**

```bash
cd src/eThangAgent.FileSystem.ACL
dotnet restore
dotnet build
```

Expected: `System.Management.Automation` restored; build succeeded.

- [ ] **Step 3: Write the integration tests**

Create `tests/eThangAgent.FileSystem.ACL.Tests/PowerShellFileSystemAccessTests.cs`:

```csharp
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

public class PowerShellFileSystemAccessTests : IDisposable
{
    private readonly PowerShellFileSystemAccess _access = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ethang-fs-{Guid.NewGuid():N}");

    public PowerShellFileSystemAccessTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string name, params string[] lines)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task MiddleRange_ReturnsRequestedLines()
    {
        var path = WriteFile("test.txt", "a", "b", "c", "d", "e");

        var result = await _access.ReadLinesAsync(path, 2, 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(["b", "c", "d"], result.Value!.Lines);
        Assert.Equal(4, result.Value.LastLineRead);
        Assert.Equal(5, result.Value.TotalLines);
    }

    [Fact]
    public async Task ExactEof_ReturnsAllRequested_NoClampNeeded()
    {
        var path = WriteFile("test.txt", "a", "b", "c");

        var result = await _access.ReadLinesAsync(path, 1, 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b", "c"], result.Value!.Lines);
        Assert.Equal(3, result.Value.LastLineRead);
        Assert.Equal(3, result.Value.TotalLines);
    }

    [Fact]
    public async Task EndPastEof_ReturnsAllLines_TotalLinesKnown()
    {
        var path = WriteFile("test.txt", "a", "b");

        var result = await _access.ReadLinesAsync(path, 1, 100);

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b"], result.Value!.Lines);
        Assert.Equal(2, result.Value.LastLineRead);
        Assert.Equal(2, result.Value.TotalLines);
    }

    [Fact]
    public async Task StartBeyondEof_LastLineIsZero()
    {
        var path = WriteFile("test.txt", "a", "b");

        var result = await _access.ReadLinesAsync(path, 10, 15);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Lines);
        Assert.Equal(0, result.Value.LastLineRead);
        Assert.Equal(2, result.Value.TotalLines);
    }

    [Fact]
    public async Task MissingFile_ReturnsFileNotFoundError()
    {
        var result = await _access.ReadLinesAsync(Path.Combine(_tempDir, "nope.txt"), 1, 5);

        Assert.False(result.IsSuccess);
        Assert.Equal("FileNotFound", result.Error!.Code);
    }

    [Fact]
    public async Task EmptyFile_ReturnsZeroLineZeroTotal()
    {
        var path = WriteFile("empty.txt");

        var result = await _access.ReadLinesAsync(path, 1, 10);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Lines);
        Assert.Equal(0, result.Value.LastLineRead);
        Assert.Equal(0, result.Value.TotalLines);
    }

    [Fact]
    public async Task CRLF_IsNormalized()
    {
        var path = Path.Combine(_tempDir, "crlf.txt");
        File.WriteAllText(path, "line1\r\nline2\r\n", Encoding.UTF8);

        var result = await _access.ReadLinesAsync(path, 1, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(["line1", "line2"], result.Value!.Lines);
        Assert.False(result.Value.Lines.Any(l => l.Contains('\r')));
    }

    [Fact]
    public async Task Utf8Bom_ContentReadCorrectly()
    {
        var path = Path.Combine(_tempDir, "bom.txt");
        File.WriteAllText(path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await _access.ReadLinesAsync(path, 1, 1);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value!.Lines.Single());
    }

    [Fact]
    public async Task RunspaceReuse_TwoSequentialReadsBothSucceed()
    {
        var path1 = WriteFile("a.txt", "aa");
        var path2 = WriteFile("b.txt", "bb");

        var r1 = await _access.ReadLinesAsync(path1, 1, 1);
        var r2 = await _access.ReadLinesAsync(path2, 1, 1);

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.Equal("aa", r1.Value!.Lines.Single());
        Assert.Equal("bb", r2.Value!.Lines.Single());
    }

    [Fact]
    public async Task LargeFile_IsFast()
    {
        var path = Path.Combine(_tempDir, "large.txt");
        var lines = Enumerable.Range(1, 50_000).Select(i => $"line {i}");
        File.WriteAllLines(path, lines, Encoding.UTF8);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _access.ReadLinesAsync(path, 40_001, 40_100);
        watch.Stop();

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value!.Lines.Count);
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"took {watch.ElapsedMilliseconds}ms");
    }
}
```

- [ ] **Step 4: Run tests — verify fail (PowerShellFileSystemAccess not found)**

Run: `dotnet test tests/eThangAgent.FileSystem.ACL.Tests --nologo -v q`
Expected: Compile error — `PowerShellFileSystemAccess` does not exist.

- [ ] **Step 5: Implement PowerShellFileSystemAccess**

Create `src/eThangAgent.FileSystem.ACL/PowerShellFileSystemAccess.cs`:

```csharp
using System.Collections;
using System.Management.Automation;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class PowerShellFileSystemAccess : IFileSystemAccess, IDisposable
{
    private const string Script = """
        param([string]$Path, [int]$Start, [int]$End)
        $exists = [System.IO.File]::Exists($Path)
        if (-not $exists) { return @{ Found = $false } }
        $reader = [System.IO.File]::OpenText($Path)
        try {
            $lines = [System.Collections.Generic.List[string]]::new()
            $i = 0; $last = 0
            while ($true) {
                $line = $reader.ReadLine()
                if ($null -eq $line) { break }
                $i++
                if ($i -ge $Start) { [void]$lines.Add($line); $last = $i }
                if ($i -ge $End) {
                    # Drain remaining lines to count total lines accurately
                    while ($null -ne $reader.ReadLine()) { $i++ }
                    break
                }
            }
            return @{ Found = $true; Lines = $lines; LastLine = $last; TotalLines = $i }
        } finally { $reader.Dispose() }
        """;

    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellFileSystemAccess()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
    }

    public async Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var ps = PowerShell.Create(_runspace);
            ps.AddScript(Script)
              .AddParameter("Path", path)
              .AddParameter("Start", startLine)
              .AddParameter("End", endLine);

            var output = ps.Invoke();

            if (ps.HadErrors)
            {
                var msg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message
                    ?? "Unknown PowerShell error.";
                return Result<FileRead>.Failure(new Error("FileSystemError", msg));
            }
            if (output.Count == 0)
                return Result<FileRead>.Failure(new Error("FileSystemError",
                    "PowerShell script produced no output."));

            var table = (Hashtable)output[0].BaseObject;
            var found = table["Found"] is true;
            if (!found)
                return Result<FileRead>.Failure(new Error("FileNotFound",
                    $"File not found: {path}"));

            var rawLines = (IEnumerable)table["Lines"]!;
            var lines = rawLines.Cast<object>()
                .Select(o => o is PSObject pso ? pso.BaseObject?.ToString() ?? "" : o.ToString() ?? "")
                .ToList();
            var lastLine = Convert.ToInt32(table["LastLine"]);
            var totalLines = Convert.ToInt32(table["TotalLines"]);

            return Result<FileRead>.Success(new FileRead(lines, lastLine, totalLines));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _runspace.Dispose();
        _gate.Dispose();
    }
}
```

- [ ] **Step 6: Run integration tests**

Run: `dotnet test tests/eThangAgent.FileSystem.ACL.Tests --nologo -v q`
Expected: 0 failed (integration tests exercise real PowerShell runspace on temp files).

- [ ] **Step 7: Run full solution build + test**

```bash
dotnet build eThangAgent.slnx
dotnet test eThangAgent.slnx --nologo -v q
```

Expected: Build green. All tests pass.

- [ ] **Step 8: Commit**

```bash
git add eThangAgent.slnx src/eThangAgent.FileSystem.ACL/ tests/eThangAgent.FileSystem.ACL.Tests/
git commit -m "feat: add PowerShell-runspace FileSystem.ACL for file reading"
```

---

### Task 7: CLI wiring + E2E tool-call flow

**Files:**

- Modify: `src/eThangAgent.CLI/eThangAgent.CLI.csproj`
- Modify: `src/eThangAgent.CLI/Program.cs`
- Modify: `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs`
- Modify: `tests/eThangAgent.CLI.Tests/E2ETests.cs`

**Interfaces:**

- Consumes: `ReadTool`, `ToolRegistry`, `IToolRegistry`, `PowerShellFileSystemAccess`, `IFileSystemAccess` (from Tasks 1, 5, 6); `Agent` (from Task 4)

- [ ] **Step 1: Add project references to CLI**

Replace `src/eThangAgent.CLI/eThangAgent.CLI.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.*" />
    <ProjectReference Include="../eThangAgent.Agent.Application/eThangAgent.Agent.Application.csproj" />
    <ProjectReference Include="../eThangAgent.OpenRouter.ACL/eThangAgent.OpenRouter.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.Terminal.ACL/eThangAgent.Terminal.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Wire DI registrations in Program.cs**

Edit `src/eThangAgent.CLI/Program.cs`:

Add after existing `using eThangAgent.OpenRouter.ACL;`:

```csharp
using eThangAgent.ToolDomain;
using eThangAgent.FileSystem.ACL;
```

Replace the `AddSingleton<Ag>` block (the lambda registering Agent):

```csharp
            .AddSingleton<IFileSystemAccess, PowerShellFileSystemAccess>()
            .AddSingleton<ITool>(sp => new ReadTool(sp.GetRequiredService<IFileSystemAccess>()))
            .AddSingleton<IToolRegistry>(sp => new ToolRegistry([sp.GetRequiredService<ITool>()]))
            .AddSingleton<Ag>(sp =>
            {
                var provider = sp.GetRequiredService<IModelProvider>();
                var conversation = sp.GetRequiredService<Conversation>();
                var config = sp.GetRequiredService<ModelConfig>();
                var tools = sp.GetRequiredService<IToolRegistry>();
                return new Ag(provider, conversation, config, tools);
            })
```

Also add the missing `using eThangAgent.AgentDomain;` at top if not present (it's already aliased as `Ag = ...`).

- [ ] **Step 3: Build the CLI**

Run: `dotnet build src/eThangAgent.CLI/eThangAgent.CLI.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Update MockOpenRouterServer to support scripted responses**

Edit `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs`:

Add field:

```csharp
    private readonly Queue<string> _scriptedResponses = new();
    public List<string> RequestBodies { get; } = new();
```

Add method:

```csharp
    public MockOpenRouterServer Returns(string responseJson)
    {
        _scriptedResponses.Enqueue(responseJson);
        return this;
    }
```

In `LoopAsync`, replace the body reading + response block:

```csharp
                using var reader = new StreamReader(ctx.Request.InputStream);
                var requestBody = await reader.ReadToEndAsync();
                LastChatRequestBody = requestBody;
                RequestBodies.Add(requestBody);

                string body;
                if (_scriptedResponses.Count > 0)
                    body = _scriptedResponses.Dequeue();
                else
                    body = """{"choices":[{"message":{"content":"pineapple"}}]}""";
```

- [ ] **Step 5: Write the E2E tool-call test**

Append to `tests/eThangAgent.CLI.Tests/E2ETests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Repl_ExecutesReadTool_EndToEnd()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        var tempFile = Path.Combine(Path.GetTempPath(), $"ethang-e2e-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line", "gamma line"]);

        var toolArgs = System.Text.Json.JsonSerializer.Serialize(
            new { path = tempFile, startLine = 2, endLine = 3 });
        var toolCallResponse = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call_1",
                                type = "function",
                                function = new { name = "read", arguments = toolArgs }
                            }
                        }
                    }
                }
            }
        });
        mock.Returns(toolCallResponse);
        mock.Returns("""{"choices":[{"message":{"content":"read completed"}}]}""");

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("read that file");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("read completed", response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, mock.RequestBodies.Count);
        Assert.Contains("\"role\":\"tool\"", mock.RequestBodies[1]);
        Assert.Contains("beta line", mock.RequestBodies[1]);
        Assert.Contains("gamma line", mock.RequestBodies[1]);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(tempFile); } catch { }
    }
```

- [ ] **Step 6: Run the E2E test**

```bash
dotnet build eThangAgent.slnx
dotnet test tests/eThangAgent.CLI.Tests --nologo -v q --filter "FullyQualifiedName~Repl_ExecutesReadTool"
```

Expected: Single test passes — full tool-call loop proved end-to-end.

- [ ] **Step 7: Run full test suite**

```bash
dotnet test eThangAgent.slnx --nologo -v q
```

Expected: All tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/eThangAgent.CLI/ tests/eThangAgent.CLI.Tests/
git commit -m "feat: wire Tool Domain + FileSystem ACL into CLI, add E2E tool-call flow"
```

---

### Task 8: Full verification + live smoke instructions

- [ ] **Step 1: Clean build**

```bash
dotnet clean eThangAgent.slnx
dotnet build eThangAgent.slnx
```

Expected: No errors, no warnings.

- [ ] **Step 2: Full test suite**

```bash
dotnet test eThangAgent.slnx --nologo
dotnet test eThangAgent.slnx --nologo --collect:"XPlat Code Coverage"
```

Expected: 0 failed across all test projects.

- [ ] **Step 3: Manual live smoke test**

```bash
$env:OPENROUTER_API_KEY = "<your real key>"
dotnet run --project src/eThangAgent.CLI
```

Then at the prompt:

```
> read the first 10 lines of src/eThangAgent.CLI/Program.cs using your read tool
```

Expected: The model calls `read`, the tool executes, and the model responds with a description of the file contents. The output should show line-numbered content from Program.cs.

- [ ] **Step 4: Commit**

```bash
git add -A
git status  # verify nothing forgotten
git commit -m "chore: final verification — full build and test suite green"
```
