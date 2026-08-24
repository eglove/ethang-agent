# P2 Capability Registry — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Per project preference, execution dispatches one Fabric agent per task with review between tasks.

**Goal:** Collapse the model-facing surface to `exec` alone and generalize everything scripts can call into a domain-owned capability registry — providers contribute actions, the exec engine routes through the registry, and action documentation is generated from the live registry into the session-start guide plus on-demand introspection.

**Architecture:** New bounded context `eThangAgent.Capability.Domain` owns `ActionDescriptor`, `ICapabilityProvider`, `ICapabilityRegistry`, the strict `CapabilityRegistry`, an `AgentToolsProvider` adapter over existing `ITool`s, and the reference renderer. `ReadTool` is untouched and surfaces as action `agent.read`. The PowerShell.ACL broker, wrapper generation, and introspection switch from `IToolRegistry` to `ICapabilityRegistry`. `ModelRequest.Tools` carries only `exec`; agent loop and OpenRouter translation are untouched.

**Tech Stack:** .NET 10, C# (records, Result<T>), System.Management.Automation via Microsoft.PowerShell.SDK 7.4.* (P1 decision), xUnit, System.Text.Json, Microsoft.Extensions.DependencyInjection.

**Spec:** `docs/skills/specs/2026-08-21-capability-registry-design.md` — the plan argues from the spec; executors read both.

> **Progress:** ALL TASKS (1–9) COMPLETE — full suite green, coverage collected (Capability.Domain ~100%, PowerShell.ACL ≥ 80%), spec cross-check passed. P2 done; next cycle: P3 Durable State + Schema spec.

## Global Constraints

- **Windows-only, PowerShell-only.** No `.sh`, `.cmd`, `.bat` scripts in the repo.
- **.NET 10 / C#**, ImplicitUsings + Nullable (inherited from `Directory.Build.props`).
- **DDD layering:** Capability.Domain references only SharedKernel + Tool.Domain (for the `ITool` adapter). PowerShell types stay in PowerShell.ACL.
- **Model-facing surface:** `ModelRequest.Tools` contains only `exec`. No other tool is registered as an `ITool`.
- **Strict registration (fail fast at composition time):** duplicate action names across providers → throw; duplicate provider ids → throw; empty provider id or empty action set → throw; action names must match `[A-Za-z0-9_]+` (they become PowerShell function names).
- **Refs:** `provider.action` internally (`agent.read`); bare action names are the wrapper-function names; `Invoke-AgentTool -Name` and `Get-AgentAction` accept bare name or full ref.
- **No nested exec:** `exec` is never registered as a capability action — structural, not filtered.
- **Budgets and gutters carried from P1 verbatim:** program ≤ 65536 chars; output 51200; errors 20480; ≤ 10 parse errors; timeout 120s; `exec error [<Code>]:` for exec-run failures; `Error [<Code>]:` for input-shape failures; action failures surface as terminating PowerShell errors carrying their gutter.
- **Every task ends green:** `dotnet build` passes and the task's targeted tests pass before committing (conventional commits).
- **Test conventions:** xUnit; `GlobalUsings.cs` with `global using Xunit;` in every test project; hand-rolled fakes only.
- **Build checks must scan full output** (`… | rg 'error|FAILED' || echo BUILD-CLEAN`), never a tail slice — P1 lesson.

## File Structure

**New project:** `src/eThangAgent.Capability.Domain/` (namespace `eThangAgent.CapabilityDomain`) — references SharedKernel + Tool.Domain.
**New test project:** `tests/eThangAgent.Capability.Domain.Tests/`.

**New files (Capability.Domain):** `CapabilityNameRules.cs`, `ActionParameter.cs`, `ActionDescriptor.cs`, `CapabilityInvocationResult.cs`, `ResolvedCapability.cs`, `ProviderCapabilities.cs`, `ICapabilityProvider.cs`, `ICapabilityRegistry.cs`, `CapabilityRegistry.cs`, `AgentToolBinding.cs`, `AgentToolsProvider.cs`, `CapabilityReferenceRenderer.cs`.

**Modified:** `eThangAgent.slnx` (+2 entries), `ToolBroker.cs` (registry-based + `ListActions`/`DescribeAction`/`ListProviders`), `PowerShellExecEngine.cs` (registry wiring + 2 new injected functions), `ExecGuide.cs` (v1.1: introspection docs), `ExecGuidePromptProvider.cs` (registry-aware), `Program.cs` (capability wiring, exec-only registry), `E2ETests.cs` (tools-only-exec + reference-line assertions), `ToolBrokerTests.cs` + `PowerShellExecEngine*Tests.cs` (registry-based wiring).

---

### Task 1: Capability.Domain scaffold, core records, name rules

**Files:**

- Create: `src/eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj`
- Create: `src/eThangAgent.Capability.Domain/CapabilityNameRules.cs`
- Create: `src/eThangAgent.Capability.Domain/ActionParameter.cs`
- Create: `src/eThangAgent.Capability.Domain/ActionDescriptor.cs`
- Create: `src/eThangAgent.Capability.Domain/CapabilityInvocationResult.cs`
- Create: `src/eThangAgent.Capability.Domain/ResolvedCapability.cs`
- Create: `src/eThangAgent.Capability.Domain/ProviderCapabilities.cs`
- Create: `src/eThangAgent.Capability.Domain/ICapabilityProvider.cs`
- Create: `src/eThangAgent.Capability.Domain/ICapabilityRegistry.cs`
- Create: `tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
- Create: `tests/eThangAgent.Capability.Domain.Tests/GlobalUsings.cs`
- Create: `tests/eThangAgent.Capability.Domain.Tests/CapabilityNameRulesTests.cs`
- Modify: `eThangAgent.slnx` (add both projects)

**Interfaces:**

- Produces (later tasks rely on these exact shapes): `ActionParameter(Name, Type, Description)`; `ActionDescriptor(Name, Summary, Description, Parameters)`; `CapabilityInvocationResult(Content, IsError)` with `Ok`/`Fail` factories; `ResolvedCapability(ProviderId, Action)`; `ProviderCapabilities(Id, Actions)`; `ICapabilityProvider { string Id; IReadOnlyList<ActionDescriptor> Actions; Task<CapabilityInvocationResult> InvokeAsync(string actionName, string jsonArguments, CancellationToken ct = default); }`; `ICapabilityRegistry { Result<ResolvedCapability> Resolve(string nameOrRef); IReadOnlyList<ProviderCapabilities> Providers { get; } Task<CapabilityInvocationResult> InvokeAsync(ResolvedCapability capability, string jsonArguments, CancellationToken ct = default); }`; `CapabilityNameRules.IsValidActionName(string) → bool`.

- [ ] **Step 1: Create both projects + solution entries**

`src/eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Capability.Domain.Tests/GlobalUsings.cs`:

```csharp
global using Xunit;
```

In `eThangAgent.slnx`, add after the FileSystem.ACL project line:

```xml
  <Project Path="src/eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
