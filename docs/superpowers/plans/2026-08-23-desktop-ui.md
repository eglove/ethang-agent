# Desktop UI (Avalonia) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give eThang Agent an Avalonia desktop frontend with strict feature parity to the CLI, over a shared extracted composition so any frontend can be added or removed independently.

**Architecture:** Three work streams from the spec: (1) extract an `IPathResolver` seam plus an unrooted resolver so tools work without a workspace; (2) move all host-agnostic DI wiring out of `eThangAgent.CLI/Program.cs` into a new `eThangAgent.Composition`; (3) build `eThangAgent.Desktop` (Avalonia MVVM) that consumes the composition with its own clarify channel, stream bridge, and views. The CLI keeps byte-for-byte behavior; its `Main` shrinks to config-load + core-registration + terminal bits + existing REPL loops.

**Tech Stack:** .NET 10 (TFM `net10.0` from `Directory.Build.props`), C#, Avalonia 11.3.*, CommunityToolkit.Mvvm 8.*, xUnit 2.* (auto-injected by `tests/Directory.Build.props`), System.Threading.Channels.

**Spec:** `docs/superpowers/specs/2026-08-23-desktop-ui-design.md`

## Global Constraints

- Platform: Windows only; shell scripts are PowerShell only (no .sh/.cmd/.bat).
- TFM `net10.0` everywhere (inherited from `Directory.Build.props`; do not restate per-project).
- Test projects inherit xUnit/Test.Sdk/coverlet from `tests/Directory.Build.props`; declare only project references unless adding UI-specific packages.
- Expected failures are `Result<T>` errors; exceptions are programmer/infrastructure errors only.
- Strict input validation: nothing silently coerced, defaulted, or clamped.
- Domain namespaces omit the dot (`eThangAgent.ToolDomain`); ACL/composition namespaces keep it (`eThangAgent.Composition`).
- No domain project may reference Avalonia. `eThangAgent.Desktop` must not reference `eThangAgent.CLI` or `eThangAgent.Terminal.ACL`.
- Every task leaves the whole solution building green and all tests passing before its commit.
- Pin Avalonia packages to version `11.3.*` exactly across Desktop and Desktop.Tests.
- Default model stays `stealth/ox-alpha`, declared by each host: `ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!`.
- Update `README.md` in the same change-set (final task owns it).

---

### Task 1: Desktop skeleton + headless smoke (risk-register gate)

Proves Avalonia restores/builds/runs headless on net10.0 **before** anything depends on it.

**Files:**

- Create: `src/eThangAgent.Desktop/eThangAgent.Desktop.csproj`
- Create: `src/eThangAgent.Desktop/Program.cs`
- Create: `src/eThangAgent.Desktop/App.axaml`
- Create: `src/eThangAgent.Desktop/App.axaml.cs`
- Create: `tests/eThangAgent.Desktop.Tests/eThangAgent.Desktop.Tests.csproj`
- Create: `tests/eThangAgent.Desktop.Tests/DesktopSmokeTests.cs`
- Modify: `eThangAgent.slnx` (add both projects)

**Interfaces:**

- Consumes: nothing (skeleton).
- Produces: runnable `App` with `DataContext`-free `MainWindow` placeholder; headless test fixture pattern reused by Tasks 12–13.

- [ ] **Step 1: Create the Desktop project files**

`src/eThangAgent.Desktop/eThangAgent.Desktop.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.*" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.*" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
  </ItemGroup>
</Project>
```

`src/eThangAgent.Desktop/Program.cs`:

```csharp
using Avalonia;

namespace eThangAgent.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
```

`src/eThangAgent.Desktop/App.axaml`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="eThangAgent.Desktop.App"
             RequestedThemeVariant="Dark">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

`src/eThangAgent.Desktop/App.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

namespace eThangAgent.Desktop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
```

Create placeholder `src/eThangAgent.Desktop/Views/MainWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="eThangAgent.Desktop.Views.MainWindow"
        Title="eThang Agent" Width="900" Height="650">
  <TextBlock Text="eThang Agent" HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Window>
```

And `src/eThangAgent.Desktop/Views/MainWindow.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace eThangAgent.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 2: Create the headless test project**

`tests/eThangAgent.Desktop.Tests/eThangAgent.Desktop.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Avalonia.Headless.XUnit" Version="11.3.*" />
    <ProjectReference Include="../../src/eThangAgent.Desktop/eThangAgent.Desktop.csproj" />
  </ItemGroup>
</Project>
```

`tests/eThangAgent.Desktop.Tests/DesktopSmokeTests.cs`:

```csharp
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop;
using eThangAgent.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace eThangAgent.Desktop.Tests;

public class TestApp
{
    internal static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class DesktopSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_Instantiates_And_Has_Title()
    {
        var window = new MainWindow();
        Assert.Equal("eThang Agent", window.Title);
    }
}
```

- [ ] **Step 3: Register both projects in the solution**

Append to `eThangAgent.slnx` (inside the top-level `<Solution>`, alongside the other flat entries):

```xml
  <Project Path="src/eThangAgent.Desktop/eThangAgent.Desktop.csproj" />
  <Project Path="tests/eThangAgent.Desktop.Tests/eThangAgent.Desktop.Tests.csproj" />
```

- [ ] **Step 4: Verify restore + build + smoke test (the risk-register gate)**

Run: `dotnet build && dotnet test tests/eThangAgent.Desktop.Tests`
Expected: restore succeeds with Avalonia 11.3.* on net10.0; build green; smoke test passes.
If package restore fails here, STOP and resolve pinning before proceeding — every later task depends on this.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: scaffold Avalonia desktop skeleton with headless smoke test"
```

---

### Task 2: Extract IPathResolver seam

**Files:**

- Create: `src/eThangAgent.Tool.Domain/IPathResolver.cs`
- Modify: `src/eThangAgent.Tool.Domain/WorkspacePathResolver.cs` (declare interface)
- Modify: `src/eThangAgent.Tool.Domain/WriteTool.cs`, `EditTool.cs`, `SearchTool.cs`, `GitStatusTool.cs`, `WorkingDiffTool.cs`, `GitCommitTool.cs` (constructor + field type)
- Test: `tests/eThangAgent.Tool.Domain.Tests/PathResolutionContractTests.cs` (new)

**Interfaces:**

- Consumes: existing `WorkspacePathResolver.Resolve(string) : Result<string>`.
- Produces: `namespace eThangAgent.ToolDomain { public interface IPathResolver { Result<string> Resolve(string path); } }` — Tasks 5/7 bind implementations through this.

Note: existing tool tests construct tools with `WorkspacePathResolver` instances, which still satisfy the widened constructor parameters — they compile unchanged.

- [ ] **Step 1: Write the failing conformance test**

`tests/eThangAgent.Tool.Domain.Tests/PathResolutionContractTests.cs`:

```csharp
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class PathResolutionContractTests
{
    [Fact]
    public void WorkspacePathResolver_Satisfies_IPathResolver_Contract()
    {
        IPathResolver resolver = new WorkspacePathResolver("C:\\tmp\\ws");
        var result = resolver.Resolve("C:\\tmp\\ws\\file.txt");
        Assert.True(result.IsSuccess);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter PathResolutionContractTests`
Expected: FAIL — `IPathResolver` does not exist.

- [ ] **Step 3: Implement the seam**

`IPathResolver.cs`:

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Seam for turning a model-supplied path argument into an absolute path.</summary>
public interface IPathResolver
{
    Result<string> Resolve(string path);
}
```

`WorkspacePathResolver.cs`: change the declaration line to
`public sealed class WorkspacePathResolver : IPathResolver` (body unchanged; ensure `using eThangAgent.SharedKernel;` present).

In each of the six tools, change the field and constructor parameter type from `WorkspacePathResolver` to `IPathResolver` (null-guards unchanged), e.g. WriteTool:

```csharp
private readonly IPathResolver _resolver;

public WriteTool(IPathResolver resolver, IFileWriteAccess files)
{
    _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    _files = files ?? throw new ArgumentNullException(nameof(files));
}
```

Apply identically to `EditTool(IPathResolver, IFileEditAccess)`, `SearchTool(IPathResolver, ISearchAccess)`, `GitStatusTool(IPathResolver, IGitQueryAccess)`, `WorkingDiffTool(IPathResolver, IGitQueryAccess)`, `GitCommitTool(IPathResolver, IGitCommitAccess)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests`
Expected: PASS — new contract test plus all pre-existing tool tests untouched and green.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "refactor: extract IPathResolver seam from WorkspacePathResolver"
```

---

### Task 3: UnrootedPathResolver (TDD)

**Files:**

- Create: `src/eThangAgent.Tool.Domain/UnrootedPathResolver.cs`
- Test: `tests/eThangAgent.Tool.Domain.Tests/UnrootedPathResolverTests.cs`

**Interfaces:**

- Consumes: `IPathResolver` (Task 2).
- Produces: `UnrootedPathResolver : IPathResolver` — absolute paths verbatim-normalized, relative resolved against process CWD, never rejects containment, malformed paths fail `InvalidPath`. Task 7 binds it in the Desktop option set.

- [ ] **Step 1: Write the failing tests**

```csharp
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class UnrootedPathResolverTests
{
    private readonly UnrootedPathResolver _resolver = new();

    [Theory]
    [InlineData("C:\\work\\a.txt")]
    [InlineData("D:\\deep\\dir\\note.md")]
    public void Absolute_Paths_Pass_Through_Normalized(string path)
    {
        var result = _resolver.Resolve(path);
        Assert.True(result.IsSuccess);
        Assert.Equal(System.IO.Path.GetFullPath(path), result.Value);
    }

    [Fact]
    public void Relative_Paths_Resolve_Against_Process_Cwd()
    {
        var result = _resolver.Resolve("src\\file.cs");
        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src\\file.cs")), result.Value);
    }

    [Fact]
    public void Traversal_Is_Never_Rejected_As_Outside_Anything()
    {
        var result = _resolver.Resolve("..\\other\\file.txt");
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Or_Whitespace_Fails_InvalidPath(string path)
    {
        var result = _resolver.Resolve(path);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidPath", result.Error!.Code);
    }

    [Fact]
    public void Malformed_Path_Fails_InvalidPath_Not_Exception()
    {
        var result = _resolver.Resolve("C:\\bad\\|<>\"");
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidPath", result.Error!.Code);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests --filter UnrootedPathResolverTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

```csharp
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Resolves model-supplied paths without a workspace root: absolute paths pass
///     through verbatim (normalized), relative paths resolve against the process working
///     directory, and no containment rule ever rejects a path. Malformed paths still fail
///     with the same InvalidPath error contract as WorkspacePathResolver.</summary>
public sealed class UnrootedPathResolver : IPathResolver
{
    public Result<string> Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string>.Failure(new Error("InvalidPath",
                "'path' must be a non-empty string."));

