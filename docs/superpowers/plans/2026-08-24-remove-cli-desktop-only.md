# Remove the CLI — Desktop-Only Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete `eThangAgent.CLI` and `eThangAgent.Terminal.ACL` (plus their test projects), porting the valuable piped-E2E coverage into headless `Desktop.Tests` so the Avalonia app becomes the sole frontend with no coverage loss.

**Architecture:** Both frontends already share one core (`eThangAgent.Composition.AddEThangAgentCore`). The desktop's `MainViewModel.SubmitAsync`/`WaitForTurnAsync` drives real turns deterministically, so E2E scenarios migrate from process-piping (`CLI.exe` + stdin/stdout scraping) to in-process headless runs (real composition → real OpenRouter client → local mock server → view-model). Assertions move from stdout scraping to decoded tool-message content in captured request bodies and typed transcript entries.

**Tech Stack:** .NET 10, C#, xUnit, Avalonia.Headless.XUnit, `HttpListener` mock server.

**Spec:** `docs/superpowers/specs/2026-08-24-remove-cli-desktop-only-design.md`

## Global Constraints

- Every change leaves the build green: `dotnet build` and `dotnet test` pass at each task boundary.
- Windows-only; PowerShell is the only shell (`.ps1`, no `.sh`/`.cmd`/`.bat`).
- No changes to any domain project, ACL (other than deleting Terminal.ACL), or `eThangAgent.Composition` internals.
- Strict input validation everywhere; expected failures are `Result<T>` errors, never exceptions.
- Tests use the existing in-repo `MockOpenRouterServer` twin in `Desktop.Tests` — do not introduce a new mock or an HTTP mocking package.
- Default session model id is exactly `stealth/ox-alpha` (matches production wiring in `DesktopHost.PrepareAsync`).
- File-writing caveat for agents in this workspace: the harness `write`/`edit` tools misvalidate in-workspace paths (`Error [PathOutsideWorkspace]`); create/modify files with `System.IO` inside exec scripts instead. Multi-token shell commands go through `Shell("powershell", "<command>")`.
- Conventional commits, lowercase type prefixes (`test:`, `chore:`, `docs:`).

## Verified facts this plan relies on

- `MainViewModel` ctor: `(TurnRunner runner, RootSessionLifecycle lifecycle, AgentId rootId, Conversation conversation, string modelId, Action? requestClose, Func<UiStreamEvent, Task>? uiStreamSink = null)`; drive turns with `await vm.SubmitAsync(text); await vm.WaitForTurnAsync();`. `vm.Transcript.Entries` is `ObservableCollection<TranscriptEntry>` with variants `UserMessageEntry(string Text)`, `AssistantTextEntry(string Text)`, `ReasoningEntry`, `ToolCallEntry(string Name, string Arguments)`, `ToolResultEntry(string Name, string Summary)`, `NoticeEntry` (namespace `eThangAgent.Desktop.ViewModels`).
- Working service build (proven by `DesktopPipelineSmokeTests`): `new ServiceCollection().AddEThangAgentCore(settings, apiKey, ModelConfig.Create(...).Value!, new AgentHostOptions(clarifyChannel, new FixedWorkspaceContext("app"), new UnrootedPathResolver())).BuildServiceProvider()` where `settings = new AgentSettings(apiKey, new Uri(mock.BaseUrl), new SubAgentOptions(null, TimeSpan.FromSeconds(30), maxConcurrent), MaxToolIterationsConfiguration.Default)`.
- `AppDatabase` resolves its path: explicit ctor arg → `ETHANG_AGENT_DB` env var → `%LOCALAPPDATA%`. Composition registers `AddSingleton<AppDatabase>()` with no arg, so setting `ETHANG_AGENT_DB` isolates storage. All E2E test classes join one xUnit collection to serialize env-var use.
- `MockOpenRouterServer` (Desktop.Tests): `Start()`, `Returns(json)`, `ReturnsForModel(model, params json[])`, `RequestBodies` (List<string>), `LastChatRequestBody`, `TryGetRequestModel(body)`, `ChildIdPlaceholder` (`{{child_id}}`), `IDisposable`.
- Scripted response shapes: plain completion `{"choices":[{"message":{"content":"..."}}]}` (as a raw-string literal in tests); exec tool call via the `ExecToolCall(id, argumentsJson)` helper below.
- The piped CLI E2E source being ported lives at `tests/eThangAgent.CLI.Tests/E2ETests.cs`; its JSON-decode helpers (`ExecProgram`, `ExecToolCall`, `FindToolMessageContaining`, `GetLastToolMessage`) move into the shared fixture, and scenario bodies carry over with stdout-scraping replaced by transcript/request-body assertions.