```

and after the FileSystem.ACL.Tests line:

```xml
  <Project Path="tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj" />
```

- [ ] **Step 2: Write the failing test**

`tests/eThangAgent.Capability.Domain.Tests/CapabilityNameRulesTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityNameRulesTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("Get_Item")]
    [InlineData("a1")]
    public void ValidActionNames_Accepted(string name)
        => Assert.True(CapabilityNameRules.IsValidActionName(name));

    [Theory]
    [InlineData("")]
    [InlineData("read-file")]
    [InlineData("read.file")]
    [InlineData("has space")]
    [InlineData("héllo")]
    public void InvalidActionNames_Rejected(string name)
        => Assert.False(CapabilityNameRules.IsValidActionName(name));
}
```

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: build fails, `CapabilityNameRules` not found.

- [ ] **Step 4: Implement the records, interfaces, and name rules**

`src/eThangAgent.Capability.Domain/CapabilityNameRules.cs`:

```csharp
using System.Text.RegularExpressions;

namespace eThangAgent.CapabilityDomain;

public static class CapabilityNameRules
{
    private static readonly Regex Pattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>Action names become PowerShell function names — restrict to what is
    ///     safe to generate, reject rather than sanitize.</summary>
    public static bool IsValidActionName(string name)
        => !string.IsNullOrWhiteSpace(name) && Pattern.IsMatch(name);
}
```

`src/eThangAgent.Capability.Domain/ActionParameter.cs`:

```csharp
namespace eThangAgent.CapabilityDomain;

public sealed record ActionParameter(string Name, string Type, string Description);
```

`src/eThangAgent.Capability.Domain/ActionDescriptor.cs`:

```csharp
namespace eThangAgent.CapabilityDomain;

public sealed record ActionDescriptor(
    string Name,
    string Summary,
    string Description,
    IReadOnlyList<ActionParameter> Parameters);
```

`src/eThangAgent.Capability.Domain/CapabilityInvocationResult.cs`:

```csharp
namespace eThangAgent.CapabilityDomain;

public sealed record CapabilityInvocationResult(string Content, bool IsError)
{
    public static CapabilityInvocationResult Ok(string content) => new(content, false);
    public static CapabilityInvocationResult Fail(string content) => new(content, true);
}
```

`src/eThangAgent.Capability.Domain/ResolvedCapability.cs`:

```csharp
namespace eThangAgent.CapabilityDomain;

public sealed record ResolvedCapability(string ProviderId, ActionDescriptor Action);
```

`src/eThangAgent.Capability.Domain/ProviderCapabilities.cs`:

```csharp
namespace eThangAgent.CapabilityDomain;

public sealed record ProviderCapabilities(string Id, IReadOnlyList<ActionDescriptor> Actions);
```

`src/eThangAgent.Capability.Domain/ICapabilityProvider.cs`:

```csharp
namespace eThangAgent.CapabilityDomain;

public interface ICapabilityProvider
{
    string Id { get; }
    IReadOnlyList<ActionDescriptor> Actions { get; }

    Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default);
}
```

`src/eThangAgent.Capability.Domain/ICapabilityRegistry.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain;

public interface ICapabilityRegistry
{
    Result<ResolvedCapability> Resolve(string nameOrRef);

    IReadOnlyList<ProviderCapabilities> Providers { get; }

    Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default);
}
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: 8 tests pass.