        try
        {
            return Result<string>.Success(Path.GetFullPath(path));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Failure(new Error("InvalidPath",
                $"'path' could not be resolved: {ex.Message}"));
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Tool.Domain.Tests`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: add UnrootedPathResolver for workspace-free frontends"
```

---

### Task 4: Composition skeleton — options, configuration, fixed workspace context

**Files:**

- Create: `src/eThangAgent.Composition/eThangAgent.Composition.csproj`
- Create: `src/eThangAgent.Composition/AgentHostOptions.cs`
- Create: `src/eThangAgent.Composition/FixedWorkspaceContext.cs`
- Create: `src/eThangAgent.Composition/AgentSettings.cs`
- Create: `src/eThangAgent.Composition/AgentConfiguration.cs`
- Move: `src/eThangAgent.CLI/SubAgentConfiguration.cs` → `src/eThangAgent.Composition/SubAgentConfiguration.cs` (namespace `eThangAgent.Composition`)
- Move: `src/eThangAgent.CLI/MaxToolIterationsConfiguration.cs` → `src/eThangAgent.Composition/MaxToolIterationsConfiguration.cs` (same treatment)
- Modify: `src/eThangAgent.CLI/Program.cs` (usings only — still compiles)
- Modify: `eThangAgent.slnx`
- Test: `tests/eThangAgent.Composition.Tests/AgentConfigurationTests.cs` (new project, csproj mirrors other test projects: single ProjectReference to Composition)

**Interfaces:**

- Consumes: `SubAgentOptions` (Agent.Domain), `IWorkspaceContext` (`eThangAgent.StateDomain`), `IPathResolver`/`UnrootedPathResolver` (Tool.Domain), `IClarifyChannel` (Tool.Domain), `OpenRouterConfiguration` (OpenRouter.ACL).
- Produces:

```csharp
public sealed record AgentSettings(string? ApiKey, Uri BaseUrl, SubAgentOptions SubAgents, int MaxToolIterations);
public static AgentSettings AgentConfiguration.Load();   // strict; ApiKey may be null; invalid optional bindings throw InvalidOperationException
public sealed record AgentHostOptions(IClarifyChannel ClarifyChannel, IWorkspaceContext WorkspaceContext, IPathResolver PathResolver);
public sealed class FixedWorkspaceContext(string id) : IWorkspaceContext { public string WorkspaceId { get; } = id; }
```

- [ ] **Step 1: Create project + supporting types**

`src/eThangAgent.Composition/eThangAgent.Composition.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../eThangAgent.Agent.Application/eThangAgent.Agent.Application.csproj" />
    <ProjectReference Include="../eThangAgent.Agent.Infrastructure/eThangAgent.Agent.Infrastructure.csproj" />
    <ProjectReference Include="../eThangAgent.Agent.Domain/eThangAgent.Agent.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.OpenRouter.ACL/eThangAgent.OpenRouter.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.FileSystem.ACL/eThangAgent.FileSystem.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.Storage.ACL/eThangAgent.Storage.ACL.csproj" />
    <ProjectReference Include="../eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.Memory.Domain/eThangAgent.Memory.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.Skill.Domain/eThangAgent.Skill.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.Conversation.Domain/eThangAgent.Conversation.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.Model.Domain/eThangAgent.Model.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
  </ItemGroup>
</Project>
```

`AgentHostOptions.cs`:

```csharp
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The three presentation-scoped decisions a frontend supplies to the shared core.
///     Everything else about hosting is identical across frontends.</summary>
public sealed record AgentHostOptions(
    IClarifyChannel ClarifyChannel,
    IWorkspaceContext WorkspaceContext,
    IPathResolver PathResolver);
```

`FixedWorkspaceContext.cs`:

```csharp
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Constant workspace identity for frontends without a workspace concept.
///     Scopes curated-memory writes only; replaced by the future multi-workspace design.</summary>
public sealed class FixedWorkspaceContext(string id) : IWorkspaceContext
{
    public string WorkspaceId { get; } = id;
}
```

`AgentSettings.cs`:

```csharp
using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Everything a host needs before building the core. ApiKey may be null — each
///     host decides how to present a missing key (CLI throws; Desktop shows a dialog).</summary>
public sealed record AgentSettings(
    string? ApiKey,
    Uri BaseUrl,
    SubAgentOptions SubAgents,
    int MaxToolIterations);
```

- [ ] **Step 2: Write failing configuration tests**

`tests/eThangAgent.Composition.Tests/AgentConfigurationTests.cs` (project references only Composition):

```csharp
using eThangAgent.Composition;

namespace eThangAgent.Composition.Tests;

public class AgentConfigurationTests
{
    [Fact]
    public void Missing_Api_Key_Is_Null_Not_Throw() =>
        Assert.Null(Load(env: []).ApiKey);

    [Fact]
    public void Api_Key_Is_Read_From_Environment()
    {
        var s = Load(env: [("OPENROUTER_API_KEY", "sk-or-test")]);
        Assert.Equal("sk-or-test", s.ApiKey);
    }

    [Fact]
    public void Base_Url_Defaults_To_OpenRouter()
    {
        var s = Load(env: []);
        Assert.Equal(new Uri("https://openrouter.ai"), s.BaseUrl);
    }

    [Fact]
    public void Base_Url_Override_Is_Honored()
    {
        var s = Load(env: [("OPENROUTER_BASE_URL", "http://localhost:5599")]);
        Assert.Equal(new Uri("http://localhost:5599"), s.BaseUrl);
    }

    [Fact]
    public void Invalid_SubAgent_Configuration_Throws()
    {
        var ex = Record.Exception(() => Load(env: [
            ("OPENROUTER_API_KEY", "k"), ("SubAgent__MaxConcurrentAgents", "0")]));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Invalid_Max_Tool_Iterations_Throws()
    {
        var ex = Record.Exception(() => Load(env: [
            ("OPENROUTER_API_KEY", "k"), ("Agent__MaxToolIterations", "abc")]));
        Assert.IsType<InvalidOperationException>(ex);
    }

    private static AgentSettings Load(params (string Key, string Value)[] env)
    {
        foreach (var (key, value) in env) Environment.SetEnvironmentVariable(key, value);
        try { return AgentConfiguration.Load(); }
        finally { foreach (var (key, _) in env) Environment.SetEnvironmentVariable(key, null); }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Composition.Tests`
Expected: FAIL — `AgentConfiguration` does not exist.

- [ ] **Step 4: Implement AgentConfiguration.Load**

```csharp
using Microsoft.Extensions.Configuration;

namespace eThangAgent.Composition;

/// <summary>Shared, strict configuration load for every host: optional appsettings.json next
///     to the executable, overridden by environment variables. Optional-value binding errors
///     throw InvalidOperationException — never coerced, defaulted, or clamped.</summary>
public static class AgentConfiguration
{
    public static AgentSettings Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var baseUrlEnv = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
        var baseUrl = string.IsNullOrWhiteSpace(baseUrlEnv)
            ? new Uri("https://openrouter.ai")
            : new Uri(baseUrlEnv);

        var subAgents = SubAgentConfiguration.Bind(
            configuration["SubAgent:DefaultModel"],
            configuration["SubAgent:ChildTimeoutSeconds"],
            configuration["SubAgent:MaxConcurrentAgents"]);
        var maxToolIterations = MaxToolIterationsConfiguration.Bind(
            configuration["Agent:MaxToolIterations"]);

        return new AgentSettings(apiKey, baseUrl, subAgents, maxToolIterations);
    }
}
```

Move `SubAgentConfiguration.cs` and `MaxToolIterationsConfiguration.cs` from CLI into this project changing only their `namespace` lines to `eThangAgent.Composition;` (bodies verbatim).

- [ ] **Step 5: Fix CLI compilation**

In `src/eThangAgent.CLI/Program.cs` add `using eThangAgent.Composition;`. Delete the two moved source files from the CLI project. Nothing else changes yet.

Run: `dotnet build && dotnet test tests/eThangAgent.Composition.Tests && dotnet test tests/eThangAgent.CLI.Tests`
Expected: build green; new tests PASS; existing CLI tests PASS.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: add composition skeleton with strict shared configuration load"
```

---

### Task 5: Move host-agnostic prompt providers and conversation repository

**Files:**

- Move: `src/eThangAgent.CLI/SuperpowersBootstrapPromptProvider.cs` → `src/eThangAgent.Composition/SuperpowersBootstrapPromptProvider.cs`
- Move: `src/eThangAgent.CLI/ExecGuidePromptProvider.cs` → `src/eThangAgent.Composition/ExecGuidePromptProvider.cs`
- Move: `src/eThangAgent.CLI/InMemoryConversationRepository.cs` → `src/eThangAgent.Composition/InMemoryConversationRepository.cs`
- Create: `src/eThangAgent.Composition/CuratedMemoryGuidePromptProvider.cs` (extracted from bottom of `Program.cs`)
- Modify: all four files' `namespace` lines to `eThangAgent.Composition`
- Modify: `src/eThangAgent.CLI/Program.cs` — delete the `CuratedMemoryGuidePromptProvider` class; keep `using eThangAgent.Composition;`
- Modify: `tests/eThangAgent.CLI.Tests/SuperpowersBootstrapTests.cs` — add `using eThangAgent.Composition;` if it referenced the old namespace directly

**Interfaces:**

- Produces: `SuperpowersBootstrapPromptProvider`, `ExecGuidePromptProvider`, `CuratedMemoryGuidePromptProvider : ISystemPromptProvider`, `InMemoryConversationRepository : IConversationRepository` — consumed by name in Task 7 wiring.

- [ ] **Step 1: Perform the moves** (git-tracked rename + namespace edit; bodies verbatim)

```powershell
git mv src/eThangAgent.CLI/SuperpowersBootstrapPromptProvider.cs src/eThangAgent.Composition/
git mv src/eThangAgent.CLI/ExecGuidePromptProvider.cs src/eThangAgent.Composition/
git mv src/eThangAgent.CLI/InMemoryConversationRepository.cs src/eThangAgent.Composition/
```

Edit the three files' `namespace` lines to `eThangAgent.Composition`. Cut the `CuratedMemoryGuidePromptProvider` class out of `Program.cs` into `src/eThangAgent.Composition/CuratedMemoryGuidePromptProvider.cs` under the same new namespace.

- [ ] **Step 2: Build + affected tests**

Run: `dotnet build && dotnet test tests/eThangAgent.CLI.Tests`
Expected: green; SuperpowersBootstrap tests pass against the relocated provider.

- [ ] **Step 3: Commit**

```powershell
git add -A
git commit -m "refactor: move prompt providers and conversation repository into Composition"
```

---

### Task 6: RootSessionLifecycle extraction (TDD)

**Files:**

- Create: `src/eThangAgent.Composition/RootSessionLifecycle.cs`
- Test: `tests/eThangAgent.Composition.Tests/RootSessionLifecycleTests.cs`

**Interfaces:**

- Consumes: `IAgentStore` (`eThangAgent.AgentDomain`: `SaveAsync(AgentRecord)`, `GetAsync(AgentId) : Result<AgentRecord?>`, `UpdateAsync(AgentRecord)`, `AppendMessageAsync(AgentId, Message)`), `Conversation`, `Result<string>`.
- Produces:

```csharp
public sealed class RootSessionLifecycle(IAgentStore store)
{
    public Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
        int messageCountBefore, Result<string> result, Action<string> reportError);
    public Task CompleteAsync(AgentId rootId, Action<string> reportError);
}
```

Semantics copied verbatim from `Program.AppendExchangeAsync`/`CompleteRootSessionAsync`: failed turns append nothing; persistence failures call `reportError` and continue; completion transitions the persisted row to `AgentStatus.Completed` preserving all other fields.

- [ ] **Step 1: Write failing tests with a recording fake store**

```csharp
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

sealed class FakeAgentStore : IAgentStore
{
    public List<AgentRecord> Saved = [];
    public List<(AgentId Id, Message Message)> Appended = [];
    public AgentRecord? Current;
    public Result<bool> SaveOutcome = Result<bool>.Success(true);
    public Result<AgentRecord?> GetOutcome;
    public Result<bool> UpdateOutcome = Result<bool>.Success(true);
    public Result<bool> AppendOutcome = Result<bool>.Success(true);

    public Task<Result<bool>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    { Saved.Add(record); return Task.FromResult(SaveOutcome); }
    public Task<Result<AgentRecord?>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(GetOutcome.IsSuccess ? Result<AgentRecord?>.Success(Current) : Result<AgentRecord?>.Failure(GetOutcome.Error!));
    public Task<Result<bool>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    { Current = record; return Task.FromResult(UpdateOutcome); }
    public Task<Result<bool>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    { Appended.Add((id, message)); return Task.FromResult(AppendOutcome); }
}
```

Adjust fake member signatures to the actual `IAgentStore` members if they differ in detail — the compiler will say precisely what; do not weaken the recorded-call assertions.

Tests:

```csharp
public class RootSessionLifecycleTests
{
    private static readonly AgentId RootId = AgentId.NewId();

    [Fact]
    public async Task Failed_Turn_Appends_Nothing()
    {
        var store = new FakeAgentStore();
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.AppendExchangeAsync(RootId, new Conversation(), 0,
            Result<string>.Failure(new Error("E", "boom")), errors.Add);
        Assert.Empty(store.Appended);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Successful_Turn_Appends_User_Then_Assistant_Message()
    {
        var store = new FakeAgentStore();
        var lifecycle = new RootSessionLifecycle(store);
        var conversation = new Conversation();
        conversation.AddUserMessage("hi");
        conversation.AddAssistantMessage("hello");
        await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
            Result<string>.Success("hello"), _ => Assert.Fail("no errors expected"));
        Assert.Equal(2, store.Appended.Count);
        Assert.Equal(Role.User, store.Appended[0].Message.Role);
        Assert.Equal(Role.Assistant, store.Appended[^1].Message.Role);
    }

    [Fact]
    public async Task Append_Failures_Surface_Via_ReportError_And_Continue()
    {
        var store = new FakeAgentStore
        {
            AppendOutcome = Result<bool>.Failure(new Error("DbDown", "nope"))
        };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        var conversation = new Conversation();
        conversation.AddUserMessage("hi");
        conversation.AddAssistantMessage("hello");
        await lifecycle.AppendExchangeAsync(RootId, conversation, 0,
            Result<string>.Success("hello"), errors.Add);
        Assert.Equal(2, store.Appended.Count);      // second attempt still made
        Assert.Equal(2, errors.Count);              // both failures reported
    }