---

### Task 1: Move `SuperpowersBootstrapTests` into Composition.Tests

**Files:**
- Create: `tests/eThangAgent.Composition.Tests/SuperpowersBootstrapTests.cs`
- Delete: `tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs`

**Interfaces:**
- Consumes: `eThangAgent.Composition.SuperpowersBootstrapPromptProvider`, `eThangAgent.SkillDomain.EmbeddedSkillCatalog` (already referenced transitively by Composition.Tests).
- Produces: nothing downstream; pure relocation.

- [ ] **Step 1: Create the relocated test file**

Copy `tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs` verbatim except the namespace line becomes:

```csharp
namespace eThangAgent.Composition.Tests;
```

Keep the six facts exactly as-is (`Build_WrapsOutputInExtremelyImportantTags`, `Build_ContainsVerbatimUsingSuperpowersSkill`, `Build_ContainsEveryMappingKey`, `Build_MarksSkillAsAlreadyActive`, `Build_WrapperMarkersOccurExactlyOnceEach`, `Build_CatalogMissingUsingSuperpowers_ThrowsInvalidOperationException`) including the `StableBodyPhrase` constant and `CountOccurrences` helper.

- [ ] **Step 2: Delete the original**

Delete `tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs` (file only — the CLI.Tests project stays until Task 7).

- [ ] **Step 3: Run both affected suites**

Run: `dotnet test tests/eThangAgent.Composition.Tests` and `dotnet test tests/eThangAgent.CLI.Tests`
Expected: Composition.Tests passes with the 6 relocated facts; CLI.Tests still compiles and passes (one fewer file).

- [ ] **Step 4: Commit**

```text
test: relocate SuperpowersBootstrapTests to Composition.Tests
```

### Task 2: Move `MockOpenRouterServerTests` beside the surviving mock server

**Files:**
- Create: `tests/eThangAgent.Desktop.Tests/MockOpenRouterServerTests.cs`
- Delete: `tests/eThangAgent.CLI.Tests/MockOpenRouterServerTests.cs`

**Interfaces:**
- Consumes: `MockOpenRouterServer` already living in namespace `eThangAgent.Desktop.Tests` (identical twin of the CLI one).
- Produces: nothing downstream; pure relocation.

- [ ] **Step 1: Create the relocated test file**

Copy `tests/eThangAgent.CLI.Tests/MockOpenRouterServerTests.cs` verbatim except the namespace line becomes:

```csharp
namespace eThangAgent.Desktop.Tests;
```

The file is self-contained (its private helpers `Program`, `ExecToolCall`, `ChatRequest`, `Message`, `PostChatAsync`, `PostAsync` travel with it). Its usings (`System.Net`, `System.Text`) stay.

- [ ] **Step 2: Delete the original**

Delete `tests/eThangAgent.CLI.Tests/MockOpenRouterServerTests.cs`.

- [ ] **Step 3: Run the Desktop suite**

Run: `dotnet test tests/eThangAgent.Desktop.Tests`
Expected: PASS including the 2 relocated facts (`Serving_ReplacesEveryPlaceholder_WithMostRecentAgentId`, `Serving_ScriptDemandingSubstitution_WithoutAnyAgentId_IsRefused`).

- [ ] **Step 4: Commit**

```text
test: relocate MockOpenRouterServerTests beside the surviving mock server
```

### Task 3: Headless E2E fixture + provider-contract scenarios

**Files:**
- Create: `tests/eThangAgent.Desktop.Tests/E2EFixture.cs`
- Create: `tests/eThangAgent.Desktop.Tests/DesktopE2ETests.cs`

**Interfaces:**
- Consumes: `AddEThangAgentCore`, `AgentSettings`, `SubAgentOptions`, `MaxToolIterationsConfiguration`, `FixedWorkspaceContext`, `UnrootedPathResolver` (Composition); `MainViewModel`, `AssistantTextEntry` (Desktop); `MockOpenRouterServer` (Desktop.Tests).
- Produces (used by Tasks 4–6): `static class E2E` with members `Host`, `SessionModel` (const string, value `stealth/ox-alpha`), `RunTurnAsync(this MainViewModel, string)`, `ExecProgram(string) : string`, `ExecToolCall(string id, string arguments) : string`, `FindToolMessageContaining(IReadOnlyList<string>, string) : string`, `GetLastToolMessage(string) : string`, `AllToolMessages(IReadOnlyList<string>) : string`. `Host` exposes `Mock` (`MockOpenRouterServer`, started by `Start()`), `Vm` (`MainViewModel`); `Start()` returns the host; implements `IDisposable` (clears `ETHANG_AGENT_DB`, deletes the temp db, disposes services and mock).