- [ ] **Step 6: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.Capability.Domain tests/eThangAgent.Capability.Domain.Tests eThangAgent.slnx
```

```bash
git commit -m "feat(capability-domain): add capability records, interfaces, and name rules"
```

---

### Task 2: CapabilityRegistry — strict construction, resolution, invocation

**Files:**

- Create: `src/eThangAgent.Capability.Domain/CapabilityRegistry.cs`
- Create: `tests/eThangAgent.Capability.Domain.Tests/CapabilityRegistryTests.cs`

**Interfaces:**

- Consumes: Task 1's records/interfaces; `Result<T>`/`Error`.
- Produces: `CapabilityRegistry.Create(IEnumerable<ICapabilityProvider>)` (throws `InvalidOperationException` on: zero providers, empty/duplicate provider ids, empty action sets, invalid action names, duplicate action names across providers); `Resolve` by bare name or `provider.action` ref (`Result<ResolvedCapability>`, `UnknownAction` error listing available actions); `InvokeAsync(ResolvedCapability, jsonArguments)` routing to the owning provider.

- [ ] **Step 1: Write the failing tests**

`tests/eThangAgent.Capability.Domain.Tests/CapabilityRegistryTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityRegistryTests
{
    private static ActionDescriptor Act(string name) => new(name, "sum", "desc", []);

    [Fact]
    public void Create_NoProviders_Throws()
        => Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create([]));

    [Fact]
    public void Create_EmptyProviderId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapabilityRegistry.Create([new FakeProvider("")]));
        Assert.Contains("id must be non-empty", ex.Message);
    }

    [Fact]
    public void Create_DuplicateProviderId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create(
            [new FakeProvider("agent", Act("read")), new FakeProvider("agent", Act("grep"))]));
        Assert.Contains("Duplicate capability provider id 'agent'", ex.Message);
    }

    [Fact]
    public void Create_ProviderWithoutActions_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapabilityRegistry.Create([new FakeProvider("agent")]));
        Assert.Contains("exposes no actions", ex.Message);
    }

    [Fact]
    public void Create_InvalidActionName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapabilityRegistry.Create([new FakeProvider("agent", Act("read-file"))]));
        Assert.Contains("is invalid", ex.Message);
    }

    [Fact]
    public void Create_DuplicateActionNameAcrossProviders_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create(
            [new FakeProvider("a", Act("read")), new FakeProvider("b", Act("read"))]));
        Assert.Contains("Duplicate action name 'read'", ex.Message);
    }

    [Fact]
    public void Resolve_ByBareName_Succeeds()
    {
        var registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"))]);

        var result = registry.Resolve("read");

        Assert.True(result.IsSuccess);
        Assert.Equal("agent", result.Value!.ProviderId);
        Assert.Equal("read", result.Value.Action.Name);
    }

    [Fact]
    public void Resolve_ByFullRef_Succeeds()
    {
        var registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"))]);

        var result = registry.Resolve("agent.read");

        Assert.True(result.IsSuccess);
        Assert.Equal("read", result.Value!.Action.Name);
    }

    [Fact]
    public void Resolve_Unknown_ListsAvailable()
    {
        var registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"), Act("grep"))]);

        var result = registry.Resolve("nope");

        Assert.False(result.IsSuccess);
        Assert.Equal("UnknownAction", result.Error!.Code);
        Assert.Contains("grep, read", result.Error.Message);
    }

    [Fact]
    public async Task InvokeAsync_RoutesToOwningProvider()
    {
        var provider = new RecordingProvider("agent", Act("read"));
        var registry = CapabilityRegistry.Create([provider]);
        var resolved = registry.Resolve("read").Value!;

        var result = await registry.InvokeAsync(resolved, "{}");

        Assert.False(result.IsError);
        Assert.Equal("{}", provider.LastJson);
    }

    private sealed class FakeProvider : ICapabilityProvider
    {
        public FakeProvider(string id, params ActionDescriptor[] actions)
        {
            Id = id;
            Actions = actions;
        }

        public string Id { get; }
        public IReadOnlyList<ActionDescriptor> Actions { get; }

        public Task<CapabilityInvocationResult> InvokeAsync(
            string actionName, string jsonArguments, CancellationToken ct = default)
            => Task.FromResult(CapabilityInvocationResult.Ok("ok"));
    }

    private sealed class RecordingProvider : ICapabilityProvider
    {
        public RecordingProvider(string id, params ActionDescriptor[] actions)
        {
            Id = id;
            Actions = actions;
        }

        public string Id { get; }
        public IReadOnlyList<ActionDescriptor> Actions { get; }
        public string? LastJson { get; private set; }

        public Task<CapabilityInvocationResult> InvokeAsync(
            string actionName, string jsonArguments, CancellationToken ct = default)
        {
            LastJson = jsonArguments;
            return Task.FromResult(CapabilityInvocationResult.Ok("ok"));
        }
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: build fails, `CapabilityRegistry` not found.

- [ ] **Step 3: Implement**

`src/eThangAgent.Capability.Domain/CapabilityRegistry.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, ICapabilityProvider> _providersById;
    private readonly Dictionary<string, (ICapabilityProvider Provider, ActionDescriptor Action)> _byName;
    private readonly IReadOnlyList<ProviderCapabilities> _providers;

    private CapabilityRegistry(IReadOnlyList<ICapabilityProvider> providers)
    {
        _providersById = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _providers = providers.Select(p => new ProviderCapabilities(p.Id, p.Actions)).ToList();
        _byName = providers
            .SelectMany(p => p.Actions.Select(a => (Provider: p, Action: a)))
            .ToDictionary(x => x.Action.Name, x => (x.Provider, x.Action), StringComparer.Ordinal);
    }

    public static CapabilityRegistry Create(IEnumerable<ICapabilityProvider> providers)
    {
        var list = providers.ToList();
        if (list.Count == 0)
            throw new InvalidOperationException("At least one capability provider is required.");

        foreach (var provider in list)
        {
            if (string.IsNullOrWhiteSpace(provider.Id))
                throw new InvalidOperationException(
                    $"Capability provider id must be non-empty ({provider.GetType().Name}).");
            if (list.Count(p => p.Id == provider.Id) > 1)
                throw new InvalidOperationException($"Duplicate capability provider id '{provider.Id}'.");
            if (provider.Actions.Count == 0)
                throw new InvalidOperationException($"Capability provider '{provider.Id}' exposes no actions.");
            foreach (var action in provider.Actions)
            {
                if (!CapabilityNameRules.IsValidActionName(action.Name))
                    throw new InvalidOperationException(
                        $"Action name '{action.Name}' in provider '{provider.Id}' is invalid; " +
                        "use [A-Za-z0-9_] only.");
            }
        }

        var duplicate = list.SelectMany(p => p.Actions.Select(a => a.Name))
            .GroupBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Duplicate action name '{duplicate.Key}' across capability providers.");

        return new CapabilityRegistry(list);
    }

    public IReadOnlyList<ProviderCapabilities> Providers => _providers;

    public Result<ResolvedCapability> Resolve(string nameOrRef)
    {
        if (_byName.TryGetValue(nameOrRef, out var direct))
            return Result<ResolvedCapability>.Success(
                new ResolvedCapability(direct.Provider.Id, direct.Action));

        var dot = nameOrRef.IndexOf('.');
        if (dot > 0 && dot < nameOrRef.Length - 1)
        {
            var providerId = nameOrRef[..dot];
            var actionName = nameOrRef[(dot + 1)..];
            if (_providersById.TryGetValue(providerId, out var provider))
            {
                var action = provider.Actions.FirstOrDefault(a => a.Name == actionName);
                if (action is not null)
                    return Result<ResolvedCapability>.Success(new ResolvedCapability(providerId, action));
            }
        }

        return Result<ResolvedCapability>.Failure(new Error("UnknownAction",
            $"Unknown action '{nameOrRef}'. Available: {string.Join(", ", _byName.Keys.OrderBy(k => k))}."));
    }

    public async Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
    {
        var provider = _providersById[capability.ProviderId];
        return await provider.InvokeAsync(capability.Action.Name, jsonArguments, ct);
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: 9 tests pass (plus Task 1's 8).

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.Capability.Domain tests/eThangAgent.Capability.Domain.Tests
```

```bash
git commit -m "feat(capability-domain): add strict capability registry with ref resolution"
```

---

### Task 3: AgentToolsProvider — the ITool adapter

**Files:**

- Create: `src/eThangAgent.Capability.Domain/AgentToolBinding.cs`
- Create: `src/eThangAgent.Capability.Domain/AgentToolsProvider.cs`
- Create: `tests/eThangAgent.Capability.Domain.Tests/AgentToolsProviderTests.cs`

**Interfaces:**

- Consumes: `ITool`, `ToolDefinition`, `ToolParameterType`, `RawToolInput` (Tool.Domain); `ICapabilityProvider` (Task 1).
- Produces: `AgentToolBinding(ITool Tool, string Summary)`; `AgentToolsProvider(string id, IReadOnlyList<AgentToolBinding> bindings)` — actions mapped from tool definitions (`Type` rendered via `ToolParameterType.ToString()` → `String`/`Integer`), `InvokeAsync` delegating to `tool.ExecuteAsync(new RawToolInput(actionName, jsonArguments))` and carrying `IsError`; unknown action → `CapabilityInvocationResult.Fail("Error [UnknownAction]: Unknown action: X.")`.

- [ ] **Step 1: Write the failing tests**

`tests/eThangAgent.Capability.Domain.Tests/AgentToolsProviderTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class AgentToolsProviderTests
{
    private static AgentToolsProvider Create() =>
        new("agent",
            [new AgentToolBinding(new ReadTool(new FakeFileSystemAccess()), "Read lines from a text file.")]);

    [Fact]
    public void Actions_MappedFromToolDefinitions()
    {
        var action = Assert.Single(Create().Actions);

        Assert.Equal("read", action.Name);
        Assert.Equal("Read lines from a text file.", action.Summary);
        Assert.Contains("annotation", action.Description);
        Assert.Equal(3, action.Parameters.Count);
        Assert.Contains(action.Parameters, p => p.Name == "path" && p.Type == "String");
        Assert.Contains(action.Parameters, p => p.Name == "startLine" && p.Type == "Integer");
    }

    [Fact]
    public async Task InvokeAsync_DelegatesToTool_AndReturnsContent()
    {
        var result = await Create().InvokeAsync("read",
            """{"path":"x.txt","startLine":1,"endLine":2}""");

        Assert.False(result.IsError);
        Assert.Contains("[read x.txt lines 1-2 of 2 total]", result.Content);
        Assert.Contains("alpha", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_ToolError_CarriesIsErrorAndGutter()
    {
        var provider = new AgentToolsProvider("agent",
            [new AgentToolBinding(new ReadTool(new FailingFileSystemAccess()), "Read lines.")]);

        var result = await provider.InvokeAsync("read",
            """{"path":"missing.txt","startLine":1,"endLine":5}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [FileNotFound]:", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_UnknownAction_ReturnsError()
    {
        var result = await Create().InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Contains("Error [UnknownAction]: Unknown action: nope", result.Content);
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha", "beta"], 2, 2)));
    }

    private sealed class FailingFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Failure(
                new Error("FileNotFound", $"File not found: {path}.")));
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: build fails, `AgentToolsProvider` not found.

- [ ] **Step 3: Implement**

`src/eThangAgent.Capability.Domain/AgentToolBinding.cs`:

```csharp
using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

public sealed record AgentToolBinding(ITool Tool, string Summary);
```

`src/eThangAgent.Capability.Domain/AgentToolsProvider.cs`:

```csharp
using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

/// <summary>Exposes existing ITool instances as capability actions. Read's behavior,
///     format contract, and tests are unchanged — this is a pure adapter.</summary>
public sealed class AgentToolsProvider : ICapabilityProvider
{
    private readonly Dictionary<string, ITool> _tools;

    public AgentToolsProvider(string id, IReadOnlyList<AgentToolBinding> bindings)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _tools = bindings.ToDictionary(b => b.Tool.Definition.Name, b => b.Tool, StringComparer.Ordinal);
        Actions = bindings.Select(b => new ActionDescriptor(
            b.Tool.Definition.Name,
            b.Summary,
            b.Tool.Definition.Description,
            b.Tool.Definition.Parameters
                .Select(p => new ActionParameter(p.Name, p.Type.ToString(), p.Description))
                .ToList())).ToList();
    }

    public string Id { get; }

    public IReadOnlyList<ActionDescriptor> Actions { get; }

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(actionName, out var tool))
            return CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}.");

        var result = await tool.ExecuteAsync(new RawToolInput(actionName, jsonArguments), ct);
        return new CapabilityInvocationResult(result.Content, result.IsError);
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: 4 tests pass (plus prior 17).

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.Capability.Domain tests/eThangAgent.Capability.Domain.Tests
```

```bash
git commit -m "feat(capability-domain): adapt agent tools as capability actions"
```

---

### Task 4: CapabilityReferenceRenderer — generated action reference

**Files:**

- Create: `src/eThangAgent.Capability.Domain/CapabilityReferenceRenderer.cs`
- Create: `tests/eThangAgent.Capability.Domain.Tests/CapabilityReferenceRendererTests.cs`

**Interfaces:**

- Consumes: `ICapabilityRegistry` (Task 1).
- Produces: `CapabilityReferenceRenderer.Render(ICapabilityRegistry) → string` — `## Available actions` header, then per provider its id, then one line per action: `name(param: Type, …): Summary`.