    [Fact]
    public async Task Complete_Marks_Row_Completed_Preserving_Other_Fields()
    {
        var root = AgentRecord.Root(RootId, DateTimeOffset.UtcNow);
        var store = new FakeAgentStore { Current = root, GetOutcome = Result<AgentRecord?>.Success(root) };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.CompleteAsync(RootId, errors.Add);
        Assert.Empty(errors);
        Assert.Equal(AgentStatus.Completed, store.Current!.Status);
        Assert.NotNull(store.Current.CompletedAt);
        Assert.Equal(root.StartedAt, store.Current.StartedAt);
    }

    [Fact]
    public async Task Complete_When_Get_Fails_Reports_Error()
    {
        var store = new FakeAgentStore { GetOutcome = Result<AgentRecord?>.Failure(new Error("Db", "down")) };
        var lifecycle = new RootSessionLifecycle(store);
        var errors = new List<string>();
        await lifecycle.CompleteAsync(AgentId.NewId(), errors.Add);
        Assert.Single(errors);
    }
}
```

If `AgentRecord` property names differ (e.g. no `StartedAt`), assert on whatever fields exist to prove preservation-by-reconstruction; adjust, don't skip.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Composition.Tests --filter RootSessionLifecycleTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement (verbatim semantics from Program.cs)**

```csharp
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Persists the root session around a turn loop: appends one completed exchange
///     (user then final assistant — the same Message instances the aggregate holds) and marks
///     the row Completed on graceful exit. Persistence failures surface via reportError;
///     the session continues. Semantics lifted verbatim from the CLI's Program helpers.</summary>
public sealed class RootSessionLifecycle(IAgentStore store)
{
    public async Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
        int messageCountBefore, Result<string> result, Action<string> reportError)
    {
        if (!result.IsSuccess) return;

        var user = await store.AppendMessageAsync(rootId, conversation.Messages[messageCountBefore]);
        if (!user.IsSuccess)
            reportError($"Error [{user.Error!.Code}]: {user.Error.Message}");

        var assistant = await store.AppendMessageAsync(rootId, conversation.Messages[^1]);
        if (!assistant.IsSuccess)
            reportError($"Error [{assistant.Error!.Code}]: {assistant.Error.Message}");
    }