- [ ] **Step 1: Write the fixture**

```csharp
using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Shared machinery for headless desktop E2E tests: builds the REAL composed core
/// (real OpenRouter client, real stores, real exec) against a local mock provider
/// and drives turns through MainViewModel. Replaces the piped-CLI E2E harness.
/// </summary>
public static class E2E
{
    /// <summary>The session model id wired at the composition root (mirrors DesktopHost).</summary>
    public const string SessionModel = "stealth/ox-alpha";

    /// <summary>A disposable headless agent host: mock server + services + view-model,
    /// with storage isolated to a temp database via ETHANG_AGENT_DB.</summary>
    public sealed class Host : IDisposable
    {
        private ServiceProvider? _services;

        public MockOpenRouterServer Mock { get; } = new();

        private string DatabasePath { get; set; } = "";

        public MainViewModel Vm { get; private set; } = null!;

        public Host Start()
        {
            Mock.Start();
            DatabasePath = Path.Combine(Path.GetTempPath(), $"ethang-e2e-{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", DatabasePath);

            var settings = new AgentSettings(
                "sk-or-test",
                new Uri(Mock.BaseUrl),
                new SubAgentOptions(null, TimeSpan.FromSeconds(30), 2),
                MaxToolIterationsConfiguration.Default);

            _services = new ServiceCollection()
                .AddEThangAgentCore(settings, settings.ApiKey!,
                    ModelConfig.Create(SessionModel, 32 * 1024, 0.7f).Value!,
                    new AgentHostOptions(
                        new NeverClarifyChannel(),
                        new FixedWorkspaceContext("app"),
                        new UnrootedPathResolver()))
                .BuildServiceProvider();

            var handler = _services.GetRequiredService<SendMessageCommandHandler>();
            var lifecycle = _services.GetRequiredService<RootSessionLifecycle>();
            var conversation = _services.GetRequiredService<Conversation>();

            Vm = new MainViewModel(
                (command, ct, content, reasoning, iterationEnd, toolCall, toolResult) =>
                    handler.Handle(command, ct, content, reasoning, iterationEnd, toolCall, toolResult),
                lifecycle,
                AgentId.NewId(),
                conversation,
                SessionModel,
                requestClose: () => { });
            return this;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
            try { if (DatabasePath.Length > 0) File.Delete(DatabasePath); } catch { /* best effort */ }
            _services?.Dispose();
            Mock.Dispose();
        }
    }

    /// <summary>Submits one user turn and waits for it to resolve, bounded so a wedged
    /// turn fails the test instead of hanging CI.</summary>
    public static async Task RunTurnAsync(this MainViewModel vm, string input)
    {
        await vm.SubmitAsync(input);
        await vm.WaitForTurnAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Serializes an exec tool-call argument carrying one C# program.</summary>
    public static string ExecProgram(string program) =>
        JsonSerializer.Serialize(new { program });

    /// <summary>Scripted assistant response performing one exec tool call.</summary>
    public static string ExecToolCall(string id, string arguments) =>
        JsonSerializer.Serialize(new
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
                            new { id, type = "function", function = new { name = "exec", arguments } }
                        }
                    }
                }
            }
        });

    /// <summary>Returns the decoded content of the first tool message containing the marker
    ///     across all captured chat request bodies (never raw-substring on escaped bodies).</summary>
    public static string FindToolMessageContaining(IReadOnlyList<string> bodies, string marker)
    {
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages))
                continue;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role)
                    && role.GetString() == "tool"
                    && message.TryGetProperty("content", out var content)
                    && content.GetString() is { } text
                    && text.Contains(marker, StringComparison.Ordinal))
                    return text;
            }
        }
        Assert.Fail($"no decoded tool message containing '{marker}' found in {bodies.Count} request bodies");
        return "";
    }

    /// <summary>Returns the decoded content of the LAST tool-role message in a chat request
    ///     body (never raw-substring on escaped bodies).</summary>
    public static string GetLastToolMessage(string body)
    {
        using var doc = JsonDocument.Parse(body);
        string? last = null;
        foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role)
                && role.GetString() == "tool"
                && message.TryGetProperty("content", out var content))
                last = content.GetString();
        }
        Assert.NotNull(last);
        return last!;
    }

    /// <summary>Joins all tool-message contents across all captured bodies, decoded.</summary>
    public static string AllToolMessages(IReadOnlyList<string> bodies)
    {
        var parts = new List<string>();
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages))
                continue;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role)
                    && role.GetString() == "tool"
                    && message.TryGetProperty("content", out var content)
                    && content.GetString() is { } text)
                    parts.Add(text);
            }
        }
        return string.Join("\n", parts);
    }

    private sealed class NeverClarifyChannel : IClarifyChannel
    {
        public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default) =>
            Task.FromResult(Result<string>.Failure(
                new Error("Cancelled", "no clarify expected in this E2E scenario")));
    }
}

/// <summary>All desktop E2E classes share one xUnit collection: they mutate the process-wide
///     ETHANG_AGENT_DB variable, so parallel classes must not race it.</summary>
[CollectionDefinition("Desktop E2E")]
public sealed class DesktopE2ECollection { }
```