- [ ] **Step 1: Write the failing test**

`tests/eThangAgent.Capability.Domain.Tests/CapabilityReferenceRendererTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityReferenceRendererTests
{
    [Fact]
    public void Render_GroupsByProvider_OneLinePerAction()
    {
        var registry = new StubRegistry(
            new ProviderCapabilities("agent",
            [
                new ActionDescriptor("read", "Read lines from a text file.", "full", 
                [
                    new ActionParameter("path", "String", "file path"),
                    new ActionParameter("startLine", "Integer", "first line"),
                    new ActionParameter("endLine", "Integer", "last line"),
                ]),
            ]));

        var text = CapabilityReferenceRenderer.Render(registry);

        Assert.Equal(
            "## Available actions\nagent:\nread(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            text);
    }

    private sealed class StubRegistry : ICapabilityRegistry
    {
        public StubRegistry(params ProviderCapabilities[] providers) => Providers = providers;

        public IReadOnlyList<ProviderCapabilities> Providers { get; }

        public Result<ResolvedCapability> Resolve(string nameOrRef)
            => Result<ResolvedCapability>.Failure(new Error("UnknownAction", "stub"));

        public Task<CapabilityInvocationResult> InvokeAsync(
            ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
            => Task.FromResult(CapabilityInvocationResult.Fail("stub"));
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: build fails, `CapabilityReferenceRenderer` not found.

- [ ] **Step 3: Implement**

`src/eThangAgent.Capability.Domain/CapabilityReferenceRenderer.cs`:

```csharp
using System.Text;

namespace eThangAgent.CapabilityDomain;