    public async Task CompleteAsync(AgentId rootId, Action<string> reportError)
    {
        var record = await store.GetAsync(rootId);
        if (!record.IsSuccess || record.Value is null)
        {
            reportError(record.IsSuccess
                ? $"Error [NotFound]: root session {rootId} was not found."
                : $"Error [{record.Error!.Code}]: {record.Error.Message}");
            return;
        }

        var updated = await store.UpdateAsync(record.Value with
        {
            Status = AgentStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        if (!updated.IsSuccess)
            reportError($"Error [{updated.Error!.Code}]: {updated.Error.Message}");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Composition.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: extract RootSessionLifecycle shared by all frontends"
```

---

### Task 7: AddEThangAgentCore — the big wiring move + drift-guard test

**Files:**

- Create: `src/eThangAgent.Composition/AgentComposition.cs`
- Modify: `src/eThangAgent.CLI/Program.cs` — `Main` becomes thin; REPL loops and `StreamEvent`/`DrainStream` presentation helpers stay
- Modify: `src/eThangAgent.CLI/eThangAgent.CLI.csproj` — add `<ProjectReference Include="../eThangAgent.Composition/eThangAgent.Composition.csproj" />`
- Test: `tests/eThangAgent.Composition.Tests/CompositionGuardTests.cs`

**Interfaces:**

- Consumes: everything from Tasks 2–6.
- Produces:

```csharp
public static IServiceCollection AddEThangAgentCore(
    this IServiceCollection services, AgentSettings settings, string apiKey,
    ModelConfig defaultModel, AgentHostOptions host);
public static ServiceProvider BuildServiceProvider(this IServiceCollection services); // using Microsoft.Extensions.DependencyInjection.ServiceProvider extension on IServiceCollection
```

- [ ] **Step 1: Write the failing drift-guard test**

```csharp
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Infrastructure;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.MemoryDomain;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

public class CompositionGuardTests
{
    public static TheoryData<string, AgentHostOptions> BothHostShapes => new()
    {
        { "terminal-shaped", new AgentHostOptions(
            new StubClarifyChannel(),
            new FixedWorkspaceContext(Path.GetFullPath(".")),
            new WorkspacePathResolver(Path.GetFullPath("."))) },
        { "desktop-shaped", new AgentHostOptions(
            new StubClarifyChannel(),
            new FixedWorkspaceContext("app"),
            new UnrootedPathResolver()) },
    };

    [Theory]
    [MemberData(nameof(BothHostShapes))]
    public void Core_Graph_Resolves_Every_Service_For_Every_Host(string label, AgentHostOptions host)
    {
        var settings = new AgentSettings("sk-or-test", new Uri("https://openrouter.test"),
            new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2), MaxToolIterationsConfiguration.Default);
        using var services = new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey!,
                ModelConfig.Create("test/model", 512, 0.5f).Value!, host)
            .BuildServiceProvider();

        object?[] resolutions =
        [
            services.GetRequiredService<Ag>(),
            services.GetRequiredService<SendMessageCommandHandler>(),
            services.GetRequiredService<Conversation>(),
            services.GetRequiredService<IConversationRepository>(),
            services.GetRequiredService<IFileSystemAccess>(),
            services.GetRequiredService<IFileWriteAccess>(),
            services.GetRequiredService<IFileEditAccess>(),
            services.GetRequiredService<ISearchAccess>(),
            services.GetRequiredService<IGitQueryAccess>(),
            services.GetRequiredService<IGitCommitAccess>(),
            services.GetRequiredService<IExecEngine>(),
            services.GetRequiredService<IToolRegistry>(),
            services.GetRequiredService<ITool>(),
            services.GetRequiredService<ICapabilityRegistry>(),
            services.GetRequiredService<IStateService>(),
            services.GetRequiredService<IStateStore>(),
            services.GetRequiredService<IAgentStore>(),
            services.GetRequiredService<AppDatabase>(),
            services.GetRequiredService<ISkillCatalog>(),
            services.GetRequiredService<ILearnedSkillStore>(),
            services.GetRequiredService<ICuratedMemoryStore>(),
            services.GetRequiredService<IClarifyChannel>(),
            services.GetRequiredService<IWorkspaceContext>(),
            services.GetRequiredService<IPathResolver>(),
            services.GetRequiredService<IModelProvider>(),
            services.GetRequiredService<IModelProviderFactory>(),
            services.GetRequiredService<IAgentRuntime>(),
            services.GetRequiredService<IAgentSpawnCommand>(),
            services.GetRequiredService<IMemoryRecallQuery>(),
            services.GetRequiredService<ISystemPromptProvider>(),
            services.GetRequiredService<SubAgentSpawner>(),
            services.GetRequiredService<RootSessionLifecycle>(),
            services.GetRequiredService<ModelConfig>(),
        ];
        Assert.All(resolutions, r => Assert.NotNull(r));
    }

    sealed class StubClarifyChannel : IClarifyChannel
    {
        public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("1"));
    }
}
```

If a service interface/type listed above resolves under a different concrete name than this solution uses today, correct the test to the actual type — the guard's value is completeness, not these exact spellings.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/eThangAgent.Composition.Tests --filter CompositionGuardTests`
Expected: FAIL — `AddEThangAgentCore` does not exist.

- [ ] **Step 3: Implement AgentComposition (move ALL host-agnostic registrations verbatim from Program.cs)**

```csharp
using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Memory;
using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.MemoryDomain;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

public static class AgentComposition
{
    /// <summary>Registers every host-agnostic piece of the agent: OpenRouter and platform
    ///     ACLs, the agent loop, capability registry, stores, nudge policy, system prompts,
    ///     and session lifecycle. Frontends supply exactly three decisions via AgentHostOptions.
    ///     Registration order and lifetimes mirror the CLI composition root this replaces.</summary>
    public static IServiceCollection AddEThangAgentCore(this IServiceCollection services,
        AgentSettings settings, string apiKey, ModelConfig defaultModel, AgentHostOptions host)
    {
        return services
            .AddSingleton(new OpenRouterConfiguration(apiKey, settings.BaseUrl))
            .AddHttpClient("OpenRouter", client => { client.Timeout = TimeSpan.FromSeconds(120); })
            .Services
            .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services
            .AddSingleton(defaultModel)
            .AddSingleton<Conversation>()
            .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
            .AddSingleton<DirectFileSystemAccess>()
            .AddSingleton<IFileSystemAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<IFileWriteAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<IFileEditAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<ISearchAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<DirectGitAccess>()
            .AddSingleton<IGitQueryAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
            .AddSingleton<IGitCommitAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
            .AddSingleton(ExecOptions.Default)
            .AddSingleton<IExecOutputStore>(_ => new ExecArtifactStore())
            .AddSingleton<IExecActivitySink>(_ => NullExecActivitySink.Instance)
            .AddSingleton(sp => new AgentToolsProvider("agent",
            [
                new AgentToolBinding(
                    new ReadTool(sp.GetRequiredService<IFileSystemAccess>()),
                    "Read lines from a text file."),
                new AgentToolBinding(
                    new WriteTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileWriteAccess>()),
                    "Create or overwrite a workspace file."),
                new AgentToolBinding(
                    new EditTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileEditAccess>()),
                    "Edit a file by exact literal replacement."),
                new AgentToolBinding(
                    new SearchTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<ISearchAccess>()),
                    "Search workspace text files with literal or regex patterns."),
                new AgentToolBinding(
                    new SkillListTool(sp.GetRequiredService<ISkillCatalog>(),
                        sp.GetRequiredService<ILearnedSkillStore>()),
                    "List available skills."),
                new AgentToolBinding(
                    new SkillViewTool(sp.GetRequiredService<ISkillCatalog>(),
                        sp.GetRequiredService<ILearnedSkillStore>()),
                    "Load a skill's full content by name."),
                new AgentToolBinding(
                    new SkillManageTool(sp.GetRequiredService<ISkillCatalog>(),
                        sp.GetRequiredService<ILearnedSkillStore>(),
                        sp.GetRequiredService<Func<DateTimeOffset>>()),
                    "Create, update, or delete learned skills."),
                new AgentToolBinding(
                    new ClarifyTool(sp.GetRequiredService<IClarifyChannel>()),
                    "Ask the human a clarifying question with structured options."),
                new AgentToolBinding(
                    new TodoTool(new StateServiceTodoListStore(sp.GetRequiredService<IStateService>())),
                    "Track a workspace task list."),
                new AgentToolBinding(
                    new GitStatusTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IGitQueryAccess>()),
                    "Show branch and working-tree status."),
                new AgentToolBinding(
                    new WorkingDiffTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IGitQueryAccess>()),
                    "Show staged/unstaged/all working-tree diff, bounded."),
                new AgentToolBinding(
                    new GitCommitTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IGitCommitAccess>()),
                    "Commit the current index with a validated conventional or gitmoji message."),
            ]))
            .AddSingleton(host.WorkspaceContext)
            .AddSingleton(host.PathResolver)
            .AddSingleton(host.ClarifyChannel)
            .AddSingleton<AppDatabase>()
            .AddSingleton<IStateStore, SqliteStateStore>()
            .AddSingleton<IAgentStore, SqliteAgentStore>()
            .AddSingleton<ISkillCatalog, EmbeddedSkillCatalog>()
            .AddSingleton<ILearnedSkillStore, SqliteLearnedSkillStore>()
            .AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow)
            .AddSingleton<SqliteCuratedMemoryStore>()
            .AddSingleton<ICuratedMemoryStore>(sp => sp.GetRequiredService<SqliteCuratedMemoryStore>())
            .AddSingleton<SessionMemoryWriteCounter>()
            .AddSingleton<INudgePolicy>(_ => new DefaultNudgePolicy(() => DateTimeOffset.UtcNow))
            .AddSingleton(sp => new OpenRouterModelProviderFactory(
                sp.GetRequiredService<OpenRouterConfiguration>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")))
            .AddSingleton<SubAgentSpawner>()
            .AddSingleton<IAgentRuntime>(sp => new InProcessAgentRuntime(
                sp.GetRequiredService<SubAgentSpawner>(),
                sp.GetRequiredService<IAgentStore>(),
                settings.SubAgents.MaxConcurrentAgents))
            .AddSingleton<IAgentSpawnCommand, StartSpawnHandler>()
            .AddSingleton<IAgentQueries, AgentQueries>()
            .AddSingleton<IMemoryRecallQuery, RecallQueryHandler>()
            .AddSingleton<IMemorySessionsQuery, SessionsQueryHandler>()
            .AddSingleton<AgentCapabilityProvider>(sp =>
            {
                var rootRecord = AgentRecord.Spawned(AgentId.NewId(), null, 0,
                    sp.GetRequiredService<ModelConfig>().ModelId, null,
                    "root session", DateTimeOffset.UtcNow);
                return new AgentCapabilityProvider(
                    sp.GetRequiredService<IAgentSpawnCommand>(),
                    sp.GetRequiredService<IAgentQueries>(),
                    () => SubAgentSpawner.RunningChild ?? rootRecord);
            })
            .AddSingleton<EvidenceOptions>(_ => EvidenceOptions.Default)
            .AddSingleton<IEvidenceRunner, CSharpEvidenceRunner>()
            .AddSingleton<IStateService, StateService>()
            .AddSingleton<StateCapabilityProvider>()
            .AddSingleton<MemoryCapabilityProvider>()
            .AddSingleton<ICapabilityRegistry>(sp =>
                CapabilityRegistry.Create(
                [
                    new MergedCapabilityProvider("agent",
                    [
                        sp.GetRequiredService<AgentToolsProvider>(),
                        sp.GetRequiredService<AgentCapabilityProvider>(),
                    ]),
                    sp.GetRequiredService<StateCapabilityProvider>(),
                    sp.GetRequiredService<MemoryCapabilityProvider>(),
                    new CuratedMemoryCapabilityProvider(
                        sp.GetRequiredService<ICuratedMemoryStore>(),
                        () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
                        () => SubAgentSpawner.RunningChild?.Id.ToString(),
                        sp.GetRequiredService<SessionMemoryWriteCounter>().Increment,
                        () => DateTimeOffset.UtcNow),
                ]))
            .AddSingleton<IExecEngine>(sp => new CSharpScriptExecEngine(
                new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>()),
                sp.GetRequiredService<ExecOptions>()))
            .AddSingleton<ITool>(sp => new ExecTool(
                sp.GetRequiredService<IExecEngine>(),
                sp.GetRequiredService<ExecOptions>(),
                sp.GetRequiredService<IExecOutputStore>(),
                sp.GetRequiredService<IExecActivitySink>()))
            .AddSingleton<IToolRegistry>(sp =>
                new ToolRegistry([sp.GetRequiredService<ITool>()]))
            .AddSingleton<ISystemPromptProvider>(sp => new CompositeSystemPromptProvider(
            [
                new SuperpowersBootstrapPromptProvider(sp.GetRequiredService<ISkillCatalog>()),
                new StaticPromptProvider(
                    "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
                    "workspace, prefer the provided tools over guessing, and keep responses tight."),
                new ExecGuidePromptProvider(
                    new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>())),
                new CuratedMemoryGuidePromptProvider(),
            ]))
            .AddSingleton(subAgents(settings))
            .AddSingleton<Ag>(sp =>
            {
                var provider = sp.GetRequiredService<IModelProvider>();
                var conversation = sp.GetRequiredService<Conversation>();
                var config = sp.GetRequiredService<ModelConfig>();
                var tools = sp.GetRequiredService<IToolRegistry>();
                return new Ag(provider, conversation, config, tools,
                    sp.GetRequiredService<ISystemPromptProvider>(), settings.MaxToolIterations);
            })
            .AddSingleton(sp => new SendMessageCommandHandler(
                sp.GetRequiredService<Ag>(),
                sp.GetRequiredService<Conversation>(),
                sp.GetRequiredService<INudgePolicy>(),
                () => sp.GetRequiredService<SessionMemoryWriteCounter>().Count))
            .AddSingleton<RootSessionLifecycle>()
            ;
    }

    private static SubAgentOptions subAgents(AgentSettings settings) => settings.SubAgents;
}
```

Notes for the implementer: `StateServiceTodoListStore` stays in the CLI today — if it has no CLI dependencies, move it to Composition in this task (preferred); otherwise leave it registered by each host right after `AddEThangAgentCore` (CLI) / in Desktop (Task 12) and remove its registration from the core listing accordingly. Where a moved registration referenced `subAgentOptions`, use `settings.SubAgents`; where it captured `maxToolIterations`, use `settings.MaxToolIterations`.

- [ ] **Step 4: Slim down Program.Main**

Replace the body of `Program.Main` up to (but not including) the REPL-mode branch with:

```csharp
var settings = AgentConfiguration.Load();
var apiKey = settings.ApiKey
    ?? throw new InvalidOperationException(
        "OPENROUTER_API_KEY environment variable not set. " +
        "Get a key at https://openrouter.ai/keys");

using var services = new ServiceCollection()
    .AddEThangAgentCore(settings, apiKey,
        ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!,
        new AgentHostOptions(
            Console.IsInputRedirected
                ? new PipedClarifyChannel(Console.In)
                : new InteractiveClarifyChannel(new AnsiTerminal(), new AnsiTerminal()),
            new CwdWorkspaceContext(),
            new WorkspacePathResolver(Path.GetFullPath('.'))))
    .BuildServiceProvider();

var handler = services.GetRequiredService<SendMessageCommandHandler>();
var modelConfig = services.GetRequiredService<ModelConfig>();

// Root session bootstrap: identical to before.
var store = services.GetRequiredService<IAgentStore>();
var conversation = services.GetRequiredService<Conversation>();
var rootId = AgentId.NewId();
var rootSaved = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow));
if (!rootSaved.IsSuccess)
    throw new InvalidOperationException(
        "failed to persist root session: " +
        $"[{rootSaved.Error!.Code}] {rootSaved.Error.Message}");
```

Delete from `Program.cs` every registration line this replaced and the now-unused usings (`OpenRouter.ACL`, `FileSystem.ACL`, `Storage.ACL`, `Roslyn.ACL`, `CapabilityDomain`, `MemoryDomain`, `StateDomain`, most of `ToolDomain` usage remains only if still referenced). `AppendExchangeAsync`/`CompleteRootSessionAsync` static helpers switch to `services.GetRequiredService<RootSessionLifecycle>()` calls with `Console.Error.WriteLine` as `reportError`. REPL loops, `StreamEvent`, `DrainStream`, and `CliCommands` remain untouched.

- [ ] **Step 5: Full solution verification**

Run: `dotnet build && dotnet test`
Expected: entire solution green — including all pre-existing CLI/E2E tests asserting unchanged piped behavior, and the drift guard passing for both host shapes.

- [ ] **Step 6: Manual smoke of interactive CLI**

Run (with a real key): `$env:OPENROUTER_API_KEY='sk-or-…'; dotnet run --project src/eThangAgent.CLI`
Send one short message; confirm streamed reply, `/help`, and clean `/exit`. This is behavioral proof the refactor changed nothing.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "refactor: extract shared agent composition; CLI becomes thin host"
```

---

### Task 8: Transcript model + TranscriptViewModel (TDD, no Avalonia types)

**Files:**

- Create: `src/eThangAgent.Desktop/ViewModels/TranscriptEntry.cs`
- Create: `src/eThangAgent.Desktop/ViewModels/TranscriptViewModel.cs`
- Test: `tests/eThangAgent.Desktop.Tests/TranscriptViewModelTests.cs`

**Interfaces:**

- Produces:

```csharp
public abstract record TranscriptEntry
{
    public sealed record UserMessage(string Text) : TranscriptEntry;
    public sealed record AssistantText(string Text) : TranscriptEntry;
    public sealed record Reasoning(string Text) : TranscriptEntry;
    public sealed record ToolCall(string Name, string Arguments) : TranscriptEntry;
    public sealed record ToolResult(string Name, string Summary) : TranscriptEntry;
    public sealed record Notice(string Text) : TranscriptEntry;
}
public sealed class TranscriptViewModel
{
    public System.Collections.ObjectModel.ObservableCollection<TranscriptEntry> Entries { get; }
    public void AddUser(string text);
    public void AppendAssistantDelta(string text);   // opens/extends current assistant block
    public void EndIteration();                       // closes open stream/reasoning blocks
    public void AppendReasoning(string text);         // opens/extends current reasoning block
    public void AddToolCall(string name, string arguments);
    public void AddToolResult(string name, string summary);
    public void AddNotice(string text);
}
```

- [ ] **Step 1: Write failing tests**

```csharp
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class TranscriptViewModelTests
{
    [Fact]
    public void First_Delta_Opens_Assistant_Block_Second_Extends_It()
    {
        var vm = new TranscriptViewModel();
        vm.AppendAssistantDelta("Hel");
        vm.AppendAssistantDelta("lo");
        var entry = Assert.IsType<TranscriptEntry.AssistantText>(vm.Entries[^1]);
        Assert.Equal("Hello", entry.Text);
        Assert.Equal(1, vm.Entries.Count);
    }

    [Fact]
    public void Iteration_End_Closes_Block_Next_Delta_Starts_New_Entry()
    {
        var vm = new TranscriptViewModel();
        vm.AppendAssistantDelta("one");
        vm.EndIteration();
        vm.AppendAssistantDelta("two");
        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal("two", Assert.IsType<TranscriptEntry.AssistantText>(vm.Entries[^1]).Text);
    }

    [Fact]
    public void Reasoning_Blocks_Open_Extend_And_Close_Like_Assistant()
    {
        var vm = new TranscriptViewModel();
        vm.AppendReasoning("think");
        vm.AppendReasoning("ing");
        Assert.Equal("thinking", Assert.IsType<TranscriptEntry.Reasoning>(vm.Entries[^1]).Text);
        vm.EndIteration();
        vm.AppendReasoning("more");
        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void Non_Stream_Events_Close_Open_Blocks_First()
    {
        var vm = new TranscriptViewModel();
        vm.AppendAssistantDelta("partial");
        vm.AddToolCall("read", "{\"path\":\"a.cs\"}");
        vm.AddToolResult("read", "12 lines");
        vm.AppendAssistantDelta("done");
        Assert.Equal(4, vm.Entries.Count);
        Assert.IsType<TranscriptEntry.ToolCall>(vm.Entries[1]);
        Assert.IsType<TranscriptEntry.ToolResult>(vm.Entries[2]);
    }

    [Fact]
    public void User_Message_And_Notice_Render_As_Their_Own_Entries()
    {
        var vm = new TranscriptViewModel();
        vm.AddUser("hi");
        vm.AddNotice("Commands:/help");
        Assert.IsType<TranscriptEntry.UserMessage>(vm.Entries[0]);
        Assert.IsType<TranscriptEntry.Notice>(vm.Entries[1]);
    }

    [Fact]
    public void Extending_An_Entry_Raises_Collection_Change_Replace()
    {
        var vm = new TranscriptViewModel();
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);
        vm.AppendAssistantDelta("a");
        vm.AppendAssistantDelta("b");
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Add, changes);
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Replace, changes);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter TranscriptViewModelTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`TranscriptEntry.cs`: the record hierarchy exactly as specified in Interfaces.

`TranscriptViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Holds rendered transcript entries and applies stream events with the same
///     semantics as the terminal DrainStream: deltas extend the open block; iteration end
///     (or any non-stream event) closes it so the next delta opens a fresh entry. All methods
///     run on the UI thread — callers marshal (Task 9 bridge).</summary>
public sealed class TranscriptViewModel
{
    private readonly ObservableCollection<TranscriptEntry> _entries = [];
    private int _openIndex = -1; // index of the extendable Assistant/Reasoning entry, else -1

    public ObservableCollection<TranscriptEntry> Entries => _entries;

    public void AddUser(string text) { CloseOpen(); _entries.Add(new TranscriptEntry.UserMessage(text)); }
    public void AddToolCall(string name, string arguments) { CloseOpen(); _entries.Add(new TranscriptEntry.ToolCall(name, arguments)); }
    public void AddToolResult(string name, string summary) { CloseOpen(); _entries.Add(new TranscriptEntry.ToolResult(name, summary)); }
    public void AddNotice(string text) { CloseOpen(); _entries.Add(new TranscriptEntry.Notice(text)); }

    public void EndIteration() => CloseOpen();

    public void AppendAssistantDelta(string text) => Extend(text, open => new TranscriptEntry.AssistantText(open.Text + text));
    public void AppendReasoning(string text) => Extend(text, open => new TranscriptEntry.Reasoning(open.Text + text));

    private void Extend<T>(string _, Func<T, TranscriptEntry> rebuild) where T : TranscriptEntry
    {
        if (_openIndex >= 0 && _entries[_openIndex] is T open)
        {
            _entries[_openIndex] = rebuild(open); // Replace notification drives re-render
            return;
        }
        CloseOpen();
        _entries.Add(rebuild((T)(TranscriptEntry)(text.Length >= 0
            ? Activator.CreateInstance(typeof(T), "")! )) ); // replaced below by explicit branches
        _openIndex = _entries.Count - 1;
    }

    private void CloseOpen() => _openIndex = -1;
}
```

The generic `Extend` above is intentionally rejected — records are immutable and the generic construction is convoluted. Final shape (use this): two explicit methods, no generics:

```csharp
public void AppendAssistantDelta(string text)
{
    if (_openIndex >= 0 && _entries[_openIndex] is TranscriptEntry.AssistantText open)
        _entries[_openIndex] = open with { Text = open.Text + text };
    else { CloseOpen(); _entries.Add(new TranscriptEntry.AssistantText(text)); _openIndex = _entries.Count - 1; }
}

public void AppendReasoning(string text)
{
    if (_openIndex >= 0 && _entries[_openIndex] is TranscriptEntry.Reasoning open)
        _entries[_openIndex] = open with { Text = open.Text + text };
    else { CloseOpen(); _entries.Add(new TranscriptEntry.Reasoning(text)); _openIndex = _entries.Count - 1; }
}
```

(`with` requires the records to declare `{ get; init; }` properties — write them as positional records with `init`, which C# positional records already provide.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter TranscriptViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: desktop transcript view-model mirroring terminal drain semantics"
```

---

### Task 9: StreamBridge — callbacks to UI-thread events over a Channel (TDD)

**Files:**

- Create: `src/eThangAgent.Desktop/Streaming/UiStreamEvent.cs`
- Create: `src/eThangAgent.Desktop/Streaming/StreamBridge.cs`
- Test: `tests/eThangAgent.Desktop.Tests/StreamBridgeTests.cs`

**Interfaces:**

- Produces:

```csharp
public abstract record UiStreamEvent
{
    public sealed record Delta(string Text) : UiStreamEvent;
    public sealed record Reasoning(string Text) : UiStreamEvent;
    public sealed record IterationEnd() : UiStreamEvent;
    public sealed record ToolCallEvent(string Name, string Arguments) : UiStreamEvent;
    public sealed record ToolResultEvent(string Name, string Summary) : UiStreamEvent;
}
public sealed class StreamBridge
{
    public StreamBridge(Action<UiStreamEvent> sink);            // sink invoked on pump thread (tests: recorder; prod: Dispatcher post)
    public Action<string> OnContentDelta { get; }               // wire into Handle(...)
    public Action<string> OnReasoningDelta { get; }
    public Action OnIterationEnd { get; }
    public Action<string, string> OnToolCall { get; }
    public Action<string, string> OnToolResult { get; }
    public Task DrainUntilIdleAsync(TimeSpan? pollInterval = null); // completes after queue empties AND turn marked done
    public void MarkTurnComplete();                             // called when the Handle task resolves
}
```

Pump semantics: single reader drains in order; after `MarkTurnComplete()` and queue exhaustion, remaining buffered events flush then pumping stops. `DrainUntilIdleAsync` gives deterministic test synchronization without sleeps.

- [ ] **Step 1: Write failing tests**

```csharp
using System.Threading.Channels;
using eThangAgent.Desktop.Streaming;

namespace eThangAgent.Desktop.Tests;

public class StreamBridgeTests
{
    [Fact]
    public async Task Events_Are_Delivered_In_Publication_Order_Exactly_Once()
    {
        var received = new List<UiStreamEvent>();
        var bridge = new StreamBridge(e => received.Add(e));
        bridge.OnContentDelta("a");
        bridge.OnReasoningDelta("r");
        bridge.OnIterationEnd();
        bridge.OnContentDelta("b");
        bridge.OnToolCall("read", "{}");
        bridge.OnToolResult("read", "ok");
        bridge.MarkTurnComplete();
        await bridge.DrainUntilIdleAsync();

        Assert.Equal(6, received.Count);
        Assert.IsType<UiStreamEvent.Delta>(received[0]);
        Assert.IsType<UiStreamEvent.Reasoning>(received[1]);
        Assert.IsType<UiStreamEvent.IterationEnd>(received[2]);
        Assert.IsType<UiStreamEvent.Delta>(received[3]);
        Assert.IsType<UiStreamEvent.ToolCallEvent>(received[4]);
        Assert.IsType<UiStreamEvent.ToolResultEvent>(received[5]);
    }

    [Fact]
    public async Task Events_Published_From_Many_Threads_All_Arrive()
    {
        var received = new List<UiStreamEvent>();
        var bridge = new StreamBridge(e => received.Add(e));
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++) bridge.OnContentDelta(i + ":" + j);
        })).ToArray();
        await Task.WhenAll(tasks);
        bridge.MarkTurnComplete();
        await bridge.DrainUntilIdleAsync();
        Assert.Equal(400, received.Count);
        Assert.Equal(400, received.Select(e => ((UiStreamEvent.Delta)e).Text).Distinct().Count());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter StreamBridgeTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`UiStreamEvent.cs`: record hierarchy exactly as in Interfaces.