- [ ] **Step 2: Write the provider-contract E2E tests**

```csharp
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite: the REAL
/// composition answers through the mock provider and the view-model renders it.
/// Provider-contract scenarios: configured model selection, superpowers bootstrap,
/// exposed tool surface, exec guide injection.
/// </summary>
[Collection("Desktop E2E")]
public class DesktopE2ETests
{
    private static string RawCompletion(string content) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task Turn_SendsConfiguredDefaultModel_ToProvider()
    {
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains(E2E.SessionModel, host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task Turn_InjectsSuperpowersBootstrap_OncePerSession()
    {
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        var body = host.Mock.LastChatRequestBody;
        Assert.NotNull(body);
        // The wire body JSON-escapes angle brackets (\u003C/\u003E), so assertions on
        // injected content run against the decoded system message, not the raw body.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var system = doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString();
        Assert.NotNull(system);
        Assert.Contains("<EXTREMELY_IMPORTANT>", system);
        Assert.Contains("name: using-superpowers", system);
        Assert.Contains("ALREADY ACTIVE", system);
        Assert.Contains("skill_view", system);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(system!,
            System.Text.RegularExpressions.Regex.Escape("<EXTREMELY_IMPORTANT>")));
    }

    [Fact]
    public async Task ModelToolsContainOnlyExec()
    {
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains("\"name\":\"exec\"", host.Mock.LastChatRequestBody);
        Assert.DoesNotContain("\"name\":\"read\"", host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task SendsExecGuide_InSystemPrompt()
    {
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains("\"role\":\"system\"", host.Mock.LastChatRequestBody);
        Assert.Contains("writing C# programs", host.Mock.LastChatRequestBody);
        Assert.Contains("get(key: String): Read a durable state value.", host.Mock.LastChatRequestBody);
        Assert.Contains(
            "verify(ids: String[]): Run attached evidence fail-closed and certify.",
            host.Mock.LastChatRequestBody);
        Assert.Contains(
            "read(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            host.Mock.LastChatRequestBody);
    }
}
```

- [ ] **Step 3: Run the new tests**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter "FullyQualifiedName~DesktopE2ETests"`
Expected: 4 PASS.

- [ ] **Step 4: Commit**

```text
test: port provider-contract E2Es to headless desktop host
```
### Task 4: Port exec-tool E2E scenarios

**Files:**
- Modify: `tests/eThangAgent.Desktop.Tests/DesktopE2ETests.cs` (append facts)

**Interfaces:**
- Consumes: `E2E.Host`, `E2E.RunTurnAsync`, `E2E.ExecProgram`, `E2E.ExecToolCall` from Task 3.
- Produces: nothing downstream.

- [ ] **Step 1: Append two facts to `DesktopE2ETests`**

```csharp
    [Fact]
    public async Task ExecutesExecTool_EndToEnd()
    {
        using var host = new E2E.Host().Start();

        var tempFile = Path.Combine(Path.GetTempPath(), $"ethang-exec-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line"]);

        var pathArg = tempFile.Replace("\\", "\\\\");
        var program = $"return Tools.read(new {{ path = \"{pathArg}\", startLine = 1, endLine = 2 }});";
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
        host.Mock.Returns(RawCompletion("exec completed"));

        await host.Vm.RunTurnAsync("run a program");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("exec completed", assistant, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, host.Mock.RequestBodies.Count);
        Assert.Contains("\"role\":\"tool\"", host.Mock.RequestBodies[1]);
        Assert.Contains("alpha line", host.Mock.RequestBodies[1]);

        try { File.Delete(tempFile); } catch { }
    }

    [Fact]
    public async Task Exec_ParseErrorFeedsBack_AndCorrectedProgramSucceeds()
    {
        using var host = new E2E.Host().Start();

        var broken = System.Text.Json.JsonSerializer.Serialize(new { program = "if (x {" });
        var corrected = System.Text.Json.JsonSerializer.Serialize(
            new { program = "Write-Output 'corrected output'" });
        host.Mock.Returns(E2E.ExecToolCall("call_1", broken));
        host.Mock.Returns(E2E.ExecToolCall("call_2", corrected));
        host.Mock.Returns(RawCompletion("done"));

        await host.Vm.RunTurnAsync("try exec");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, host.Mock.RequestBodies.Count);
        Assert.Contains("ExecParseError", host.Mock.RequestBodies[1]);
        Assert.Contains("ExecParseError", host.Mock.RequestBodies[2]);
        Assert.Contains("corrected output", host.Mock.RequestBodies[2]);
    }