public static class CapabilityReferenceRenderer
{
    public static string Render(ICapabilityRegistry registry)
    {
        var sb = new StringBuilder("## Available actions");
        foreach (var provider in registry.Providers)
        {
            sb.Append("\n").Append($"{provider.Id}:");
            foreach (var action in provider.Actions)
            {
                var parameters = string.Join(", ",
                    action.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                sb.Append("\n").Append($"{action.Name}({parameters}): {action.Summary}");
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj`
Expected: 1 test passes (plus prior 21).

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.Capability.Domain tests/eThangAgent.Capability.Domain.Tests
```

```bash
git commit -m "feat(capability-domain): render generated action reference"
```

---

### Task 5: ToolBroker switches to ICapabilityRegistry

**Files:**

- Modify: `src/eThangAgent.PowerShell.ACL/ToolBroker.cs` (full rewrite)
- Modify: `tests/eThangAgent.PowerShell.ACL.Tests/ToolBrokerTests.cs` (full rewrite)

**Interfaces:**

- Consumes: `ICapabilityRegistry`, `AgentToolsProvider`, `AgentToolBinding` (Tasks 2–3); `PowerShellValueConverter`; `ReadTool`.
- Produces: `ToolBroker(ICapabilityRegistry)` with `InvokeTool(nameOrRef, input)` (resolve → convert → invoke; unknown action and conversion failures throw `ExecToolCallException` with gutters), `ListActions()` (compact `name(params)` lines), `DescribeAction(nameOrRef)` (summary + full description + per-parameter docs), `ListProviders()` (`id (N actions)`). `WrappableDefinitions` is **removed** — no nested exec is now structural (exec is never registered as an action).

- [ ] **Step 1: Replace the failing tests**

Replace the entire contents of `tests/eThangAgent.PowerShell.ACL.Tests/ToolBrokerTests.cs` with:

```csharp
using System.Collections;
using System.Management.Automation;
using eThangAgent.CapabilityDomain;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

public class ToolBrokerTests
{
    private static CapabilityRegistry Registry(ICapabilityProvider provider)
        => CapabilityRegistry.Create([provider]);

    private static ICapabilityProvider ReadProvider(IFileSystemAccess? files = null)
        => new AgentToolsProvider("agent",
            [new AgentToolBinding(new ReadTool(files ?? new FakeFileSystemAccess()),
                "Read lines from a text file.")]);

    [Fact]
    public void InvokeTool_UnknownAction_Throws_ListingAvailable()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("nope", new Hashtable()));

        Assert.Contains("Error [UnknownAction]:", ex.Message);
        Assert.Contains("read", ex.Message);
    }

    [Fact]
    public void InvokeTool_ByFullRef_Resolves()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var content = broker.InvokeTool("agent.read",
            new Hashtable { ["path"] = "x.txt", ["startLine"] = 1, ["endLine"] = 2 });

        Assert.Contains("[read x.txt lines 1-2 of 2 total]", content);
    }

    [Fact]
    public void InvokeTool_NullInput_Throws()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var ex = Assert.Throws<ExecToolCallException>(() => broker.InvokeTool("read", null));

        Assert.Contains("Error [InvalidToolInput]:", ex.Message);
    }

    [Fact]
    public void InvokeTool_ScriptBlockInput_Throws()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var ex = Assert.Throws<ExecToolCallException>(() => broker.InvokeTool("read",
            new Hashtable { ["path"] = ScriptBlock.Create("{ 1 }") }));

        Assert.Contains("Error [InvalidToolInput]:", ex.Message);
    }

    [Fact]
    public void InvokeTool_ConvertsInput_AndReturnsContent()
    {
        RawToolInput? received = null;
        var tool = new RecordingTool("read", r => received = r, "file content");
        var broker = new ToolBroker(Registry(new AgentToolsProvider("agent",
            [new AgentToolBinding(tool, "Read.")])));

        var content = broker.InvokeTool("read", new Hashtable { ["path"] = "a.txt" });

        Assert.Equal("file content", content);
        Assert.NotNull(received);
        Assert.Equal("read", received!.Name);
        Assert.Contains("\"path\":\"a.txt\"", received.JsonArguments);
    }

    [Fact]
    public void InvokeTool_ActionError_Throws_WithContent()
    {
        var tool = new RecordingTool("read", _ => { }, "Error [FileNotFound]: nope.", isError: true);
        var broker = new ToolBroker(Registry(new AgentToolsProvider("agent",
            [new AgentToolBinding(tool, "Read.")])));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("read", new Hashtable { ["path"] = "a.txt" }));

        Assert.Equal("Error [FileNotFound]: nope.", ex.Message);
    }

    [Fact]
    public void ListActions_CompactListing_NoExec()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var listing = broker.ListActions();

        Assert.Contains("read(path: String, startLine: Integer, endLine: Integer)", listing);
        Assert.DoesNotContain("exec(", listing);
    }

    [Fact]
    public void DescribeAction_ReturnsFullDescriptor()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var doc = broker.DescribeAction("read");

        Assert.Contains("read — Read lines from a text file.", doc);
        Assert.Contains("annotation line", doc);
        Assert.Contains("- path: String —", doc);
    }

    [Fact]
    public void DescribeAction_Unknown_Throws()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        Assert.Throws<ExecToolCallException>(() => broker.DescribeAction("nope"));
    }

    [Fact]
    public void ListProviders_ShowsIdAndCount()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var listing = broker.ListProviders();

        Assert.Equal("agent (1 actions)", listing);
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha", "beta"], 2, 2)));
    }

    private sealed class RecordingTool : ITool
    {
        private readonly Action<RawToolInput> _onExecute;
        private readonly string _content;
        private readonly bool _isError;

        public RecordingTool(string name, Action<RawToolInput> onExecute, string content,
            bool isError = false)
        {
            _onExecute = onExecute;
            _content = content;
            _isError = isError;
            Definition = new ToolDefinition(name, "desc", []);
        }

        public ToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        {
            _onExecute(input);
            return Task.FromResult(new ToolResult(_content, _isError));
        }
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj`
Expected: build fails — `ToolBroker` has no ctor taking `ICapabilityRegistry`.

- [ ] **Step 3: Rewrite the broker**

Replace the entire contents of `src/eThangAgent.PowerShell.ACL/ToolBroker.cs` with:

```csharp
using System.Text;
using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL;

/// <summary>Bridges in-script action calls into the ICapabilityRegistry. Blocking on the
///     async invocation is safe: the runspace pipeline thread has no synchronization context.</summary>
public sealed class ToolBroker
{
    private readonly ICapabilityRegistry _registry;

    public ToolBroker(ICapabilityRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string InvokeTool(string nameOrRef, object? input)
    {
        var resolved = _registry.Resolve(nameOrRef);
        if (!resolved.IsSuccess)
            throw new ExecToolCallException($"Error [UnknownAction]: {resolved.Error!.Message}");
        if (input is null)
            throw new ExecToolCallException(
                "Error [InvalidToolInput]: Pass a hashtable of tool arguments, e.g. " +
                "read @{ path = 'file.txt'; startLine = 1; endLine = 5 }.");

        string json;
        try
        {
            json = PowerShellValueConverter.ToJson(input);
        }
        catch (ExecInputConversionException ex)
        {
            throw new ExecToolCallException($"Error [InvalidToolInput]: {ex.Message}");
        }

        var result = _registry.InvokeAsync(resolved.Value!, json).GetAwaiter().GetResult();
        if (result.IsError)
            throw new ExecToolCallException(result.Content);
        return result.Content;
    }

    public string ListActions()
        => string.Join("\n", _registry.Providers.SelectMany(p => p.Actions)
            .Select(a => $"{a.Name}({string.Join(", ", a.Parameters.Select(p => $"{p.Name}: {p.Type}"))})"));

    public string DescribeAction(string nameOrRef)
    {
        var resolved = _registry.Resolve(nameOrRef);
        if (!resolved.IsSuccess)
            throw new ExecToolCallException($"Error [UnknownAction]: {resolved.Error!.Message}");
        var action = resolved.Value!.Action;
        var sb = new StringBuilder($"{action.Name} — {action.Summary}\n\n{action.Description}");
        foreach (var parameter in action.Parameters)
            sb.Append($"\n- {parameter.Name}: {parameter.Type} — {parameter.Description}");
        return sb.ToString();
    }

    public string ListProviders()
        => string.Join("\n", _registry.Providers.Select(p => $"{p.Id} ({p.Actions.Count} actions)"));
}

public sealed class ExecToolCallException : Exception
{
    public ExecToolCallException(string message) : base(message) { }
}
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj`
Expected: ToolBroker tests pass (engine tests will still reference the old ctor — if the build fails there, that is Task 6's scope; commit only when the full build is green by also applying Task 6, or stage this commit after Task 6's edits. Preferred: proceed directly to Task 6 and commit Tasks 5+6 together is NOT allowed — instead, if the build breaks on engine tests, temporarily keep a compile-compatible broker ctor overload `public ToolBroker(IToolRegistry)` marked `[Obsolete]` that throws `NotSupportedException`, commit Task 5 green, then remove it in Task 6.)

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.PowerShell.ACL/ToolBroker.cs tests/eThangAgent.PowerShell.ACL.Tests/ToolBrokerTests.cs
```

```bash
git commit -m "feat(powerShell-acl): route broker through the capability registry"
```

---

### Task 6: PowerShellExecEngine switches to ICapabilityRegistry

**Files:**

- Modify: `src/eThangAgent.PowerShell.ACL/PowerShellExecEngine.cs` (full rewrite)
- Modify: `tests/eThangAgent.PowerShell.ACL.Tests/PowerShellExecEngineValidateTests.cs`
- Modify: `tests/eThangAgent.PowerShell.ACL.Tests/PowerShellExecEngineExecuteTests.cs`

**Interfaces:**

- Consumes: `ICapabilityRegistry`, `AgentToolsProvider`, `AgentToolBinding`, `CapabilityRegistry` (Tasks 1–3); `ToolBroker` (Task 5).
- Produces: `PowerShellExecEngine(Lazy<ICapabilityRegistry>, ExecOptions)` + convenience `(ICapabilityRegistry, ExecOptions)`. Setup script injects one wrapper per registered action plus **five** fixed functions: `Invoke-AgentTool`, `Get-AgentTool` (compact listing), `Get-AgentAction <name>` (full docs), `Get-AgentProvider` (provider listing).

- [ ] **Step 1: Update the test wiring and add the new facts**

In BOTH `PowerShellExecEngineValidateTests.cs` and `PowerShellExecEngineExecuteTests.cs`, replace the `CreateEngine` helper(s) with the registry-based form (add usings `eThangAgent.CapabilityDomain` where missing):

```csharp
    private static PowerShellExecEngine CreateEngine(ExecOptions? options = null,
        IFileSystemAccess? files = null)
        => new(CapabilityRegistry.Create(
            [new AgentToolsProvider("agent",
                [new AgentToolBinding(
                    new ReadTool(files ?? new FakeFileSystemAccess()), "Read lines.")])]),
            options ?? ExecOptions.Default);
```

(The validate-tests variant drops the optional parameters and passes `ExecOptions.Default` directly.) Each file keeps its own private `FakeFileSystemAccess`; the execute-tests file additionally keeps `FailingFileSystemAccess` and `NamedFakeTool` may be **deleted** — exec exclusion is structural now.

Add these facts to `PowerShellExecEngineExecuteTests.cs`:

```csharp
    [Fact]
    public async Task InvokeAgentTool_ByFullRef_CallsAction()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram(
            "Invoke-AgentTool -Name agent.read -ToolInput @{ path = 'x.txt'; startLine = 1; endLine = 2 }"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("alpha", run.Output);
    }

    [Fact]
    public async Task GetAgentAction_ReturnsFullDescriptor()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram("Get-AgentAction read"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("read — Read lines.", run.Output);
        Assert.Contains("- path: String", run.Output);
    }

    [Fact]
    public async Task GetAgentProvider_ListsProviders()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram("Get-AgentProvider"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("agent (1 actions)", run.Output);
    }
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj`
Expected: build fails — engine has no ctor taking `ICapabilityRegistry`.

- [ ] **Step 3: Rewrite the engine**

Replace the entire contents of `src/eThangAgent.PowerShell.ACL/PowerShellExecEngine.cs` with:

```csharp
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Language;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL;

public sealed class PowerShellExecEngine : IExecEngine
{
    private readonly Lazy<ICapabilityRegistry> _registry;
    private readonly ExecOptions _options;

    /// <summary>Primary ctor. The registry is lazy: the composition root builds the
    ///     capability registry alongside tool wiring, and the engine must not force it
    ///     into existence before it is complete (DI cycle).</summary>
    public PowerShellExecEngine(Lazy<ICapabilityRegistry> registry, ExecOptions options)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Convenience ctor for tests and direct use.</summary>
    public PowerShellExecEngine(ICapabilityRegistry registry, ExecOptions options)
        : this(new Lazy<ICapabilityRegistry>(() => registry), options)
    {
    }

    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
        ExecProgram program, CancellationToken ct = default)
    {
        _ = Parser.ParseInput(program.Text, out _, out var parseErrors);
        var errors = parseErrors
            .Select(e => new ExecParseError(
                e.Extent.StartLineNumber, e.Extent.StartColumnNumber, e.Message))
            .ToList();
        return Task.FromResult(Result<IReadOnlyList<ExecParseError>>.Success(errors));
    }

    public async Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
    {
        var broker = new ToolBroker(_registry.Value);
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace.SessionStateProxy.PSVariable.Set("broker", broker);
        ps.AddScript(CreateSetupScript(_registry.Value));
        ps.AddScript(program.Text);

        Collection<PSObject> collected;
        try
        {
            var invokeTask = Task.Run(() => ps.Invoke());
            var completed = await Task.WhenAny(invokeTask, Task.Delay(_options.Timeout, ct));
            if (completed != invokeTask)
            {
                try { ps.Stop(); } catch { /* pipeline already stopping */ }
                try
                {
                    collected = await invokeTask; // Invoke returns what was collected before the stop
                }
                catch (Exception ex)
                {
                    return new ExecRunResult(ExecRunStatus.EngineFailure, "", [], ex.Message);
                }

                var status = ct.IsCancellationRequested
                    ? ExecRunStatus.Cancelled
                    : ExecRunStatus.Timeout;
                return new ExecRunResult(status, RenderOutput(collected), ErrorLines(ps),
                    status == ExecRunStatus.Cancelled
                        ? "Execution was cancelled."
                        : $"Execution timed out after {_options.Timeout.TotalSeconds:0} seconds; pipeline stopped.");
            }

            collected = await invokeTask;
        }
        catch (Exception ex)
        {
            return new ExecRunResult(ExecRunStatus.EngineFailure, "", [], ex.Message);
        }

        return new ExecRunResult(ExecRunStatus.Completed, RenderOutput(collected), ErrorLines(ps));
    }

    /// <summary>Functions are injected as setup-script text into a default PowerShell.Create()
    ///     runspace. CreateDefault2-based runspaces fail to load the built-in modules in
    ///     hosted (non-pwsh) processes; the default Create() runspace does not.</summary>
    private static string CreateSetupScript(ICapabilityRegistry registry)
        => string.Join("\n",
            registry.Providers
                .SelectMany(p => p.Actions)
                .Select(a =>
                    $"function {a.Name} {{ param([object]$ToolInput) $broker.InvokeTool('{a.Name}', $ToolInput) }}")
                .Append("function Invoke-AgentTool { param([string]$Name, [object]$ToolInput) $broker.InvokeTool($Name, $ToolInput) }")
                .Append("function Get-AgentTool { $broker.ListActions() }")
                .Append("function Get-AgentAction { param([string]$Name) $broker.DescribeAction($Name) }")
                .Append("function Get-AgentProvider { $broker.ListProviders() }"));

    private static IReadOnlyList<string> ErrorLines(System.Management.Automation.PowerShell ps)
        => ps.Streams.Error.Select(e => e.Exception.Message).ToList();

    private static string RenderOutput(IEnumerable<PSObject> output)
        => string.Join("\n", output.Select(o =>
        {
            var b = o.BaseObject;
            return b is string s ? s : PowerShellValueConverter.ToJson(b);
        }));
}
```

Also remove the Task 5 temporary shim if one was added (`ToolBroker(IToolRegistry)` marked `[Obsolete]`).

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj`
Expected: all pass — prior engine behavior unchanged, plus the three new facts.

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.PowerShell.ACL tests/eThangAgent.PowerShell.ACL.Tests
```

```bash
git commit -m "feat(powerShell-acl): execute against the capability registry"
```

---

### Task 7: ExecGuide v1.1 — introspection documentation

**Files:**

- Modify: `src/eThangAgent.Tool.Domain/ExecGuide.cs`
- Modify: `tests/eThangAgent.Tool.Domain.Tests/ExecGuideTests.cs`

**Interfaces:**

- Produces: `ExecGuide.Version` = `"1.1"`; guide documents `Get-AgentAction <name>` (full docs) and `Get-AgentProvider`. All existing markers preserved (`read @{`, `Invoke-AgentTool`, `Get-AgentTool`, `try/catch`, `[exec:artifact`).

- [ ] **Step 1: Update the failing tests**

In `tests/eThangAgent.Tool.Domain.Tests/ExecGuideTests.cs`, change the version assertion and add a new fact:

```csharp
    [Fact]
    public void Guide_IsVersionedAndNonEmpty()
    {
        Assert.Equal("1.1", ExecGuide.Version);
        Assert.True(ExecGuide.Text.Length >= 500);
    }

    [Fact]
    public void Guide_DocumentsIntrospection()
    {
        Assert.Contains("Get-AgentAction", ExecGuide.Text);
        Assert.Contains("Get-AgentProvider", ExecGuide.Text);
    }
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj`
Expected: version assert fails (still "1.0").

- [ ] **Step 3: Implement**

In `src/eThangAgent.Tool.Domain/ExecGuide.cs`: set `public const string Version = "1.1";` and extend the discovery block — replace:

```text
    Discover tools instead of guessing:

        Get-AgentTool
```

with:

```text
    Discover tools instead of guessing:

        Get-AgentTool

    Full documentation for any action (description + parameter docs):

        Get-AgentAction read

    Providers:

        Get-AgentProvider
```

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests/eThangAgent.Tool.Domain.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Build + commit**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN.

```bash
git add src/eThangAgent.Tool.Domain/ExecGuide.cs tests/eThangAgent.Tool.Domain.Tests/ExecGuideTests.cs
```

```bash
git commit -m "feat(tool-domain): document capability introspection in exec guide v1.1"
```

---

### Task 8: CLI wiring — exec-only surface + generated reference (includes E2E)

**Files:**

- Modify: `src/eThangAgent.CLI/ExecGuidePromptProvider.cs` (rewrite)
- Modify: `src/eThangAgent.CLI/eThangAgent.CLI.csproj` (+ Capability.Domain reference)
- Modify: `src/eThangAgent.CLI/Program.cs` (capability wiring; exec-only `IToolRegistry`)
- Modify: `tests/eThangAgent.CLI.Tests/E2ETests.cs` (collapse assertions)

**Interfaces:**

- Consumes: everything above.
- Produces: composition root where `ModelRequest.Tools` = `[exec]`; `ICapabilityRegistry` = `agent` provider exposing `read`; system prompt = identity + static guide + generated action reference.

This task deliberately merges wiring and E2E updates: wiring alone would leave the old native-read E2E red, violating the every-task-green rule.

- [ ] **Step 1: Rewrite the failing E2E assertions**

In `tests/eThangAgent.CLI.Tests/E2ETests.cs`:

(a) **Delete** `Repl_ExecutesReadTool_EndToEnd` entirely (native read no longer exists; the exec-calls-read path is covered by `Repl_ExecutesExecTool_EndToEnd`).

(b) Add this replacement fact:

```csharp
    [Fact]
    public async Task Repl_ModelToolsContainOnlyExec()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("hi");
        await process.StandardInput.FlushAsync();
        await ReadUntil(reader, "> ");

        Assert.NotNull(mock.LastChatRequestBody);
        Assert.Contains("\"name\":\"exec\"", mock.LastChatRequestBody);
        Assert.DoesNotContain("\"name\":\"read\"", mock.LastChatRequestBody);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }
```

(c) In `Repl_SendsExecGuide_InSystemPrompt`, add the generated-reference assertion before `/quit`:

```csharp
        Assert.Contains(
            "read(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            mock.LastChatRequestBody);
```

- [ ] **Step 2: Rewrite ExecGuidePromptProvider (registry-aware)**

Replace the entire contents of `src/eThangAgent.CLI/ExecGuidePromptProvider.cs` with:

```csharp
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

public sealed class ExecGuidePromptProvider : ISystemPromptProvider
{
    private readonly ICapabilityRegistry _registry;

    public ExecGuidePromptProvider(ICapabilityRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string Build() => $"{ExecGuide.Text}\n\n{CapabilityReferenceRenderer.Render(_registry)}";
}
```

- [ ] **Step 3: Wire the composition root**

`src/eThangAgent.CLI/eThangAgent.CLI.csproj` — add:

```xml
    <ProjectReference Include="../eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
```

`src/eThangAgent.CLI/Program.cs` — add `using eThangAgent.CapabilityDomain;`, then replace the block from `.AddSingleton<IFileSystemAccess, …>()` through the `ISystemPromptProvider` registration with:

```csharp
            .AddSingleton<IFileSystemAccess, PowerShellFileSystemAccess>()
            .AddSingleton(ExecOptions.Default)
            .AddSingleton<IExecOutputStore>(_ => new ExecArtifactStore())
            .AddSingleton<IExecActivitySink>(_ => NullExecActivitySink.Instance)
            .AddSingleton<AgentToolsProvider>(sp => new AgentToolsProvider("agent",
                [new AgentToolBinding(
                    new ReadTool(sp.GetRequiredService<IFileSystemAccess>()),
                    "Read lines from a text file.")]))
            .AddSingleton<ICapabilityRegistry>(sp =>
                CapabilityRegistry.Create([sp.GetRequiredService<AgentToolsProvider>()]))
            .AddSingleton<IExecEngine>(sp => new PowerShellExecEngine(
                new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>()),
                sp.GetRequiredService<ExecOptions>()))
            .AddSingleton<ITool>(sp => new ExecTool(
                sp.GetRequiredService<IExecEngine>(),
                sp.GetRequiredService<ExecOptions>(),
                sp.GetRequiredService<IExecOutputStore>(),
                sp.GetRequiredService<IExecActivitySink>()))
            .AddSingleton<IToolRegistry>(sp =>
                new ToolRegistry([sp.GetRequiredService<ITool>()]))
            .AddSingleton<ExecGuidePromptProvider>()
            .AddSingleton<ISystemPromptProvider>(sp => new CompositeSystemPromptProvider(
            [
                new StaticPromptProvider(
                    "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
                    "workspace, prefer the provided tools over guessing, and keep responses tight."),
                sp.GetRequiredService<ExecGuidePromptProvider>(),
            ]))
```

Note what disappeared: the `ReadTool` `ITool` registration (read now exists only as capability action `agent.read`) and the `Lazy<IToolRegistry>` engine dependency (now `Lazy<ICapabilityRegistry>`).

- [ ] **Step 4: Verify**

Run:

```
cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|FAILED' || echo BUILD-CLEAN
dotnet test tests/eThangAgent.Agent.Domain.Tests/eThangAgent.Agent.Domain.Tests.csproj --no-build --nologo 2>&1 | tail -2
dotnet test tests/eThangAgent.CLI.Tests/eThangAgent.CLI.Tests.csproj --no-build --nologo 2>&1 | tail -2
```

Expected: BUILD-CLEAN; all suites pass including the three E2E collapse facts.

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.CLI tests/eThangAgent.CLI.Tests/E2ETests.cs
```

```bash
git commit -m "feat(cli): collapse model surface to exec with registry-generated docs"
```

---

### Task 9: Full verification

**Files:** none created — solution-level verification.

**Interfaces:**

- Consumes: everything.
- Produces: the definition of done.

- [ ] **Step 1: Full build with warning scan**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet build --nologo -v q 2>&1 | rg 'error|warning|FAILED' || echo BUILD-CLEAN`
Expected: BUILD-CLEAN (no new warnings; fix, don't suppress).

- [ ] **Step 2: Full test suite**

Run: `cd /c/Users/glove/projects/ethang-agent && dotnet test --nologo 2>&1 | rg 'Passed!|Failed!' | head -15`
Expected: every project green — prior 209 plus the new Capability.Domain suite and the migrated broker/engine suites.

- [ ] **Step 3: Coverage**

Run:

```
dotnet test tests/eThangAgent.Capability.Domain.Tests/eThangAgent.Capability.Domain.Tests.csproj --collect:'XPlat Code Coverage' --nologo 2>&1 | tail -2
fd --no-ignore 'coverage.cobertura.xml' tests/eThangAgent.Capability.Domain.Tests | head -3
```

(P1 lesson: `TestResults` is gitignored — always `fd --no-ignore`.) Extract per-class rates with `rg -o 'name="eThangAgent.CapabilityDomain.[A-Za-z]+"[^>]*line-rate="[0-9.]+"' <newest-file>`.
Expected: Capability.Domain classes ~100%; re-check PowerShell.ACL ≥ 80% the same way. If below, add targeted tests — never weaken assertions to raise numbers.

- [ ] **Step 4: Spec cross-check**

Run: `rg -n 'Duplicate action name|Unknown action|provider.action' src/eThangAgent.CapabilityDomain/CapabilityRegistry.cs src/eThangAgent.PowerShell.ACL/ToolBroker.cs 2>/dev/null || rg -n 'Duplicate action name|Unknown action' src/eThangAgent.Capability.Domain/CapabilityRegistry.cs src/eThangAgent.PowerShell.ACL/ToolBroker.cs`
Expected: strict-registration throw, unknown-action listing, and ref handling all present exactly as specified.

- [ ] **Step 5: Final commit (if anything is pending)**

```bash
git status --short
```

If clean, done. If stray files, commit with a `chore:` message describing them.

---

## Spec Coverage Map

| Spec item | Where implemented |
| --- | --- |
| D1 collapse to exec-only model surface | Task 8 (wiring + E2E `Repl_ModelToolsContainOnlyExec`) |
| D2 Capability.Domain bounded context; ReadTool untouched | Tasks 1–3 (records, registry, adapter) |
| D3 generated docs: session-start reference + on-demand full docs | Tasks 4 (renderer), 7 (guide v1.1), 8 (registry-aware provider + E2E reference-line assert) |
| Refs `provider.action`, bare-name wrappers | Tasks 2 (Resolve), 5 (dispatcher accepts both), 6 (setup script) |
| Strict registration, fail fast | Task 2 (`CapabilityRegistry.Create`) |
| Guardrails carried (budgets, gutters, no nested exec) | Task 6 (engine unchanged; exec structurally unregistered) |
| Three-layer testing | All tasks + Task 9 |

## Out of Scope (next cycles)

MCP provider via its own ACL (the `ICapabilityProvider` seam is its landing spot), additional providers with P3+ (state/schema) and P6 (memory), nested/recursive exec at P4, discovery-first token knob at P5+, pi-fabric component-plane machinery (not ported).