`StreamBridge.cs`:

```csharp
using System.Threading.Channels;

namespace eThangAgent.Desktop.Streaming;

/// <summary>Bridges agent-loop stream callbacks (arbitrary threads) to a UI sink. Callbacks
///     only write to an unbounded channel; a single reader pumps events to the sink in order.
///     Event-driven — no polling timer. The channel lives for one turn; MarkTurnComplete ends it.</summary>
public sealed class StreamBridge(Action<UiStreamEvent> sink)
{
    private readonly Channel<UiStreamEvent> _channel =
        Channel.CreateUnbounded<UiStreamEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StreamBridgePump? _pump;

    public Action<string> OnContentDelta => text => _channel.Writer.TryWrite(new UiStreamEvent.Delta(text));
    public Action<string> OnReasoningDelta => text => _channel.Writer.TryWrite(new UiStreamEvent.Reasoning(text));
    public Action OnIterationEnd => () => _channel.Writer.TryWrite(new UiStreamEvent.IterationEnd());
    public Action<string, string> OnToolCall => (name, args) => _channel.Writer.TryWrite(new UiStreamEvent.ToolCallEvent(name, args));
    public Action<string, string> OnToolResult => (name, summary) => _channel.Writer.TryWrite(new UiStreamEvent.ToolResultEvent(name, summary));

    public void Start()
    {
        _pump = new StreamBridgePump(_channel.Reader, sink, _drained);
        _ = Task.Run(_pump.RunAsync);
    }

    public void MarkTurnComplete() => _channel.Writer.TryComplete();

    public Task DrainUntilIdleAsync(TimeSpan? pollInterval = null) => _drained.Task;
}

internal sealed class StreamBridgePump(
    ChannelReader<UiStreamEvent> reader, Action<UiStreamEvent> sink, TaskCompletionSource drained)
{
    public async Task RunAsync()
    {
        await foreach (var evt in reader.ReadAllAsync()) sink(evt);
        drained.TrySetResult();
    }
}
```

Update the tests to call `bridge.Start()` immediately after constructing (add the call in both test bodies). Production calls `Start()` when the turn begins and `MarkTurnComplete()` when the Handle task resolves.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter StreamBridgeTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: channel-based stream bridge from agent callbacks to UI thread"
```

---

### Task 10: MainViewModel — command routing, turn orchestration, bookkeeping (TDD)

**Files:**

- Create: `src/eThangAgent.Desktop/ViewModels/Commands.cs`
- Create: `src/eThangAgent.Desktop/ViewModels/MainViewModel.cs`
- Test: `tests/eThangAgent.Desktop.Tests/MainViewModelTests.cs`

**Interfaces:**

- Consumes: `TurnRunner` delegate matching `SendMessageCommandHandler.Handle` exactly:

```csharp
public delegate Task<Result<string>> TurnRunner(SendMessageCommand command, CancellationToken ct,
    Action<string>? onContentDelta, Action<string>? onReasoningDelta, Action? onIterationEnd,
    Action<string, string>? onToolCall, Action<string, string>? onToolResult);
```

- Produces:

```csharp
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(TurnRunner runner, RootSessionLifecycle lifecycle, AgentId rootId,
        Conversation conversation, string modelId, Action requestClose);
    public TranscriptViewModel Transcript { get; }
    public StatusViewModel Status { get; }              // Task 13 provides it; stub here via minimal local class if ordered earlier
    public ClarifyViewModel? Clarify { get; }           // null unless a question is pending (wired in Task 11)
    public bool IsBusy { get; }                         // input disabled while true
    public int MessageCount { get; }                    // messages sent this session
    public System.Windows.Input.ICommand SubmitCommand { get; } // bound to input box Enter
    public Task SubmitAsync(string input);
}
public static class DesktopCommands
{
    public static IReadOnlyList<(string Name, string Description)> All { get; } // /exit,/help,/quit — descriptions identical to CliCommands
    public static bool IsQuit(string input);
    public static bool IsHelp(string input);
}
```

Behavior contract (each clause covered by a test below):

1. `/help` → notice listing all commands; not sent to model; message count unchanged.
2. `/exit`, `/quit` → invoke `requestClose`; nothing sent to model.
3. Any other non-empty input → user entry appended, `IsBusy` true during turn, stream callbacks flow into `Transcript` via a `StreamBridge`, on resolution `lifecycle.AppendExchangeAsync` called with the message-count snapshot, `IsBusy` false, success with zero streamed deltas → final text as a notice (non-streaming fallback parity), failure → `Error [code]: message` notice.
4. Busy submissions ignored (parity with modal editor.Read).
5. Persistence-error strings route through `reportError` → notice entries.
6. Blank input ignored.

- [ ] **Step 1: Write failing tests**

```csharp
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

public sealed class RecordingLifecycle(IAgentStore store) : RootSessionLifecycle(store)
{
    public int Exchanges; public int Completions;
    public new async Task AppendExchangeAsync(AgentId rootId, Conversation c, int before,
        Result<string> result, Action<string> err) { Exchanges++; await base.AppendExchangeAsync(rootId, c, before, result, err); }
}

public class MainViewModelTests
{
    private static (MainViewModel Vm, List<string> Errors, Func<Task> Pump) Build(
        TurnRunner runner, out RecordingLifecycle lifecycle)
    {
        var store = new StubStore();
        lifecycle = new RecordingLifecycle(store);
        var errors = new List<string>();
        var closed = false;
        var vm = new MainViewModel(runner, lifecycle, AgentId.NewId(), new Conversation(),
            "test/model", () => closed = true);
        return (vm, errors, () => Task.CompletedTask);
    }