```

Note: `RawCompletion(text)` is the tiny per-class helper defined in Task 3's test class; add it to `DesktopAgentCapabilityE2ETests` in Task 5 as well.

- [ ] **Step 2: Run them**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter "FullyQualifiedName~DesktopE2ETests"`
Expected: 6 PASS total in the class.

- [ ] **Step 3: Commit**

```text
test: port exec-tool E2Es to headless desktop host
```

### Task 5: Port agent-capability E2E scenarios (state discipline, todo boundary)

**Files:**
- Create: `tests/eThangAgent.Desktop.Tests/DesktopAgentCapabilityE2ETests.cs`

**Interfaces:**
- Consumes: `E2E` fixture from Task 3.
- Produces: nothing downstream.

- [ ] **Step 1: Write the test class**

```csharp
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite:
/// durable-state discipline and the todo/reserved-namespace boundary through
/// the real composition against the mock provider.
/// </summary>
[Collection("Desktop E2E")]
public class DesktopAgentCapabilityE2ETests
{
    private static string RawCompletion(string content) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task StateDisciplineLoop_Certifies()
    {
        using var host = new E2E.Host().Start();

        var program = """
            Tools.Invoke("state.set", new { key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { from = "coding", to = "done", summary = "work", evidence = new[] { "true" } });
            return Tools.Invoke("state.verify", new { });
            """;
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
        host.Mock.Returns(RawCompletion("certified"));

        await host.Vm.RunTurnAsync("track the work");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("certified", assistant, StringComparison.OrdinalIgnoreCase);
        Assert.True(host.Mock.RequestBodies.Count >= 2);
        Assert.Contains("\"Certified\":true",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateDisciplineLoop_Violated_OnFailingEvidence()
    {
        using var host = new E2E.Host().Start();

        var program = """
            Tools.Invoke("state.set", new { key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { from = "coding", to = "done", summary = "work", evidence = new[] { "throw new System.Exception(\"boom\")" } });
            return Tools.Invoke("state.verify", new { });
            """;
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
        host.Mock.Returns(RawCompletion("violated"));

        await host.Vm.RunTurnAsync("track the work");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("violated", assistant, StringComparison.OrdinalIgnoreCase);
        var toolContent = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
        Assert.Contains("\"Certified\":false", toolContent, StringComparison.Ordinal);
        Assert.Contains("\"Violated\":true", toolContent, StringComparison.Ordinal);
        Assert.Contains("boom", toolContent, StringComparison.Ordinal);
    }

    /// <summary>Boundary honesty E2E over the composed stack: the todo tool's own writes
    ///     flow through StateServiceTodoListStore → StateService → SqliteStateStore and
    ///     succeed, while model-invoked state.set/state.delete against the reserved
    ///     'todo' namespace are rejected at the capability boundary with ReservedNamespace
    ///     and leave the persisted todo document untouched.</summary>
    [Fact]
    public async Task TodoToolWritesFlow_ButModelStateWritesOnTodoNs_AreRejected()
    {
        using var host = new E2E.Host().Start();

        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram("Tools.Invoke(\"todo\", new { action = \"Add\", description = \"ship it\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram("Tools.Invoke(\"state.set\", new { key = \"todo/list\", value = \"hijack\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_3", E2E.ExecProgram("Tools.Invoke(\"state.delete\", new { key = \"todo/list\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_4", E2E.ExecProgram("Tools.Invoke(\"todo\", new { action = \"List\" })")));
        host.Mock.Returns(RawCompletion("done"));

        await host.Vm.RunTurnAsync("track one task, then try to write todo state directly");

        Assert.True(host.Mock.RequestBodies.Count >= 5,
            $"expected at least 5 scripted requests, got {host.Mock.RequestBodies.Count}");

        // (a) Composed flow: the todo tool's own adapter write landed in durable state.
        Assert.Contains("[todo] added #1",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[1]), StringComparison.Ordinal);

        // (b) Boundary gate: model-invoked writes to the reserved namespace are rejected
        //     with ReservedNamespace, never reaching the service.
        Assert.Contains("ReservedNamespace",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[2]), StringComparison.Ordinal);
        Assert.Contains("ReservedNamespace",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[3]), StringComparison.Ordinal);

        // (c) The rejected foreign writes left the persisted todo document untouched.
        Assert.Contains("#1 [Pending] ship it",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[4]), StringComparison.Ordinal);

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter "FullyQualifiedName~DesktopAgentCapabilityE2ETests"`
Expected: 3 PASS.