    [Fact]
    public async Task Help_Prints_Command_List_Not_Sent_To_Model()
    {
        var sent = 0;
        var (vm, _, _) = Build((_, _, _, _, _, _, _) => { sent++; return Task.FromResult(Result<string>.Success("")); }, out _);
        await vm.SubmitAsync("/help");
        Assert.Equal(0, sent);
        Assert.False(vm.IsBusy);
        var notice = Assert.IsType<TranscriptEntry.Notice>(vm.Transcript.Entries[^1]);
        Assert.Contains("/help", notice.Text);
        Assert.Contains("/exit", notice.Text);
        Assert.Contains("/quit", notice.Text);
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/quit")]
    public async Task Quit_Commands_Request_Close_Without_Model_Call(string cmd)
    {
        var sent = 0;
        var closed = false;
        var store = new StubStore();
        var vm = new MainViewModel((_, _, _, _, _, _, _) => { sent++; return Task.FromResult(Result<string>.Success("")); },
            new RecordingLifecycle(store), AgentId.NewId(), new Conversation(), "m", () => closed = true);
        await vm.SubmitAsync(cmd);
        Assert.True(closed);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task Normal_Turn_Appends_User_Entries_Disables_Input_And_Books_Exchange()
    {
        var (vm, errors, pump) = Build(async (_, _, onContent, _, _, _, _) =>
        {
            onContent!("hel"); onContent!("lo");
            await Task.Yield();
            return Result<string>.Success("hello");
        }, out var lifecycle);
        await vm.SubmitAsync("hi");
        await vm.WaitForTurnAsync();
        Assert.IsType<TranscriptEntry.UserMessage>(vm.Transcript.Entries[0]);
        Assert.IsType<TranscriptEntry.AssistantText>(vm.Transcript.Entries[^1]);
        Assert.Equal("hello", Assert.IsType<TranscriptEntry.AssistantText>(vm.Transcript.Entries[^1]).Text);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, lifecycle.Exchanges);
        Assert.Equal(1, vm.MessageCount);
    }

    [Fact]
    public async Task Failure_Produces_Error_Notice_With_Code()
    {
        var (vm, _, _) = Build((_, _, _, _, _, _, _) =>
            Task.FromResult(Result<string>.Failure(new Error("RateLimited", "slow down"))), out _);
        await vm.SubmitAsync("go");
        await vm.WaitForTurnAsync();
        var notice = Assert.IsType<TranscriptEntry.Notice>(vm.Transcript.Entries[^1]);
        Assert.Contains("Error [RateLimited]: slow down", notice.Text);
    }

    [Fact]
    public async Task Success_Without_Streamed_Deltas_Falls_Back_To_Final_Text_Notice()
    {
        var (vm, _, _) = Build((_, _, _, _, _, _, _) => Task.FromResult(Result<string>.Success("plain answer")), out _);
        await vm.SubmitAsync("q");
        await vm.WaitForTurnAsync();
        var notice = Assert.IsType<TranscriptEntry.Notice>(vm.Transcript.Entries[^1]);
        Assert.Contains("plain answer", notice.Text);
    }

    [Fact]
    public async Task Submission_While_Busy_Is_Ignored()
    {
        var release = new TaskCompletionSource();
        var (vm, _, _) = Build((_, _, _, _, _, _, _) => release.Task.ContinueWith(_ => Result<string>.Success("done")), out _);
        var first = vm.SubmitAsync("one");
        Assert.True(vm.IsBusy);
        await vm.SubmitAsync("two");            // ignored — no second user entry
        release.SetResult();
        await first; await vm.WaitForTurnAsync();
        Assert.Equal(1, vm.Transcript.Entries.OfType<TranscriptEntry.UserMessage>().Count());
    }

    [Fact]
    public async Task Blank_Input_Is_Ignored()
    {
        var (vm, _, _) = Build((_, _, _, _, _, _, _) => Task.FromResult(Result<string>.Success("x")), out _);
        await vm.SubmitAsync("   ");
        Assert.Empty(vm.Transcript.Entries);
    }

    private sealed class StubStore : IAgentStore
    {
        public Task<Result<bool>> SaveAsync(AgentRecord record, CancellationToken ct = default) => Task.FromResult(Result<bool>.Success(true));
        public Task<Result<AgentRecord?>> GetAsync(AgentId id, CancellationToken ct = default) => Task.FromResult(Result<AgentRecord?>.Success((AgentRecord?)null));
        public Task<Result<bool>> UpdateAsync(AgentRecord record, CancellationToken ct = default) => Task.FromResult(Result<bool>.Success(true));
        public Task<Result<bool>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default) => Task.FromResult(Result<bool>.Success(true));
    }
}
```

Where the fake `TurnRunner` lambdas discard unused parameters, prefer named-with-underscore discards `(SendMessageCommand _, CancellationToken _, ...)` to keep arity explicit. If `RecordingLifecycle` shadowing proves awkward, subclass with virtual members instead — adjust `RootSessionLifecycle` methods to `virtual` in Task 6 if needed.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter MainViewModelTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement Commands.cs and MainViewModel.cs**

`Commands.cs`:

```csharp
namespace eThangAgent.Desktop.ViewModels;

public sealed record DesktopCommand(string Name, string Description);

/// <summary>Presentation commands for the desktop frontend — mirrors CliCommands semantics.</summary>
public static class DesktopCommands
{
    private static readonly string[] QuitNames = ["/exit", "/quit"];

    public static IReadOnlyList<DesktopCommand> All { get; } =
    [
        new("/exit", "Exit the agent"),
        new("/help", "Show the command list"),
        new("/quit", "Exit the agent (alias of /exit)"),
    ];

    public static bool IsQuit(string input) => QuitNames.Contains(input);
    public static bool IsHelp(string input) => input == "/help";
}
```

`MainViewModel.cs` (core logic; CommunityToolkit attributes for bindables):

```csharp
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

public delegate Task<Result<string>> TurnRunner(SendMessageCommand command, CancellationToken ct,
    Action<string>? onContentDelta, Action<string>? onReasoningDelta, Action? onIterationEnd,
    Action<string, string>? onToolCall, Action<string, string>? onToolResult);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly TurnRunner _runner;
    private readonly RootSessionLifecycle _lifecycle;
    private readonly AgentId _rootId;
    private readonly Conversation _conversation;
    private readonly Action _requestClose;
    private Task? _runningTurn;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCount))]
    private int _messages;

    public TranscriptViewModel Transcript { get; } = new();
    public StatusViewModel Status { get; }
    public ClarifyViewModel? Clarify { get; private set; }

    public int MessageCount => Messages;

    public ICommand SubmitCommand { get; }

    public MainViewModel(TurnRunner runner, RootSessionLifecycle lifecycle, AgentId rootId,
        Conversation conversation, string modelId, Action requestClose)
    {
        _runner = runner;
        _lifecycle = lifecycle;
        _rootId = rootId;
        _conversation = conversation;
        _requestClose = requestClose;
        Status = new StatusViewModel(modelId);
        SubmitCommand = new AsyncRelayCommand(() => SubmitAsync(Input), () => !IsBusy);
        Input = "";
    }

    [ObservableProperty]
    private string _input;

    public async Task SubmitAsync(string rawInput)
    {
        var input = rawInput.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;
        if (IsBusy) return;

        if (DesktopCommands.IsQuit(input)) { _requestClose(); return; }
        if (DesktopCommands.IsHelp(input))
        {
            Transcript.AddNotice("Commands:" + string.Join("",
                DesktopCommands.All.OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => $"\n  {c.Name}  —  {c.Description}")));
            return;
        }

        Messages++;
        Transcript.AddUser(input);
        Status.Phase = TurnPhase.Thinking;
        IsBusy = true;

        var bridge = new StreamBridge(ApplyStreamEvent);
        bridge.Start();
        var messageCountBefore = _conversation.Messages.Count;
        var sawStream = false;
        try
        {
            var result = await _runner(new SendMessageCommand(input), CancellationToken.None,
                onContentDelta: d => { sawStream = true; bridge.OnContentDelta(d); },
                onReasoningDelta: bridge.OnReasoningDelta,
                onIterationEnd: bridge.OnIterationEnd,
                onToolCall: bridge.OnToolCall,
                onToolResult: bridge.OnToolResult);

            await bridge.DrainUntilIdleAsync();
            await _lifecycle.AppendExchangeAsync(_rootId, _conversation, messageCountBefore,
                result, ReportPersistenceError);

            if (!result.IsSuccess || !sawStream)
                Transcript.AddNotice(result.IsSuccess
                    ? result.Value!
                    : $"Error [{result.Error!.Code}]: {result.Error.Message}");
        }
        finally
        {
            bridge.MarkTurnComplete();
            Status.Phase = TurnPhase.Ready;
            IsBusy = false;
            OnPropertyChanged(nameof(MessageCount));
        }
    }

    /// <summary>Awaits the in-flight turn in tests without polling.</summary>
    public Task WaitForTurnAsync() => _runningTurn ?? Task.CompletedTask;

    private void ApplyStreamEvent(UiStreamEvent evt)
    {
        switch (evt)
        {
            case UiStreamEvent.Delta d:
                Status.Phase = TurnPhase.Streaming;
                Transcript.AppendAssistantDelta(d.Text);
                break;
            case UiStreamEvent.Reasoning r:
                Transcript.AppendReasoning(r.Text);
                break;
            case UiStreamEvent.IterationEnd:
                Transcript.EndIteration();
                break;
            case UiStreamEvent.ToolCallEvent tc:
                Transcript.AddToolCall(tc.Name, tc.Arguments);
                break;
            case UiStreamEvent.ToolResultEvent tr:
                Transcript.AddToolResult(tr.Name, tr.Summary);
                break;
        }
    }

    private void ReportPersistenceError(string message) => Transcript.AddNotice(message);
}
```

Implementer notes: track `_runningTurn = Task.Run(...)`-style assignment if you choose to run the turn off the UI context; simplest correct form is assigning `_runningTurn = ExecuteTurnAsync(input, ...)` inside `SubmitAsync` and returning that task. `StatusViewModel`/`TurnPhase` arrive in Task 13 — create the minimal stub now (`enum TurnPhase { Ready, Thinking, Streaming }` plus a `StatusViewModel` holding `ModelId` and a settable `Phase`) so this compiles; Task 13 extends it with the animated indicator. `WaitForTurnAsync` must observe the actual turn task — make `_runningTurn` the awaited task from `SubmitAsync`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter MainViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: desktop main view-model with command routing and turn orchestration"
```

---

### Task 11: Clarify — ClarifyViewModel state machine + AvaloniaClarifyChannel (TDD)

**Files:**

- Create: `src/eThangAgent.Desktop/ViewModels/ClarifyViewModel.cs`
- Create: `src/eThangAgent.Desktop/AvaloniaClarifyChannel.cs`
- Modify: `src/eThangAgent.Desktop/ViewModels/MainViewModel.cs` — expose `PresentClarifyAsync` hook; while a question is pending, `SubmitAsync` routes typed input to the pending clarify answer instead of starting a turn
- Test: `tests/eThangAgent.Desktop.Tests/ClarifyTests.cs`

**Interfaces:**

- Consumes: `ClarifyQuestion(string Question, IReadOnlyList<string> Options, bool AllowFreeText)`, `IClarifyChannel`.
- Produces:

```csharp
public sealed partial class ClarifyViewModel : ObservableObject
{
    public ClarifyViewModel(ClarifyQuestion question);
    public string Question { get; }
    public IReadOnlyList<string> Options { get; }
    public bool AllowFreeText { get; }
    [ObservableProperty] private string _input = "";
    public Task<Result<string>> Completion { get; }        // exactly-once
    public void ChooseOption(int index);                   // 1-based display; validates range -> failure Result, NOT exception
    public void SubmitFreeText();                          // empty input -> failure Result ('answer required')
    public void Cancel();                                  // Error("Cancelled", ...) — same contract as Ctrl+C
}
public sealed class AvaloniaClarifyChannel(Func<ClarifyQuestion, Task<ClarifyViewModel>> present) : IClarifyChannel
{
    public async Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
        var vm = await present(question);
        return await vm.Completion;
    }
}
```

Cancellation semantics parity: `Cancelled` error code, message `"Cancelled by the user."`.

- [ ] **Step 1: Write failing tests**

```csharp
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

public class ClarifyTests
{
    private static ClarifyQuestion Sample(bool freeText = true) =>
        new("Which approach?", ["first", "second"], freeText);

    [Fact]
    public void Option_Selection_Completes_Once_With_Index()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.ChooseOption(1);
        Assert.Equal("1", vm.Completion.Result.Value);
        vm.Cancel(); vm.ChooseOption(2);            // double-complete guarded
        Assert.True(vm.Completion.Result.IsSuccess);
    }

    [Fact]
    public void Out_Of_Range_Option_Fails_Without_Exception()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.ChooseOption(5);
        Assert.False(vm.Completion.Result.IsSuccess);
        Assert.Equal("InvalidChoice", vm.Completion.Result.Error!.Code);
        vm.ChooseOption(1);                          // still completable after bad pick
        Assert.True(vm.Completion.Result.IsSuccess);
    }

    [Fact]
    public void Free_Text_Submits_Typed_Answer()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.Input = "neither, do this instead";
        vm.SubmitFreeText();
        Assert.Equal("neither, do this instead", vm.Completion.Result.Value);
    }

    [Fact]
    public void Empty_Free_Text_Fails_And_Stays_Pending()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.Input = "   ";
        vm.SubmitFreeText();
        Assert.False(vm.Completion.Result.IsSuccess);
        Assert.Equal("AnswerRequired", vm.Completion.Result.Error!.Code);
        vm.Input = "ok";
        vm.SubmitFreeText();
        Assert.Equal("ok", vm.Completion.Result.Value);
    }

    [Fact]
    public async Task Cancel_Matches_Terminal_Cancelled_Contract()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.Cancel();
        Assert.False(vm.Completion.Result.IsSuccess);
        Assert.Equal("Cancelled", vm.Completion.Result.Error!.Code);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Channel_Presents_Question_And_Returns_Answer()
    {
        ClarifyQuestion? presented = null;
        ClarifyViewModel? vm = null;
        var channel = new AvaloniaClarifyChannel(q =>
        {
            presented = q;
            vm = new ClarifyViewModel(q);
            return Task.FromResult(vm);
        });
        var ask = channel.AskAsync(Sample());
        vm!.ChooseOption(2);
        var result = await ask;
        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value);
        Assert.Equal("Which approach?", presented!.Question);
    }

    [Fact]
    public async Task MainViewModel_Routes_Input_To_Pending_Clarify()
    {
        // Full wiring: channel presents through MainViewModel; typed input answers.
        var questionGate = new TaskCompletionSource<ClarifyViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new AvaloniaClarifyChannel(q =>
        {
            var cvm = new ClarifyViewModel(q);
            questionGate.SetResult(cvm);
            return Task.FromResult(cvm);
        });
        var store = new MainViewModelTests.StubStore();
        var vm = new MainViewModel(
            (_, _, _, _, _, _, _) => Task.FromResult(Result<string>.Success("turn done")),
            new MainViewModelTests.RecordingLifecycleAdapter(store), AgentId.NewId(), new Conversation(),
            "m", () => { });
        vm.AttachClarifyChannel(channel);

        var turn = vm.SubmitAsync("ask me");       // model asks a clarify question mid-turn
        var clarify = await questionGate.Task;
        await vm.WaitForTurnAsync();
        Assert.NotNull(vm.Clarify);

        await vm.SubmitAsync("my free answer");     // routed to clarify, not a new turn
        var result = await turn;
        Assert.True(result.IsSuccess);
        Assert.Null(vm.Clarify);
        Assert.Equal(1, vm.MessageCount);           // clarify answer did not count as a message
        Assert.Contains("my free answer", vm.Transcript.Entries.ToString());
    }
}
```

The last test exercises the integration contract; adapt the `RecordingLifecycleAdapter`/`StubStore` reuse to whatever Task 10 actually named them (prefer extracting both into a shared `Desktop.Tests` fixtures file during Task 10 so tests here reference them directly).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter ClarifyTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`ClarifyViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Interactive clarify state for the desktop: numbered option buttons, optional
///     free-text field, cancel. Completion resolves exactly once; invalid interactions fail
///     with Result errors and keep the question pending — mirroring the terminal channel.</summary>
public sealed partial class ClarifyViewModel : ObservableObject
{
    private readonly TaskCompletionSource<Result<string>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _settled;

    public ClarifyViewModel(ClarifyQuestion question)
    {
        Question = question.Question;
        Options = question.Options;
        AllowFreeText = question.AllowFreeText;
    }

    public string Question { get; }
    public IReadOnlyList<string> Options { get; }
    public bool AllowFreeText { get; }

    [ObservableProperty]
    private string _input = "";

    public Task<Result<string>> Completion => _completion.Task;

    public void ChooseOption(int index)   // 1-based, as displayed
    {
        if (Interlocked.Exchange(ref _settled, 1) == 1) return;
        if (index < 1 || index > Options.Count)
        {
            _settled = 0;
            _completion.TrySetResult(Result<string>.Failure(new Error("InvalidChoice",
                $"Pick an option between 1 and {Options.Count}.")));
            // NOTE: a failure that leaves the question answerable cannot also be the final
            // completion — see Step 3 correction below.
            return;
        }
        _completion.TrySetResult(Result<string>.Success(index.ToString()));
    }

    public void SubmitFreeText()
    {
        var text = Input.Trim();
        if (text.Length == 0)
        {
            SetTransient(Result<string>.Failure(new Error("AnswerRequired", "Type an answer first.")));
            return;
        }
        Settle(Result<string>.Success(text));
    }

    public void Cancel() => Settle(Result<string>.Failure(new Error("Cancelled", "Cancelled by the user.")));

    private void Settle(Result<string> result)
    {
        if (Interlocked.Exchange(ref _settled, 1) == 1) return;
        _completion.TrySetResult(result);
    }
}
```

Correction required while implementing (the sketch above deliberately surfaces the issue): transient validation failures (out-of-range option, empty free text) must NOT consume the one-shot completion. Final shape: `Completion` is a property backed by a TCS that settles only on valid answer/cancel; transient failures surface through an observable `ValidationMessage` property the view displays, and the tests assert `vm.ValidationMessage` content instead of failed completion results. Rewrite the four interaction tests accordingly (e.g., `ChooseOption(5)` → `Assert.Equal(expected, vm.ValidationMessage)` then successful `ChooseOption(1)` completes normally). The exactly-once guarantee then covers only genuine settlements.

`AvaloniaClarifyChannel.cs`: exactly as in Interfaces.

`MainViewModel` additions: `AttachClarifyChannel(IClarifyChannel channel)` stores it; during a turn, the `clarify` tool invokes `AskAsync` → `present` callback sets `Clarify` (and raises PropertyChanged) on the UI thread; `SubmitAsync` checks `Clarify is not null` FIRST — routing trimmed input to the pending question (`AllowFreeText` ? submit-as-free-text : numeric parse → `ChooseOption(n)` with `ValidationMessage` on parse failure), appending the answer as a `UserMessage` transcript entry, clearing `Clarify` when settled; clarify answers do NOT increment `Messages` and do NOT start turns.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter ClarifyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: desktop clarify channel with exactly-once settlement and pending-question routing"
```

---

### Task 12: MainWindow layout + autocomplete + headless interaction tests

**Files:**

- Modify: `src/eThangAgent.Desktop/Views/MainWindow.axaml(.cs)` — full parity layout replacing the placeholder
- Create: `src/eThangAgent.Desktop/Views/AutoCompletePopup.xaml.cs` behavior (inline in code-behind is acceptable)
- Create: `src/eThangAgent.Desktop/DesktopHost.cs` — composition-root shim building `MainViewModel` from `AgentConfiguration.Load()` + `AddEThangAgentCore` + `AvaloniaClarifyChannel` + `FixedWorkspaceContext("app")` + `UnrootedPathResolver()` + root-session bootstrap (dialog on failure) + graceful `CompleteAsync` on window close
- Modify: `src/eThangAgent.Desktop/App.axaml.cs` — construct `MainWindow` with the host-built DataContext
- Test: `tests/eThangAgent.Desktop.Tests/MainWindowTests.cs`

**Interfaces:**

- Consumes: Tasks 8–11 view models; Task 7 core.
- Produces: `DesktopHost.CreateMainWindow()` used by App and tests; XAML DataTemplates keyed to every `TranscriptEntry` subtype; autocomplete popup filtering `DesktopCommands.All` on leading `/`; Enter submits, Shift+Enter newline.

Layout skeleton (bind through `DataContext`):

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:eThangAgent.Desktop.ViewModels"
        x:Class="eThangAgent.Desktop.Views.MainWindow"
        Title="eThang Agent" Width="900" Height="650"
        Closed="OnWindowClosed">
  <DockPanel>
    <Border DockPanel.Dock="Bottom">  <!-- status bar -->
      <StackPanel Orientation="Horizontal" Spacing="12" Margin="8,4">
        <TextBlock Text="{Binding Status.Spinner}" />
        <TextBlock Text="{Binding Status.PhaseLabel}" />
        <TextBlock Text="{Binding Status.ModelId}" />
        <TextBlock Text="{Binding Status.MessageCount, StringFormat='messages: {0}'}" />
      </StackPanel>
    </Border>
    <Panel>                            <!-- input area OR clarify mode -->
      <StackPanel IsVisible="{Binding Clarify, Converter={x:Static ObjectConverters.IsNull}}" DockPanel.Dock="Bottom">
        <TextBox x:Name="InputBox" Text="{Binding Input}" Watermark="Type a message. /help for commands"
                 AcceptsReturn="True" TextWrapping="Wrap" MaxHeight="120"
                 KeyDown="OnInputKeyDown" IsEnabled="{Binding !IsBusy}" />
        <Popup x:Name="CommandPopup" PlacementTarget="{Binding #InputBox}" PlacementMode="Top">
          <ListBox x:Name="CommandList" MaxHeight="120" MinWidth="300"
                   DoubleTapped="OnCommandChosen" KeyDown="OnCommandListKeyDown" />
        </Popup>
      </StackPanel>
      <StackPanel IsVisible="{Binding !Clarify, Converter={x:Static ObjectConverters.IsNull}}" DockPanel.Dock="Bottom">
        <!-- clarify mode: question, option buttons, free-text, cancel -->
        <TextBlock Text="{Binding Clarify.Question}" TextWrapping="Wrap" Margin="8,4" />
        <TextBlock Text="{Binding Clarify.ValidationMessage}" Foreground="OrangeRed" Margin="8,0" TextWrapping="Wrap" IsVisible="{Binding Clarify.ValidationMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
        <ItemsControl ItemsSource="{Binding Clarify.Options}">
          <ItemsControl.ItemTemplate>
            <DataTemplate><Button Content="{Binding}" Click="OnClarifyOption" Margin="8,2"/></DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <StackPanel Orientation="Horizontal" Spacing="8" Margin="8,4"
                    IsVisible="{Binding Clarify.AllowFreeText}">
          <TextBox Text="{Binding Clarify.Input}" Width="480" KeyDown="OnClarifyInputKeyDown" />
          <Button Content="Answer" Click="OnClarifyAnswer" />
          <Button Content="Cancel" Click="OnClarifyCancel" />
        </StackPanel>
      </StackPanel>
      <ScrollViewer>                     <!-- transcript -->
        <ItemsControl ItemsSource="{Binding Transcript.Entries}" x:Name="TranscriptList">
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <ContentControl Content="{Binding}">
                <ContentControl.Resources>
                  <DataTemplate DataType="vm:TranscriptEntry+UserMessage">
                    <TextBlock Text="{Binding Text}" FontWeight="Bold" Margin="8,2" TextWrapping="Wrap"/>
                  </DataTemplate>
                  <DataTemplate DataType="vm:TranscriptEntry+AssistantText">
                    <TextBlock Text="{Binding Text}" Margin="8,2" TextWrapping="Wrap"/>
                  </DataTemplate>
                  <DataTemplate DataType="vm:TranscriptEntry+Reasoning">
                    <TextBlock Text="{Binding Text}" FontStyle="Italic" Opacity="0.7" Margin="8,2" TextWrapping="Wrap"/>
                  </DataTemplate>
                  <DataTemplate DataType="vm:TranscriptEntry+ToolCall">
                    <TextBlock Margin="8,2" TextWrapping="Wrap"><Run Text="⚙ "/><Run Text="{Binding Name}"/><Run Text=" "/><Run Text="{Binding Arguments}" Foreground="Gray"/></TextBlock>
                  </DataTemplate>
                  <DataTemplate DataType="vm:TranscriptEntry+ToolResult">
                    <TextBlock Margin="8,2" TextWrapping="Wrap"><Run Text="↳ "/><Run Text="{Binding Name}"/><Run Text=" "/><Run Text="{Binding Summary}"/></TextBlock>
                  </DataTemplate>
                  <DataTemplate DataType="vm:TranscriptEntry+Notice">
                    <TextBlock Text="{Binding Text}" Foreground="DarkGray" Margin="8,2" TextWrapping="Wrap"/>
                  </DataTemplate>
                </ContentControl.Resources>
              </ContentControl>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </ScrollViewer>
    </Panel>
  </DockPanel>
</Window>
```

Code-behind responsibilities (all trivial, untestable-in-isolation glue lives here):

- `OnInputKeyDown`: Enter w/o Shift → `vm.SubmitAsync(InputBox.Text)` + clear; Shift+Enter → default newline. While `Input.Text` starts with `/`, filter `DesktopCommands.All` into `CommandList` and show `CommandPopup`; Tab/Enter in popup accepts selection; Esc hides.
- `OnWindowClosed`: call `vm.ShutdownAsync()` (wraps `lifecycle.CompleteAsync(rootId, msg => vm.Transcript.AddNotice(msg))`) — fire-and-forget with try/catch is acceptable at teardown; log failures via Trace.
- Auto-scroll: subscribe `Transcript.Entries.CollectionChanged` → `TranscriptList.ItemsPanelRoot?.BringIntoView(last)` best-effort.

`DesktopHost.cs`:

```csharp
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

public static class DesktopHost
{
    public static async Task<MainWindow> CreateMainWindowAsync(Func<string, Task> showError)
    {
        var settings = AgentConfiguration.Load();
        if (settings.ApiKey is null)
        {
            await showError("OPENROUTER_API_KEY environment variable not set. Get a key at https://openrouter.ai/keys");
            Environment.Exit(1);
        }

        using var services = new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey,
                ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!,
                new AgentHostOptions(new AvaloniaClarifyChannel(PresentLater),
                    new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
            .BuildServiceProvider();

        var store = services.GetRequiredService<IAgentStore>();
        var rootId = AgentId.NewId();
        var saved = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow));
        if (!saved.IsSuccess)
        {
            await showError($"failed to persist root session: [{saved.Error!.Code}] {saved.Error.Message}");
            Environment.Exit(1);
        }

        var conversation = services.GetRequiredService<Conversation>();
        var handler = services.GetRequiredService<SendMessageCommandHandler>();
        var lifecycle = services.GetRequiredService<RootSessionLifecycle>();
        var modelConfig = services.GetRequiredService<ModelConfig>();

        var vm = new MainViewModel(
            (command, ct, content, reasoning, iterEnd, toolCall, toolResult) =>
                handler.Handle(command, ct, content, reasoning, iterEnd, toolCall, toolResult),
            lifecycle, rootId, conversation, modelConfig.ModelId,
            requestClose: () => { /* window.Close() wired in code-behind */ });
        return new MainWindow(vm);
    }

    private static Task<ClarifyViewModel> PresentLater(ClarifyQuestion q)
        => throw new InvalidOperationException("clarify presenter is attached by MainViewModel at runtime");
}
```