- [ ] **Step 3: Commit**

```text
test: port state-discipline and todo-boundary E2Es to desktop host
```

### Task 6: Port nested-spawn and memory-recall E2E scenarios

**Files:**
- Modify: `tests/eThangAgent.Desktop.Tests/DesktopAgentCapabilityE2ETests.cs` (append facts)

**Interfaces:**
- Consumes: `E2E` fixture; `MockOpenRouterServer.ReturnsForModel`, `TryGetRequestModel`.
- Produces: nothing downstream.

- [ ] **Step 1: Append two facts to `DesktopAgentCapabilityE2ETests`**

```csharp
    /// <summary>Nested-spawn E2E, async contract: the parent session spawns a child through
    ///     agent.spawn (returns immediately with status=running and no report), then fetches
    ///     the finished child's report through agent.result. The mock plays both sides via
    ///     model-keyed scripting — the parent under its session model, the child under the
    ///     per-spawn model — and substitutes {{child_id}} with the runtime child id observed
    ///     in the parent's tool messages.</summary>
    [Fact]
    public async Task NestedSpawn_ChildRunsAndReports()
    {
        using var host = new E2E.Host().Start();

        // Parent script, keyed by the session model: spawn, status, poll-then-fetch result,
        // final text. Turn 3 polls status inside exec so the async child's terminal write is
        // observed before agent.result runs.
        const string pollThenResult = """
            var status = "";
            while (!status.Contains("status=completed"))
            {
                await System.Threading.Tasks.Task.Delay(50);
                status = Tools.Invoke("agent.status", new { id = "{{child_id}}" });
            }
            return Tools.Invoke("agent.result", new { id = "{{child_id}}" });
            """;
        host.Mock.ReturnsForModel(E2E.SessionModel,
            E2E.ExecToolCall("parent_call_1", E2E.ExecProgram("var spawned = Tools.Invoke(\"agent.spawn\", new { taskPrompt = \"Say child report done and nothing else.\", model = \"mock/sub-model\", label = \"e2e\" }); return spawned;")),
            E2E.ExecToolCall("parent_call_2", E2E.ExecProgram("return Tools.Invoke(\"agent.status\", new { id = \"{{child_id}}\" });")),
            E2E.ExecToolCall("parent_call_3", E2E.ExecProgram(pollThenResult)),
            RawCompletion("done: child reported"));

        // Child script, keyed by the per-spawn model: one tool turn, then the final report.
        host.Mock.ReturnsForModel("mock/sub-model",
            E2E.ExecToolCall("child_call_1", E2E.ExecProgram("return \"child report done\";")),
            RawCompletion("child report done"));

        await host.Vm.RunTurnAsync("delegate a subtask and fetch its result");

        var parentBodies = host.Mock.RequestBodies
            .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel)
            .ToList();
        Assert.True(parentBodies.Count >= 4,
            $"expected at least 4 parent requests, got {parentBodies.Count}");

        // (a) The spawn result reached the transcript as a running line — non-blocking:
        //     no report text, and none of the removed completed-gutter furniture.
        var spawnResult = E2E.GetLastToolMessage(parentBodies[1]);
        Assert.Matches("^id=[0-9a-fA-F-]{36} status=running$", spawnResult.Trim());
        Assert.DoesNotContain("child report done", spawnResult);
        Assert.DoesNotContain("--- report ---", spawnResult);

        // (b) Wire: the child ran its own loop against the mock under the per-spawn model id.
        Assert.Contains(host.Mock.RequestBodies,
            body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model");

        // (c) Decoded transcript: the parent fetched the child's report through agent.result.
        Assert.Contains("child report done",
            E2E.FindToolMessageContaining(parentBodies, "child report done"),
            StringComparison.Ordinal);

        // (d) The parent's final reply acknowledges completion.
        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("done:", assistant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seed-and-recall E2E: the first exchange seeds the persisted root transcript
    ///     with a distinctive phrase; scripted turns then list sessions and recall the phrase
    ///     through the memory capability actions inside exec programs. Assertions read only
    ///     decoded tool-message content — [mem] hit lines, the paging footer, session= lines.</summary>
    [Fact]
    public async Task MemoryRecall_AgainstMockServer()
    {
        using var host = new E2E.Host().Start();

        // Turn 1: plain assistant reply seeding 'xylophone harvest' into the transcript.
        host.Mock.Returns(RawCompletion("The xylophone harvest begins at dawn."));
        await host.Vm.RunTurnAsync("tell me about the xylophone harvest");

        // Turn 2: one exec tool call listing what conversations exist.
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram("return Tools.Invoke(\"memory.sessions\", new { limit = 50 });")));
        // Turn 3: one exec tool call recalling the seeded phrase across all sessions.
        host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram("return Tools.Invoke(\"memory.recall\", new { query = \"xylophone\", scope = \"global\" });")));
        // Turn 4: final text closes the exchange.
        host.Mock.Returns(RawCompletion("recalled."));
        await host.Vm.RunTurnAsync("now list sessions and recall what you said");

        Assert.True(host.Mock.RequestBodies.Count >= 4,
            $"expected at least 4 scripted requests, got {host.Mock.RequestBodies.Count}");

        // (a) Sessions listing shows the persisted root conversation at depth 0.
        var sessionsOutput = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "label=root depth=0");
        Assert.Matches(@"(^|\n)session=[0-9a-fA-F-]{36} label=root depth=0 entries=\d+ ", sessionsOutput);

        // (b) Recall renders the [mem] annotation line carrying the seeded phrase.
        var recallOutput = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "xylophone harvest");
        Assert.Contains("[mem] session=", recallOutput, StringComparison.Ordinal);

        // (c) The recall footer follows the paging contract.
        Assert.Matches(@"--- memory: \d+ hits, page 1/\d+ ---", recallOutput);

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("recalled.", assistant, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run the full Desktop suite**

Run: `dotnet test tests/eThangAgent.Desktop.Tests`
Expected: PASS — all pre-existing tests plus the 11 ported facts (4 + 2 + 3 + 2).

- [ ] **Step 3: Commit**

```text
test: port nested-spawn and memory-recall E2Es to desktop host
```

### Task 7: Delete the CLI and Terminal.ACL projects

**Files:**
- Delete: `src/eThangAgent.CLI/` (entire directory, including bin/obj)
- Delete: `src/eThangAgent.Terminal.ACL/` (entire directory, including bin/obj)
- Delete: `tests/eThangAgent.CLI.Tests/` (entire directory)
- Delete: `tests/eThangAgent.Terminal.ACL.Tests/` (entire directory)
- Modify: `eThangAgent.slnx` (remove 4 `<Project>` lines)

**Interfaces:**
- Consumes: Tasks 1–6 (ported coverage already green in Desktop.Tests / Composition.Tests).
- Produces: a solution with no CLI or Terminal.ACL anywhere; all later tasks build on this.

- [ ] **Step 1: Remove the four project directories**

PowerShell (from repo root):

```powershell
Remove-Item -Recurse -Force src/eThangAgent.CLI, src/eThangAgent.Terminal.ACL, tests/eThangAgent.CLI.Tests, tests/eThangAgent.Terminal.ACL.Tests
```

- [ ] **Step 2: Edit `eThangAgent.slnx`**

Delete exactly these four lines:

```xml
   <Project Path="src/eThangAgent.CLI/eThangAgent.CLI.csproj" />
   <Project Path="src/eThangAgent.Terminal.ACL/eThangAgent.Terminal.ACL.csproj" />
   <Project Path="tests/eThangAgent.CLI.Tests/eThangAgent.CLI.Tests.csproj" />
   <Project Path="tests/eThangAgent.Terminal.ACL.Tests/eThangAgent.Terminal.ACL.Tests.csproj" />
```

- [ ] **Step 3: Verify nothing references the deleted projects**

Search all `*.cs`, `*.csproj`, `*.slnx`, `*.md` under `src/`, `tests/`, and repo root for `eThangAgent.CLI` and `Terminal.ACL`.
Expected: zero hits (directories were deleted wholesale; historical spec/plan documents under `docs/` are history, not live references).

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build` then `dotnet test`
Expected: build green; every remaining project's tests pass.

- [ ] **Step 5: Commit**

```text
chore: remove the CLI and Terminal.ACL — desktop is the only frontend
```

### Task 8: Update AGENTS.md

**Files:**
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: Task 7's reality (CLI gone).
- Produces: handbook consistent with the desktop-only frontend.

- [ ] **Step 1: Make these exact edits**

1. Opening paragraph: "eThang Agent is an AI agent built with .NET, delivered through a CLI." → "eThang Agent is an AI agent built with .NET, delivered through an Avalonia desktop application."
2. Technology stack, Interface bullet: `- **Interface**: CLI` → `- **Interface**: Desktop (Avalonia)`
3. ACL table: delete the entire Terminal ACL row (`| Terminal ACL | Console I/O ... |`).
4. Testing conventions bullet: "E2E tests drive the full CLI against a local mock provider server." → "E2E tests drive the desktop app headless — real composition behind the view-model — against a local mock provider server."
5. Sweep for any remaining "CLI"/"terminal" mentions; rewrite each to reflect the desktop-only frontend or drop the phrase. Do not touch historical references inside quoted skill bodies.