The presenter indirection resolves in Task 11's `AttachClarifyChannel`: `AvaloniaClarifyChannel`'s `present` func delegates to `Dispatcher.UIThread.InvokeAsync(() => vm.PresentClarify(question))` once the VM exists — implement `DesktopHost` to construct the channel AFTER creating `vm`, handing it `q => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => vm.PresentClarify(q))`.

- [ ] **Step 1: Write failing headless tests**

```csharp
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.Views;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class MainWindowTests
{
    private static MainWindow BuildWindow(out MainViewModel vm)
    {
        vm = TestFixtures.CreateViewModel();          // shared fixture: stub runner echoing "ack"
        return new MainWindow(vm);
    }

    [AvaloniaFact]
    public void Typing_And_Enter_Sends_User_Message_To_Transcript()
    {
        var window = BuildWindow(out var vm);
        window.Show();
        var input = window.FindControl<TextBox>("InputBox")!;
        input.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); // after setting text
        input.Text = "hello agent";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Contains(vm.Transcript.Entries, e => e is TranscriptEntry.UserMessage u && u.Text == "hello agent");
    }

    [AvaloniaFact]
    public void Slash_Opens_Autocomplete_Listing_Three_Commands()
    {
        var window = BuildWindow(out var vm);
        window.Show();
        var input = window.FindControl<TextBox>("InputBox")!;
        var popup = window.FindControl<Popup>("CommandPopup")!;
        input.Text = "/";
        input.Focus();
        window.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.None);
        Assert.True(popup.IsOpen);
        var list = window.FindControl<ListBox>("CommandList")!;
        Assert.Equal(3, list.ItemCount);
    }

    [AvaloniaFact]
    public void Escape_Dismisses_Autocomplete()
    {
        var window = BuildWindow(out var vm);
        window.Show();
        var input = window.FindControl<TextBox>("InputBox")!;
        var popup = window.FindControl<Popup>("CommandPopup")!;
        input.Focus(); input.Text = "/";
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(popup.IsOpen);
    }
}
```

Add `TestFixtures.CreateViewModel()` to the shared fixtures file: stub `TurnRunner` succeeding with "ack", real `RootSessionLifecycle` over the StubStore, `AgentId.NewId()`, fresh `Conversation`, model "test/model".

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter MainWindowTests`
Expected: FAIL — controls/named elements absent.

- [ ] **Step 3: Implement the XAML + code-behind + DesktopHost as specified above**

Wire `MainWindow(MainViewModel vm)` constructor overload setting `DataContext = vm; InitializeComponent();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Desktop.Tests`
Expected: all desktop tests PASS (including earlier tasks').

- [ ] **Step 5: Manual launch check**

Run: `$env:OPENROUTER_API_KEY='sk-or-…'; dotnet run --project src/eThangAgent.Desktop`
Confirm: dark Fluent theme, one real streamed turn renders, `/help` prints list, autocomplete appears on `/`, `/exit` closes cleanly.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: desktop main window with transcript, autocomplete, clarify mode, host wiring"
```

---

### Task 13: Status bar phases + startup dialogs + shutdown path polish

**Files:**

- Modify: `src/eThangAgent.Desktop/ViewModels/StatusViewModel.cs` (created as stub in Task 10) — animated spinner frames cycling while `Phase != Ready`; labels: Ready / Thinking… / Streaming… with frames `["⠋","⠙","⠹","⠸","⠼","⠴","⠦","⠧","⠇","⠏"]` (identical glyph set to CLI)
- Modify: `src/eThangAgent.Desktop/Views/MainWindow.axaml.cs` — status timer start/stop on Phase changes; error-dialog helper
- Test: `tests/eThangAgent.Desktop.Tests/StatusViewModelTests.cs`

**Interfaces:**

- Produces:

```csharp
public enum TurnPhase { Ready, Thinking, Streaming }
public sealed partial class StatusViewModel : ObservableObject
{
    public StatusViewModel(string modelId);
    public string ModelId { get; }
    public TurnPhase Phase { get; set; }          // settable from any thread; property-changed raised on UI thread via dispatcher hook
    public string Spinner { get; }                // frame char when busy, empty when ready
    public string PhaseLabel { get; }             // "Ready" | "Thinking…" | "Streaming…"
    public int MessageCount { get; set; }         // bound to vm.MessageCount via binding, kept for parity naming
    public void Tick();                           // advances frame; called by a 80ms DispatcherTimer in the view
}
```

- [ ] **Step 1: Write failing tests**

```csharp
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class StatusViewModelTests
{
    [Fact]
    public void Ready_State_Shows_Empty_Spinner_And_Label()
    {
        var s = new StatusViewModel("m");
        Assert.Equal(TurnPhase.Ready, s.Phase);
        Assert.Equal("", s.Spinner);
        Assert.Equal("Ready", s.PhaseLabel);
    }

    [Fact]
    public void Thinking_Label_And_Frame_Advance_On_Tick()
    {
        var s = new StatusViewModel("m") { Phase = TurnPhase.Thinking };
        Assert.Equal("Thinking…", s.PhaseLabel);
        var first = s.Spinner;
        Assert.NotEqual("", first);
        s.Tick();
        Assert.NotEqual(first, s.Spinner);
    }

    [Fact]
    public void Streaming_Label_Replaces_Thinking()
    {
        var s = new StatusViewModel("m") { Phase = TurnPhase.Streaming };
        Assert.Equal("Streaming…", s.PhaseLabel);
    }

    [Fact]
    public void Back_To_Ready_Clears_Spinner()
    {
        var s = new StatusViewModel("m") { Phase = TurnPhase.Thinking };
        s.Tick();
        s.Phase = TurnPhase.Ready;
        Assert.Equal("", s.Spinner);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter StatusViewModelTests`
Expected: FAIL against the Task 10 stub.

- [ ] **Step 3: Implement** — spinner frames array exactly `["\u280b","\u2819","\u2839","\u2838","\u283c","\u2834","\u2826","\u2827","\u2807","\u280f"]`; `Tick()` advances index mod length; `Spinner` returns frame when `Phase != Ready` else `""`; `PhaseLabel` mapping Ready→"Ready", Thinking→"Thinking…", Streaming→"Streaming…". Thread-safety: `Phase` setter marshals `PropertyChanged` through `Avalonia.Threading.Dispatcher.UIThread.Post` when off the UI thread (guard: if `Dispatcher.UIThread.CheckAccess()` raise directly). View adds `<TextBlock Text="{Binding Status.Spinner}"/>` refresh via the existing bindings; a 80ms `DispatcherTimer` in `MainWindow` code-behind calls `Tick()` while busy.

- [ ] **Step 4: Run tests + full suite**

Run: `dotnet test tests/eThangAgent.Desktop.Tests && dotnet build`
Expected: PASS / green.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: animated status bar parity with terminal statusline"
```

---

### Task 14: Headless pipeline smoke — real core vs local mock provider

**Files:**

- Create: `tests/eThangAgent.Desktop.Tests/MockOpenRouterServer.cs` — compact port of `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs` (same SSE wire format: read that file and copy verbatim; adjust namespace to `eThangAgent.Desktop.Tests`)
- Create: `tests/eThangAgent.Desktop.Tests/DesktopPipelineSmokeTests.cs`

**Interfaces:**

- Consumes: Task 7 core + Task 12 host pieces, real `SendMessageCommandHandler`, mock HTTP endpoint.
- Produces: evidence the full desktop path works end-to-end without a real API key.

- [ ] **Step 1: Port the mock server**

Copy `tests/eThangAgent.CLI.Tests/MockOpenRouterServer.cs` to `tests/eThangAgent.Desktop.Tests/MockOpenRouterServer.cs`, changing only the namespace line. It must serve a streaming chat completion emitting at least: one content delta, one reasoning delta, one tool_call, then finish.

- [ ] **Step 2: Write the smoke test**

```csharp
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

public class DesktopPipelineSmokeTests
{
    [Fact]
    public async Task Real_Core_Through_Mock_Provider_Renders_Streamed_Transcript()
    {
        using var server = MockOpenRouterServer.Start(streamingResponse: true);
        var settings = new AgentSettings("sk-or-test", new Uri($"http://localhost:{server.Port}/v1"),
            new eThangAgent.AgentDomain.SubAgentOptions(null, TimeSpan.FromSeconds(30), 1),
            MaxToolIterationsConfiguration.Default);
        using var services = new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey!,
                ModelConfig.Create("mock/model", 256, 0.2f).Value!,
                new AgentHostOptions(new StubChannel(), new FixedWorkspaceContext("app"),
                    new eThangAgent.ToolDomain.UnrootedPathResolver()))
            .BuildServiceProvider();

        var handler = services.GetRequiredService<eThangAgent.Agent.Application.SendMessageCommandHandler>();
        var vm = new MainViewModel(
            (cmd, ct, c, r, i, tc, tr) => handler.Handle(cmd, ct, c, r, i, tc, tr),
            services.GetRequiredService<RootSessionLifecycle>(),
            eThangAgent.AgentDomain.AgentId.NewId(),
            services.GetRequiredService<eThangAgent.ConversationDomain.Conversation>(),
            "mock/model", () => { });

        await vm.SubmitAsync("say hi");
        await vm.WaitForTurnAsync();

        Assert.Contains(vm.Transcript.Entries, e => e is TranscriptEntry.AssistantText a && a.Text.Length > 0);
    }

    sealed class StubChannel : eThangAgent.ToolDomain.IClarifyChannel
    {
        public Task<eThangAgent.SharedKernel.Result<string>> AskAsync(
            eThangAgent.ToolDomain.ClarifyQuestion q, CancellationToken ct = default) =>
            Task.FromResult(eThangAgent.SharedKernel.Result<string>.Failure(
                new eThangAgent.SharedKernel.Error("Cancelled", "no clarify in smoke test")));
    }
}
```

Match `MockOpenRouterServer`'s actual constructor/start surface (read it first) and the URL shape its client expects (path segments after base URL). Adjust the `AgentSettings` base URL accordingly.

- [ ] **Step 3: Run the smoke test**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter DesktopPipelineSmokeTests`
Expected: PASS. If headless/dispatcher friction makes this disproportionate after a bounded attempt (~1 hour), fall back per spec: assert the same transcript outcomes through a VM-level integration test with a scripted `TurnRunner`, and record the fallback decision in the PR description.

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "test: headless desktop pipeline smoke against local mock provider"
```

---

### Task 15: README update + acceptance ledger

**Files:**

- Modify: `README.md`
- No new tests; runs the full verification battery.

- [ ] **Step 1: Update README**

- Under **What it can do today**, add a bullet: "Two interchangeable frontends over one shared core: the interactive terminal REPL and an Avalonia desktop app (`dotnet run --project src/eThangAgent.Desktop`) with the same feature surface — streamed responses, tool activity, clarify prompts, session persistence".
- Under **Getting started**, add the desktop launch command beside the CLI one.
- Under **Repository layout**, mention `src/eThangAgent.Composition/` (shared host-agnostic wiring) and `src/eThangAgent.Desktop/` (Avalonia frontend).
- Note the desktop host's temporary behaviors: paths are taken as given (absolute recommended; relative resolves against the process working directory) and multi-workspace support is planned.

- [ ] **Step 2: Run the acceptance ledger**

```powershell
dotnet build                      # green
dotnet test                       # every project green
dotnet run --project src/eThangAgent.Desktop   # manual parity pass: streamed turn, /help, /exit
```

Grep guards (must be silent):

```powershell
rg -l "Avalonia" src --glob '!eThangAgent.Desktop/**'   # expect: no matches
rg -n "Terminal\.ACL|eThangAgent\.CLI" src/eThangAgent.Desktop  # expect: no matches
rg -n "PowerShell" src                                   # expect: no matches
```

Parity checklist (tick each against the running app): streamed assistant text; reasoning tokens visible; tool call + result entries; clarify question answerable and cancelable; sub-agent spawn reachable through normal conversation; `/help`, `/exit`, `/quit`; status line fields (model, messages, phase); session row Completed after exit (`sqlite3 %LOCALAPPDATA%/eThangAgent/eThangAgent.db 'select id,status from agents order by rowid desc limit 1;'`).

- [ ] **Step 3: Commit**

```powershell
git add -A
git commit -m "docs: document Avalonia desktop frontend and shared composition"
```