- [ ] **Step 2: Verify**

Grep `AGENTS.md` for `CLI` and `Terminal`: expected zero live references (quoted skill text exempt).

- [ ] **Step 3: Commit**

```text
docs: update AGENTS.md for the desktop-only frontend
```

### Task 9: Update README.md

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: Task 7's reality.
- Produces: user-facing docs matching the product.

- [ ] **Step 1: Make these exact edits**

1. Intro paragraph: "delivered through a CLI" → "delivered through an Avalonia desktop application"; drop the terminal-REPL clause from the feature sentence.
2. "What it can do today": replace the "Two interchangeable frontends" bullet with one stating the single Avalonia desktop frontend over the shared core; delete the interactive-REPL streaming bullet and the piped-mode bullet (streaming stays described once, desktop-framed); keep everything else.
3. Getting started step 3: remove the `dotnet run --project src/eThangAgent.CLI` line and its comment; keep only `dotnet run --project src/eThangAgent.Desktop`.
4. Delete the paragraph contrasting CLI workspace behavior vs desktop ("The CLI starts an interactive REPL..."); keep/merge the accurate part about the picked working directory rooting path resolution.
5. Usage section: delete the Commands table (`/help`, `/exit`, `/quit`) and the entire "Piped mode" subsection.
6. Configuration section: verify no rows mention the CLI executable; leave as-is when clean.
7. Development section: publish command becomes `dotnet publish src/eThangAgent.Desktop -c Release -r win-x64 --self-contained false`; the E2E bullet becomes "E2E tests drive the desktop app headless against a local mock OpenRouter server."
8. Repository layout block: replace "eThangAgent.CLI (terminal frontend) and eThangAgent.Desktop (Avalonia frontend)" with "eThangAgent.Desktop (Avalonia frontend)".

- [ ] **Step 2: Verify**

Read the rendered README top-to-bottom; no section may promise a terminal interface, slash commands, or piped mode.

- [ ] **Step 3: Commit**

```text
docs: update README for the desktop-only frontend
```

### Task 10: Final verification sweep

**Files:**
- Possibly modify: any file where the sweep finds a leftover reference.

**Interfaces:**
- Consumes: Tasks 1–9 complete.
- Produces: certified clean state.

- [ ] **Step 1: Full green run**

Run: `dotnet build` and `dotnet test`
Expected: green across all projects.

- [ ] **Step 2: Repo-wide leftover scan**

Search `src/`, `tests/`, `*.slnx`, `AGENTS.md`, `README.md` for: `eThangAgent.CLI`, `Terminal.ACL`, `CliCommands`, `PipedClarifyChannel`, `CwdWorkspaceContext`, `InteractiveClarifyChannel`, `AnsiTerminal`.
Expected: zero hits. Fix anything found (each fix belongs in a small `chore:` commit), re-run Step 1 after fixing.

- [ ] **Step 3: Report**

Summarize: projects deleted, tests moved/ported/retired counts, docs updated, final build+test status.

---

## Self-review notes

- Spec coverage: deletions (Task 7); 13-scenario E2E disposition (Tasks 3–6 port 11 new facts; scenario 1 happy-path remains covered by the retained `DesktopPipelineSmokeTests`; the `/help`-quit scenario is intentionally retired with the CLI); relocations (Tasks 1–2); fixture + collection isolation (Task 3); AGENTS.md (Task 8); README.md (Task 9); verification (Tasks 7/10). Every spec bullet maps to a task.
- Placeholder scan: none — every code step carries full code; every doc step carries exact wording targets.
- Type consistency: fixture member names (`E2E.SessionModel`, `E2E.Host`, `RunTurnAsync`, `ExecProgram`, `ExecToolCall`, `FindToolMessageContaining`, `GetLastToolMessage`) match across Tasks 3–6; namespaces follow repo conventions (`eThangAgent.Desktop.Tests`, `eThangAgent.Composition.Tests`).
- Known risk carried into execution: `SubAgentOptions(null, TimeSpan.FromSeconds(30), 2)` mirrors the smoke test's proven construction; if the record's parameter order differs, align to the definition in `src/eThangAgent.Composition/SubAgentConfiguration.cs` without changing test intent.
