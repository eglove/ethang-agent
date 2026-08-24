# Agent IDE Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the single-agent desktop app into an IDE shell: a persistent main window with a left sidebar whose "Open Agent" button picks a workspace and opens that agent as a tab — multiple agents running concurrently, one per workspace.

**Architecture:** Shell + sessions. `ShellWindow` is the only top-level window (sidebar + `TabControl`). Each tab wraps today's session UI (`AgentSessionView`) backed by its own DI container built from `AddEThangAgentCore`; exactly one process-wide `AppDatabase` is shared into every container. The exec ACL stops reading ambient `Environment.CurrentDirectory` and receives its workspace root by injection, making concurrent agents safe.

**Tech Stack:** .NET 10, C#, Avalonia (classic desktop lifetime), CommunityToolkit.Mvvm, xUnit, Microsoft.Extensions.DependencyInjection, SQLite (`AppDatabase`).

**Spec:** `docs/skills/specs/2026-08-24-agent-ide-shell-design.md` — read it together with this plan; decisions marked "locked with user" live there.

## Global Constraints

- Windows-only paths; case-insensitive full-path comparison for workspace identity.
- No `.ps1`/`.sh`/`.cmd`/`.bat` scripts anywhere; repo automation is plain `dotnet` CLI.
- Every change leaves the build green: `dotnet build` + full `dotnet test` pass before each commit.
- Unit tests use fakes only — no HTTP, no OpenRouter, no real UI thread dependence unless `[AvaloniaFact]`.
- Expected failures flow through `Result<T>`; exceptions are programmer/infra errors only.
- Strict input validation at boundaries; nothing silently coerced or defaulted.
- Commits via the `git_commit` tool, Conventional style; stage with `git add <path>` first.
- If the desktop app is running, build/test with `-c Release` (Debug bin is locked).
- Domain namespaces omit the dot (`eThangAgent.ToolDomain`); ACL namespaces keep it (`eThangAgent.Roslyn.ACL`).

---
### Task 1: Share one AppDatabase across agent containers

**Files:**
- Modify: `src/eThangAgent.Composition/AgentComposition.cs` (method signature ~line 29; `.AddSingleton<AppDatabase>()` ~line 107)
- Test: `tests/eThangAgent.Composition.Tests/SharedDatabaseCompositionTests.cs` (create)

**Interfaces:**
- Consumes: existing `AddEThangAgentCore(this IServiceCollection, AgentSettings, string apiKey, ModelConfig, AgentHostOptions)`; existing `AppDatabase(string? databasePath = null)`.
- Produces: `AddEThangAgentCore(..., AgentHostOptions host, AppDatabase? sharedDatabase = null)` — later tasks call it with a shell-owned instance. Resolving `AppDatabase` from a container built with `sharedDatabase` returns that exact instance (`Assert.Same`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/eThangAgent.Composition.Tests/SharedDatabaseCompositionTests.cs
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Multi-agent frontends build one container per agent; every container must
/// resolve the SAME AppDatabase instance the shell owns.</summary>
public class SharedDatabaseCompositionTests
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "ethang-shared-db-" + Guid.NewGuid().ToString("N") + ".db");

    private ServiceProvider BuildCore(AppDatabase? shared)
    {
        var settings = new AgentSettings("sk-or-test", new Uri("https://openrouter.test"),
            new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
        return new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey!,
                ModelConfig.Create("test/model", 512, 0.5f).Value!,
                new AgentHostOptions(
                    new StubChannel(),
                    new FixedWorkspaceContext("app"),
                    AppContext.BaseDirectory,
                    new UnrootedPathResolver()),
                shared)
            .BuildServiceProvider();
    }

    [Fact]
    public void WithoutSharedDatabase_RegistersItsOwn()
    {
        using var services = BuildCore(null);
        Assert.NotNull(services.GetRequiredService<AppDatabase>());
    }

    [Fact]
    public void WithSharedDatabase_ResolvesTheSameInstance()
    {
        var shared = new AppDatabase(_dbPath);
        using var services = BuildCore(shared);
        Assert.Same(shared, services.GetRequiredService<AppDatabase>());
    }

    private sealed class StubChannel : IClarifyChannel
    {
        public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success("1"));
    }
}
```

NOTE: this test also passes the NEW 4th `AgentHostOptions` argument (`execWorkspaceRoot`, Task 2) — if you execute tasks in order, Task 2 has already landed; if you land THIS task first, omit that argument here and add it during Task 2. The recommended order lands Task 2 first.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/eThangAgent.Composition.Tests --filter SharedDatabaseCompositionTests`
Expected: FAIL — no overload of `AddEThangAgentCore` takes 6 arguments.

- [ ] **Step 3: Implement**

In `AgentComposition.cs` change the signature:

```csharp
public static IServiceCollection AddEThangAgentCore(this IServiceCollection services,
    AgentSettings settings, string apiKey, ModelConfig defaultModel, AgentHostOptions host,
    AppDatabase? sharedDatabase = null)
```

and replace the `.AddSingleton<AppDatabase>()` line with:

```csharp
.AddSingleton(_ => sharedDatabase ?? new AppDatabase())
```

Update the XML doc comment: mention the optional shared database for multi-agent hosts.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/eThangAgent.Composition.Tests --filter SharedDatabaseCompositionTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

Stage: `git add src/eThangAgent.Composition/AgentComposition.cs tests/eThangAgent.Composition.Tests/SharedDatabaseCompositionTests.cs`
Commit (git_commit tool, Conventional): type `feat`, description `allow sharing one AppDatabase across agent containers`

---
### Task 2: Exec engine takes its workspace by injection, not ambient cwd

**Files:**
- Create: `src/eThangAgent.Roslyn.ACL/ExecWorkspace.cs`
- Modify: `src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs` (ctor ~line 22; `ExecuteAsync` globals construction ~line 52)
- Modify: `src/eThangAgent.Composition/AgentHostOptions.cs` (add 4th ctor param + property)
- Modify: `src/eThangAgent.Composition/AgentComposition.cs` (registration ~line 161)
- Test: `tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs` (append tests)

**Interfaces:**
- Consumes: existing `IExecEngine.ExecuteAsync(ExecProgram, CancellationToken)` — signature UNCHANGED; existing `ScriptGlobals(registry, workspace, temp)`.
- Produces: `public sealed class ExecWorkspace { public ExecWorkspace(string root); public string Root { get; } }` in namespace `eThangAgent.Roslyn.ACL`. `CSharpScriptExecEngine(Lazy<ICapabilityRegistry>, ExecOptions)` keeps its shape but now REQUIRES the workspace: it throws `InvalidOperationException("exec workspace root is not configured...")` at execution when absent. New overload `CSharpScriptExecEngine(Lazy<ICapabilityRegistry>, ExecOptions, string? workspaceRoot = null)` and matching `(ICapabilityRegistry, ExecOptions, string? workspaceRoot = null)`. `AgentHostOptions` gains 4th optional ctor parameter `string? execWorkspaceRoot = null` and property `public string? ExecWorkspaceRoot { get; }`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs`:

```csharp
    private static CSharpScriptExecEngine CreateRootedEngine(string root)
        => new(CapabilityRegistry.Create([]), ExecOptions.Default, root);

    [Fact]
    public async Task Workspace_Is_Injected_Root_Not_Ambient_Cwd()
    {
        var root = Path.Combine(Path.GetTempPath(), "ethang-exec-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = CreateRootedEngine(root);
            var run = await engine.ExecuteAsync(new ExecProgram("Workspace"));
            Assert.Equal(ExecRunStatus.Completed, run.Status);
            Assert.Equal(Path.GetFullPath(root), run.Output.Trim());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Ambient_Cwd_Changes_Are_Ignored_When_Root_Is_Configured()
    {
        var root = Path.Combine(Path.GetTempPath(), "ethang-exec-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath(); // NOT the configured root
            var run = await CreateRootedEngine(root).ExecuteAsync(new ExecProgram("Workspace"));
            Assert.Equal(Path.GetFullPath(root), run.Output.Trim());
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_Workspace_Fails_As_Result_Not_Crash()
    {
        var engine = new CSharpScriptExecEngine(
            CapabilityRegistry.Create([]), ExecOptions.Default, workspaceRoot: null);
        var run = await engine.ExecuteAsync(new ExecProgram("1"));
        Assert.NotEqual(ExecRunStatus.Completed, run.Status);
        Assert.Contains(run.ErrorLines, e => e.Contains("workspace", StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Roslyn.ACL.Tests --filter CSharpScriptExecEngineTests`
Expected: FAIL — no 3-argument constructor exists yet.

- [ ] **Step 3: Implement**

Create `src/eThangAgent.Roslyn.ACL/ExecWorkspace.cs`:

```csharp
namespace eThangAgent.Roslyn.ACL;

/// <summary>The workspace root handed to scripts as their Workspace global. Injected per
///     container so concurrent agents never share ambient process state.</summary>
public sealed class ExecWorkspace(string root)
{
    public string Root { get; } = Path.GetFullPath(
        root ?? throw new ArgumentNullException(nameof(root)));
}
```

In `CSharpScriptExecEngine.cs`: add a nullable `string? workspaceRoot = null` third
parameter to both constructors (store `_workspaceRoot`); normalize with
`Path.GetFullPath` once at construction. In `ExecuteAsync`, replace the globals line:

```csharp
        if (_workspaceRoot is null)
        {
            return new ExecRunResult(ExecRunStatus.Failed, "",
                ["Error [ExecMisconfigured]: exec workspace root is not configured; " +
                 "the host must inject one per agent."]);
        }
        var globals = new ScriptGlobals(_registry.Value, _workspaceRoot, Path.GetTempPath());
```

NOTE: check `ExecRunStatus` for the exact non-completed member name (`Failed` vs
`Error`) before writing that line — use whatever exists.

In `AgentHostOptions.cs`: add 4th optional parameter and property:

```csharp
    public AgentHostOptions(IClarifyChannel clarifyChannel, IWorkspaceContext workspaceContext,
        IPathResolver pathResolver, string? execWorkspaceRoot = null,
        IReadOnlyList<ISystemPromptProvider>? extraPromptProviders = null)
    {
        ...
        ExecWorkspaceRoot = execWorkspaceRoot;
    }
    /// <summary>Absolute workspace root the exec ACL hands to scripts. Required for hosts
    ///     whose agents execute code; null means exec runs fail closed.</summary>
    public string? ExecWorkspaceRoot { get; }
```

IMPORTANT: `AgentHostOptions` currently passes `extraPromptProviders` as its 4th
positional argument. Every existing positional call site passing prompt providers must
switch to named syntax `[...]` → `extraPromptProviders: [...]` — grep `new AgentHostOptions(`
across src+tests and fix each.

In `AgentComposition.cs`, replace the `IExecEngine` registration:

```csharp
            .AddSingleton<IExecEngine>(sp => new CSharpScriptExecEngine(
                new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>()),
                sp.GetRequiredService<ExecOptions>(),
                host.ExecWorkspaceRoot))
```

Also update `CompositionGuardTests` ("desktop-shaped" entry) to pass an absolute temp
path as 4th argument where it exercises the desktop shape.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Roslyn.ACL.Tests --filter CSharpScriptExecEngineTests`
then `dotnet test tests/eThangAgent.Composition.Tests`
Expected: PASS everywhere.

- [ ] **Step 5: Full build green**

Run: `dotnet build && dotnet test` (use `-c Release` if the app is running).
Expected: zero errors, all suites green.

- [ ] **Step 6: Commit**

Stage: `git add src/eThangAgent.Roslyn.ACL/ExecWorkspace.cs src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs src/eThangAgent.Composition/AgentHostOptions.cs src/eThangAgent.Composition/AgentComposition.cs tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs tests/eThangAgent.Composition.Tests/CompositionGuardTests.cs`
Commit: type `feat`, description `inject exec workspace root instead of ambient cwd`

---
### Task 3: ShellViewModel — tabs, Open-Agent flow, duplicate focus, teardown

**Files:**
- Create: `src/eThangAgent.Desktop/ViewModels/ShellViewModel.cs`
- Test: `tests/eThangAgent.Desktop.Tests/ShellViewModelTests.cs` (create)

**Interfaces:**
- Consumes: `AgentSessionViewModel` (Task 4 — for the plan's purposes: has `string WorkspacePath`, `string HeaderTitle`, `Task CloseAsync()`, and property-changed `IsAwaitingClarify`; Task 4 delivers it, so if you execute strictly in order build this task against a minimal stub interface `IAgentTab` with exactly those members and let Task 4 implement it). Existing `DesktopHost.PrepareAsync(desktop, workspaceRoot)` returning `DesktopBootstrap(Services, RootId, Conversation, Handler, Lifecycle, ModelId)`.
- Produces:

```csharp
public sealed class ShellViewModel : ObservableObject
{
    public ShellViewModel(
        Func<Task<string?>> pickFolder,                       // UI seam (folder picker)
        Func<string, Task> showErrorDialog,                   // UI seam (error dialog)
        Func<string, CancellationToken, Task<AgentBootstrap>> bootstrapAgent, // heavy work
        Func<AgentBootstrap, AgentTabViewModel> createTab)    // VM factory
    public ObservableCollection<AgentTabViewModel> Tabs { get; }
    public AgentTabViewModel? SelectedTab { get; set; }
    public Task OpenAgentAsync()                              // the whole flow
    public Task CloseTabAsync(AgentTabViewModel tab)
}
public sealed record AgentBootstrap(/* DesktopBootstrap fields */);
```

The flow contract (all testable headless): pick → cancel = no-op; full-path duplicate
(case-insensitive) → select existing tab; config/API-key failure → error dialog, no
tab; success → placeholder ("starting…") inserted + selected immediately, then replaced
by the real tab on completion; bootstrap failure → placeholder removed + error dialog.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/eThangAgent.Desktop.Tests/ShellViewModelTests.cs
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ShellViewModelTests
{
    private static (ShellViewModel Vm,
        Queue<string?> Picks,
        List<string> Errors,
        List<string> Bootstrapped,
        List<AgentTabViewModel> ClosedTabs) Build(params string[] existingWorkspaces)
    {
        var picks = new Queue<string?>();
        var errors = new List<string>();
        var bootstrapped = new List<string>();
        var closed = new List<AgentTabViewModel>();

        string? Pick() => picks.Count > 0 ? picks.Dequeue() : null;
        Task ShowError(string m) { errors.Add(m); return Task.CompletedTask; }
        Task<AgentBootstrap> Bootstrap(string root, CancellationToken _)
        {
            bootstrapped.Add(root);
            return Task.FromResult(new AgentBootstrap(
                Services: null!, RootId: AgentId.NewId(), Conversation: null!,
                Handler: null!, Lifecycle: null!, ModelId: "test/model",
                WorkspacePath: root));
        }
        AgentTabViewModel CreateTab(AgentBootstrap b) => new(b,
            closeLifecycle: t => { closed.Add(t); return Task.CompletedTask; });

        var vm = new ShellViewModel(Pick, ShowError, Bootstrap, CreateTab);
        foreach (var ws in existingWorkspaces) vm.Tabs.Add(
            new AgentTabViewModel(new AgentBootstrap(null!, AgentId.NewId(), null!, null!,
                null!, "m", ws), closeLifecycle: t => { closed.Add(t); return Task.CompletedTask; }));
        return (vm, picks, errors, bootstrapped, closed);
    }

    [Fact]
    public async Task SuccessfulPick_Adds_And_Selects_Tab()
    {
        var (vm, picks, _, bootstrapped, _) = Build();
        picks.Enqueue(@"C:\ws\a");
        await vm.OpenAgentAsync();
        var tab = Assert.Single(vm.Tabs);
        Assert.Same(tab, vm.SelectedTab);
        Assert.Equal(@"C:\ws\a", tab.WorkspacePath);
        Assert.Single(bootstrapped);
    }

    [Fact]
    public async Task CancelledPicker_Is_NoOp()
    {
        var (vm, picks, errors, bootstrapped, _) = Build();
        picks.Enqueue(null);
        await vm.OpenAgentAsync();
        Assert.Empty(vm.Tabs);
        Assert.Empty(errors);
        Assert.Empty(bootstrapped);
    }

    [Fact]
    public async Task DuplicateWorkspace_Focuses_Existing_Tab()
    {
        var (vm, picks, _, bootstrapped, _) = Build(@"C:\ws\a");
        picks.Enqueue(@"C:\WS\a");  // different casing, same directory
        await vm.OpenAgentAsync();
        Assert.Single(vm.Tabs);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        Assert.Empty(bootstrapped); // never re-bootstraps an open agent
    }

    [Fact]
    public async Task BootstrapFailure_Removes_Placeholder_And_Shows_Error()
    {
        var errors = new List<string>();
        var picks = new Queue<string?>([@"C:\ws\bad"]);
        var vm = new ShellViewModel(
            () => Task.FromResult<string?>(picks.Dequeue()),
            m => { errors.Add(m); return Task.CompletedTask; },
            (_, _) => throw new InvalidOperationException("no api key"),
            b => new AgentTabViewModel(b, _ => Task.CompletedTask));
        await vm.OpenAgentAsync();
        Assert.Empty(vm.Tabs);
        Assert.Single(errors);
    }

    [Fact]
    public async Task CloseTab_Completes_Lifecycle_And_Removes()
    {
        var (vm, picks, _, _, closed) = Build();
        picks.Enqueue(@"C:\ws\a");
        await vm.OpenAgentAsync();
        var tab = vm.Tabs[0];
        await vm.CloseTabAsync(tab);
        Assert.DoesNotContain(tab, vm.Tabs);
        Assert.Single(closed);
    }
}
```

NOTE: adapt fixture details to what actually compiles (e.g. if `AgentId` needs a using,
add `using eThangAgent.AgentDomain;`). The assertions are the contract; scaffolding may flex.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter ShellViewModelTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/eThangAgent.Desktop/ViewModels/ShellViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Everything a tab needs from bootstrap, plus its workspace path. Wraps
///     DesktopBootstrap so the shell layer does not depend on host internals.</summary>
public sealed record AgentBootstrap(
    IServiceProvider? Services, AgentId RootId, object? Conversation, object? Handler,
    object? Lifecycle, string ModelId, string WorkspacePath);

/// <summary>One open agent tab. Owns its session VM once bootstrap completes; carries an
///     opaque teardown delegate so closing completes the agent lifecycle without this
///     layer knowing about DI.</summary>
public sealed partial class AgentTabViewModel : ObservableObject
{
    public AgentTabViewModel(AgentBootstrap bootstrap, Func<AgentTabViewModel, Task> closeLifecycle)
    {
        Bootstrap = bootstrap;
        WorkspacePath = bootstrap.WorkspacePath;
        HeaderTitle = Path.GetFileName(
            bootstrap.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar));
        _closeLifecycle = closeLifecycle;
    }

    public AgentBootstrap Bootstrap { get; }
    public string WorkspacePath { get; }
    public string HeaderTitle { get; }
    private readonly Func<AgentTabViewModel, Task> _closeLifecycle;

    [ObservableProperty] private bool _isStarting = true;
    [ObservableProperty] private bool _isAwaitingClarify;

    public Task CloseAsync() => _closeLifecycle(this);
}

/// <summary>The IDE shell: owns tabs and the Open-Agent flow. Picker/dialog/bootstrap are
///     injected seams — every decision here is unit-testable headless.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<string, Task> _showErrorDialog;
    private readonly Func<string, CancellationToken, Task<AgentBootstrap>> _bootstrapAgent;
    private readonly Func<AgentBootstrap, AgentTabViewModel> _createTab;

    public ShellViewModel(
        Func<Task<string?>> pickFolder,
        Func<string, Task> showErrorDialog,
        Func<string, CancellationToken, Task<AgentBootstrap>> bootstrapAgent,
        Func<AgentBootstrap, AgentTabViewModel> createTab)
    {
        _pickFolder = pickFolder; _showErrorDialog = showErrorDialog;
        _bootstrapAgent = bootstrapAgent; _createTab = createTab;
    }

    public ObservableCollection<AgentTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private AgentTabViewModel? _selectedTab;

    public bool IsOpenRunning { get; private set; }   // guards double-clicks

    public async Task OpenAgentAsync()
    {
        if (IsOpenRunning) return;
        IsOpenRunning = true;
        try
        {
            var picked = await _pickFolder();
            if (string.IsNullOrWhiteSpace(picked)) return;          // cancel = no-op

            var full = Path.GetFullPath(picked).TrimEndEndSeparator();

            var existing = Tabs.FirstOrDefault(t =>
                string.Equals(t.WorkspacePath, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { SelectedTab = existing; return; }  // focus only

            var placeholder = new AgentTabViewModel(
                new AgentBootstrap(null, AgentId.NewId(), null, null, null, "", full),
                closeLifecycle: _ => Task.CompletedTask);
            Tabs.Add(placeholder);
            SelectedTab = placeholder;

            try
            {
                var boot = await _bootstrapAgent(full, CancellationToken.None);
                var real = _createTab(boot);
                var index = Tabs.IndexOf(placeholder);
                Tabs[index] = real;
                if (SelectedTab == placeholder) SelectedTab = real;
            }
            catch (Exception ex)
            {
                Tabs.Remove(placeholder);
                await _showErrorDialog("Could not open agent: " + ex.Message);
            }
        }
        finally { IsOpenRunning = false; }
    }

    public async Task CloseTabAsync(AgentTabViewModel tab)
    {
        Tabs.Remove(tab);
        await tab.CloseAsync();   // completes the agent's root session
    }
}

internal static class ShellPathExtensions
{
    public static string TrimEndEndSeparator(this string path) =>
        path.Length > 1 && (path.EndsWith(Path.DirectorySeparatorChar) ||
                            path.EndsWith(Path.AltDirectorySeparatorChar))
            ? path[..^1] : path;
}
```

Implementation notes:
- Normalize BOTH sides before comparing (`GetFullPath` + trailing-separator trim).
- The bootstrap failure catch must catch ALL exceptions — bootstrap runs user-facing
  validation (missing key etc.) which surfaces as exceptions today.
- Keep `IsOpenRunning` reentrancy guard simple; do not disable the sidebar button.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter ShellViewModelTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

Stage: `git add src/eThangAgent.Desktop/ViewModels/ShellViewModel.cs tests/eThangAgent.Desktop.Tests/ShellViewModelTests.cs`
Commit: type `feat`, description `add shell view-model with open-agent tab flow`

---
### Task 4: AgentSessionView — today's session UI becomes a reusable tab content

**Files:**
- Create: `src/eThangAgent.Desktop/Views/AgentSessionView.axaml` + `.axaml.cs`
- Create: `src/eThangAgent.Desktop/ViewModels/AgentSessionViewModel.cs`
- Modify: `src/eThangAgent.Desktop/Views/MainWindow.axaml` + `.axaml.cs` (become ShellWindow; see Task 5)
- Test: `tests/eThangAgent.Desktop.Tests/MainWindowTests.cs` → rename to `AgentSessionViewTests.cs`

**Interfaces:**
- Consumes: `MainViewModel` unchanged in behavior.
- Produces: `public partial class AgentSessionView : UserControl` with ctor `AgentSessionView()` and `AgentSessionView(MainViewModel vm)`; `public sealed class AgentSessionViewModel { public static MainViewModel Create(AgentBootstrap boot, IClarifyChannel channel, Action requestTabClose) }` — the factory that turns a bootstrap into a wired session VM. `MainViewModel` gains an event/flag the shell can bind for the background-clarify dot: it already exposes `Clarify` (null = none), so the tab binds `IsAwaitingClarify = vm.Clarify is not null`.

- [ ] **Step 1: Move the view code**

Move ALL of today's `MainWindow.axaml` content into
`src/eThangAgent.Desktop/Views/AgentSessionView.axaml` as a `<UserControl>`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:eThangAgent.Desktop.ViewModels"
             x:Class="eThangAgent.Desktop.Views.AgentSessionView">
  <!-- paste today's MainWindow DockPanel content verbatim -->
</UserControl>
```

Code-behind: same pattern as today's `MainWindow.axaml.cs`, but `UserControl`,
no `OnWindowClosed` (tab close is owned by the shell), and no window-level concerns:

```csharp
public partial class AgentSessionView : UserControl
{
    private readonly MainViewModel? _vm;
    private Avalonia.Threading.DispatcherTimer? _statusTimer;

    public AgentSessionView() => InitializeComponent();

    public AgentSessionView(MainViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        // ... move the body of today's MainWindow(vm) constructor here verbatim:
        // transcript auto-scroll subscription, status timer, tunnel handlers,
        // InputBox.Text property subscription for autocomplete.
        // DELETE: OnWindowClosed / ShutdownAsync wiring — the shell owns teardown.
    }
    // keep every clarify/command-popup handler verbatim
}
```

- [ ] **Step 2: Rename tests**

Rename `tests/eThangAgent.Desktop.Tests/MainWindowTests.cs` → `AgentSessionViewTests.cs`;
inside, replace `new MainWindow(vm)` with `new AgentSessionView(vm)` and the class name
accordingly. Keep `[AvaloniaFact]` attributes and all assertions.

Also update `DesktopSmokeTests.MainWindow_Instantiates_And_Has_Title`: delete this test
(the title moves to the shell window, covered by Task 6's smoke test).

- [ ] **Step 3: Add the session VM factory**

```csharp
// src/eThangAgent.Desktop/ViewModels/AgentSessionViewModel.cs
using eThangAgent.Agent.Application;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Turns one agent bootstrap into a fully wired per-tab session view-model:
///     clarify presenter targets THIS tab; stream sink marshals to the UI thread;
///     requestClose closes the TAB (not the app).</summary>
public static class AgentSessionViewModel
{
    public static MainViewModel Create(AgentBootstrap bootstrap,
        IClarifyChannel channel, Action requestTabClose)
    {
        MainViewModel? viewModel = null;
        var vm = new MainViewModel(
            DesktopHost.OffUiThread((command, ct, content, reasoning, iterationEnd,
                toolCall, toolResult) =>
                ((SendMessageCommandHandler)bootstrap.Handler!).Handle(command, ct, content,
                    reasoning, iterationEnd, toolCall, toolResult)),
            (RootSessionLifecycle)bootstrap.Lifecycle!,
            bootstrap.RootId,
            (Conversation)bootstrap.Conversation!,
            bootstrap.ModelId,
            requestClose: requestTabClose,
            presentClarify: q => DesktopHost.PresentOnUIThread(
                () => viewModel!.PresentClarifyAsync(q)),
            uiStreamSink: evt => viewModel!.ApplyUiStreamEventOnUIThreadAsync(evt));
        viewModel = vm;
        vm.AttachClarifyChannel(channel);
        return vm;
    }
}
```

NOTE: `PresentOnUIThread` is private in `DesktopHost` today — widen to `internal`
(or public) as part of this task; do not duplicate it.

- [ ] **Step 4: Build green**

Run: `dotnet build && dotnet test tests/eThangAgent.Desktop.Tests`
Expected: PASS — MainWindowTests renamed suite passes against the UserControl.

- [ ] **Step 5: Commit**

Stage: `git add src/eThangAgent.Desktop/Views/AgentSessionView.axaml src/eThangAgent.Desktop/Views/AgentSessionView.axaml.cs src/eThangAgent.Desktop/ViewModels/AgentSessionViewModel.cs tests/eThangAgent.Desktop.Tests/AgentSessionViewTests.cs tests/eThangAgent.Desktop.Tests/DesktopSmokeTests.cs src/eThangAgent.Desktop/DesktopHost.cs`
Commit: type `refactor`, description `extract agent session view from main window`

---
### Task 5: Per-tab bootstrap — DesktopHost builds one container per agent

**Files:**
- Modify: `src/eThangAgent.Desktop/DesktopHost.cs` (major rewrite of this file)
- Test: `tests/eThangAgent.Desktop.Tests/ShellViewModelTests.cs` (extend) + `DesktopPipelineSmokeTests.cs` (update)

**Interfaces:**
- Consumes: `AddEThangAgentCore(..., AppDatabase? sharedDatabase = null)` (Task 1), `AgentHostOptions(..., string? execWorkspaceRoot = null, ...)` (Task 2), `AgentSessionViewModel.Create` (Task 4).
- Produces:

```csharp
public static class DesktopHost
{
    // Kept, now per-tab and WITHOUT Environment.CurrentDirectory mutation:
    public static async Task<AgentBootstrap> PrepareAgentAsync(
        AgentSettings settings, AppDatabase sharedDb,
        IClassicDesktopStyleApplicationLifetime desktop, string workspaceRoot)

    public static TurnRunner OffUiThread(TurnRunner inner)          // unchanged
    internal static Task<ClarifyViewModel> PresentOnUIThread(...)   // widened
    public static async Task ShowErrorAndExitAsync(...)             // REMOVED — replaced by:
    public static async Task ShowErrorDialogAsync(Window owner, string title, string message)
    public static Task<string?> PickWorkspaceFolderAsync(Window owner)  // owner = shell window

    // DELETED entirely: DesktopBootstrap record, CreateMainWindow,
    // DeferShutdownDuringStartup, EnableWindowCloseShutdown, TransientHostWindow,
    // AvaloniaClarifyChannel(PresentLater) startup stub.
}
```

The per-tab channel: `new AvaloniaClarifyChannel(q => PresentOnUIThread(
() => tabViewModel!.PresentClarifyAsync(q)))` — created in the composition step of
`PrepareAgentAsync`, targeting a lazily-resolved view-model reference for THAT tab.

- [ ] **Step 1: Write the failing test**

Append to `tests/eThangAgent.Desktop.Tests/ShellViewModelTests.cs`:

```csharp
    [Fact]
    public async Task TwoAgents_DifferentWorkspaces_GetDistinctContexts_OneDatabase()
    {
        var dirA = Path.Combine(Path.GetTempPath(), "ethang-shell-a-" + Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "ethang-shell-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA); Directory.CreateDirectory(dirB);
        try
        {
            var settings = new AgentSettings("sk-or-test", new Uri("https://openrouter.test"),
                new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
            var shared = new AppDatabase(Path.Combine(Path.GetTempPath(),
                "ethang-shell-shared-" + Guid.NewGuid().ToString("N") + ".db"));

            using var scopeA = DesktopHost.PrepareAgentForTests(settings, shared, dirA);
            using var scopeB = DesktopHost.PrepareAgentForTests(settings, shared, dirB);

            Assert.NotSame(scopeA.Services, scopeB.Services);
            Assert.Same(shared, scopeA.Services.GetRequiredService<AppDatabase>());
            Assert.Same(shared, scopeB.Services.GetRequiredService<AppDatabase>());

            var ctxA = scopeA.Services.GetRequiredService<IWorkspaceContext>();
            var ctxB = scopeB.Services.GetRequiredService<IWorkspaceContext>();
            Assert.Equal(dirA, ctxA.WorkspaceId);
            Assert.Equal(dirB, ctxB.WorkspaceId);

            var execA = scopeA.Services.GetRequiredService<IExecEngine>();
            var execB = scopeB.Services.GetRequiredService<IExecEngine>();
            Assert.NotSame(execA, execB);
        }
        finally { Directory.Delete(dirA, true); Directory.Delete(dirB, true); }
    }
```

with usings `eThangAgent.Composition; eThangAgent.Roslyn.ACL; eThangAgent.Storage.ACL;
eThangAgent.StateDomain; eThangAgent.ToolDomain;
Microsoft.Extensions.DependencyInjection;`.

This test needs a test seam on `DesktopHost`:

```csharp
    /// <summary>Test seam: prepares an agent without any Avalonia lifetime/window.</summary>
    public static AgentPreparationScope PrepareAgentForTests(AgentSettings settings,
        AppDatabase sharedDb, string workspaceRoot)
```

returning a small `public sealed class AgentPreparationScope : IDisposable` that owns
(`Services`, `RootId`, `Conversation`, `Handler`, `Lifecycle`, `ModelId`,
`WorkspacePath`) and disposes the provider. Production `PrepareAgentAsync` wraps exactly
this logic plus clarify-channel wiring.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter ShellViewModelTests`
Expected: FAIL — `PrepareAgentForTests` / `AgentPreparationScope` do not exist.

- [ ] **Step 3: Implement**

Rewrite `DesktopHost.PrepareAsync` into two pieces:

```csharp
public sealed class AgentPreparationScope : IDisposable
{
    public required IServiceProvider Services { get; init; }
    public required AgentId RootId { get; init; }
    public required Conversation Conversation { get; init; }
    public required SendMessageCommandHandler Handler { get; init; }
    public required RootSessionLifecycle Lifecycle { get; init; }
    public required string ModelId { get; init; }
    public required string WorkspacePath { get; init; }
    private readonly ServiceProvider _provider;
    public void Dispose() => _provider.Dispose();
    // ctor omitted for brevity — takes (provider, rootId, conversation, handler,
    // lifecycle, modelId, workspacePath) and assigns both fields and properties.
}

// inside DesktopHost:
public static AgentPreparationScope PrepareAgentForTests(AgentSettings settings,
    AppDatabase sharedDb, string workspaceRoot)
{
    if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        throw new ArgumentException("workspace directory not found: '" + workspaceRoot + "'.");

    workspaceRoot = Path.GetFullPath(workspaceRoot).TrimEndEndSeparator();

    var services = new ServiceCollection()
        .AddEThangAgentCore(settings, settings.ApiKey ?? throw new InvalidOperationException(
                "OPENROUTER_API_KEY environment variable not set. Get a key at https://openrouter.ai/keys"),
            ModelConfig.Create("stealth/ox-alpha", 32 * 1024, 0.7f).Value!,
            new AgentHostOptions(
                new StubChannel(),                       // tests never present clarify
                new FixedWorkspaceContext(workspaceRoot),
                workspaceRoot,                           // execWorkspaceRoot (Task 2 param)
                new WorkspacePathResolver(workspaceRoot)),
            sharedDb)
        .BuildServiceProvider();

    var bootstrapped = RootSessionBootstrapper.PersistRootAsync(
        services.GetRequiredService<IAgentStore>()).GetAwaiter().GetResult();
    if (!bootstrapped.IsSuccess)
        throw new InvalidOperationException(bootstrapped.Error!.Message);

    return new AgentPreparationScope(services, bootstrapped.Value!,
        services.GetRequiredService<Conversation>(),
        services.GetRequiredService<SendMessageCommandHandler>(),
        services.GetRequiredService<RootSessionLifecycle>(),
        services.GetRequiredService<ModelConfig>().ModelId, workspaceRoot);
}
```

NOTES:
- `StubChannel` = trivial `IClarifyChannel` returning a faulted/successful canned result;
  production path (`PrepareAgentAsync`) wires `AvaloniaClarifyChannel` instead.
- NO `Environment.CurrentDirectory` assignment anywhere in the new code.
- `PrepareAgentAsync(desktop, owner, settings, sharedDb, workspaceRoot)` = same body but
  builds the real `AvaloniaClarifyChannel` targeting the tab's VM (created by
  `AgentSessionViewModel.Create` with that channel), then returns the equivalent scope.

- [ ] **Step 4: Update existing smoke/E2E fixtures**

`DesktopPipelineSmokeTests.cs` and `E2EFixture.cs` currently build their own containers
via `AddEThangAgentCore` directly — update them to pass the new optional arguments
(`execWorkspaceRoot` as absolute temp path, keep their own `AppDatabase`) so they compile
and pass unchanged in behavior. `StartupShutdownModeTests.cs`: delete it — the deferral
machinery it tests is gone by design (spec §2).

- [ ] **Step 5: Full build green**

Run: `dotnet build && dotnet test` (use `-c Release` if app running).
Expected: all green.

- [ ] **Step 6: Commit**

Stage the touched files (`git add src/eThangAgent.Desktop/DesktopHost.cs tests/eThangAgent.Desktop.Tests/ShellViewModelTests.cs tests/eThangAgent.Desktop.Tests/DesktopPipelineSmokeTests.cs tests/eThangAgent.Desktop.Tests/E2EFixture.cs tests/eThangAgent.Desktop.Tests/StartupShutdownModeTests.cs`)
Commit: type `feat`, description `build one agent container per workspace`

---
### Task 6: ShellWindow — the IDE chrome, and the new App startup

**Files:**
- Create: `src/eThangAgent.Desktop/Views/ShellWindow.axaml` + `.axaml.cs`
- Modify: `src/eThangAgent.Desktop/App.axaml.cs` (rewrite `OnFrameworkInitializationCompleted`; delete `SelectWorkspaceOrShutdownAsync`)
- Delete: `src/eThangAgent.Desktop/WorkspaceStartupFlow.cs`, `src/eThangAgent.Desktop/Views/MainWindow.axaml` + `.axaml.cs`
- Test: `tests/eThangAgent.Desktop.Tests/ShellWindowTests.cs` (create); delete `WorkspaceStartupFlowTests.cs`

**Interfaces:**
- Consumes: `ShellViewModel` (Task 3), `AgentSessionView` (Task 4), `DesktopHost.PickWorkspaceFolderAsync(Window owner)` + `ShowErrorDialogAsync` (Task 5), `AgentSessionViewModel.Create` (Task 4), `DesktopHost.PrepareAgentAsync` (Task 5).
- Produces: the running app. Empty state when `Tabs.Count == 0`; tab headers show `HeaderTitle` + ✕ + clarify dot; closing the shell completes every session then exits.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/eThangAgent.Desktop.Tests/ShellWindowTests.cs
using Avalonia.Controls;
using Avalonia.Headless;
using eThangAgent.Desktop.Views;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ShellWindowTests
{
    private static ShellViewModel EmptyVm() => new(
        () => Task.FromResult<string?>(null),
        _ => Task.CompletedTask,
        (_, _) => throw new InvalidOperationException("not used"),
        b => throw new InvalidOperationException("not used"));

    [AvaloniaFact]
    public void Shell_Instantiates_With_EmptyState()
    {
        var window = new ShellWindow(EmptyVm());
        Assert.Equal("eThang Agent", window.Title);
        var emptyState = window.FindControl<StackPanel>("EmptyState");
        var tabs = window.FindControl<TabControl>("AgentTabs");
        Assert.NotNull(emptyState);
        Assert.NotNull(tabs);
        Assert.True(emptyState!.IsVisible);
    }

    [AvaloniaFact]
    public void OpenTab_Hides_EmptyState()
    {
        var vm = EmptyVm();
        var window = new ShellWindow(vm);
        vm.Tabs.Add(new AgentTabViewModel(
            new AgentBootstrap(null!, AgentId.NewId(), null!, null!, null!, "m", @"C:\ws\x"),
            _ => Task.CompletedTask));
        var emptyState = window.FindControl<StackPanel>("EmptyState")!;
        Assert.False(emptyState.IsVisible);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/eThangAgent.Desktop.Tests --filter ShellWindowTests`
Expected: FAIL — `ShellWindow` does not exist.

- [ ] **Step 3: Implement the shell view**

```xml
<!-- src/eThangAgent.Desktop/Views/ShellWindow.axaml -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:eThangAgent.Desktop.ViewModels"
        xmlns:v="using:eThangAgent.Desktop.Views"
        x:Class="eThangAgent.Desktop.Views.ShellWindow"
        Title="eThang Agent" Width="1100" Height="720">
  <Grid ColumnDefinitions="Auto,*">
    <!-- Sidebar -->
    <Border Grid.Column="0" Background="#22000000" Width="170">
      <StackPanel Margin="8">
        <Button x:Name="OpenAgentButton" Content="Open Agent"
                Click="OnOpenAgent" HorizontalAlignment="Stretch"/>
      </StackPanel>
    </Border>

    <Panel Grid.Column="1">
      <!-- Empty state -->
      <StackPanel x:Name="EmptyState" HorizontalAlignment="Center"
                  VerticalAlignment="Center" Spacing="12"
                  IsVisible="{Binding Tabs.Count, Converter={x:Static BindConverters.CountIsZero}}">
        <TextBlock Text="No agent open." FontSize="18" HorizontalAlignment="Center"/>
        <TextBlock Text="Open a workspace directory to start an agent."
                   Foreground="Gray" HorizontalAlignment="Center"/>
        <Button Content="Open Agent" Click="OnOpenAgent" HorizontalAlignment="Center"/>
      </StackPanel>

      <!-- Tabs -->
      <TabControl x:Name="AgentTabs" ItemsSource="{Binding Tabs}"
                  SelectedItem="{Binding SelectedTab}">
        <TabControl.ItemTemplate>
          <DataTemplate>
            <StackPanel Orientation="Horizontal" Spacing="6" ToolTip.Tip="{Binding WorkspacePath}">
              <TextBlock Text="{Binding HeaderTitle}"/>
              <Ellipse Width="7" Height="7" Fill="Orange"
                       IsVisible="{Binding IsAwaitingClarify}"
                       VerticalAlignment="Center"/>
              <Button Content="✕" Padding="2,0" Background="Transparent"
                      BorderThickness="0" Click="OnCloseTab"/>
            </StackPanel>
          </DataTemplate>
        </TabControl.ItemTemplate>
        <TabControl.ContentTemplate>
          <DataTemplate>
            <Panel>
              <TextBlock Text="Starting…" FontStyle="Italic" Foreground="Gray"
                         Margin="16" IsVisible="{Binding IsStarting}"/>
              <v:AgentSessionView IsVisible="{Binding !IsStarting}"/>
            </Panel>
          </DataTemplate>
        </TabControl.ContentTemplate>
      </TabControl>
    </Panel>
  </Grid>
</Window>
```

NOTE on the empty-state visibility binding: if `BindConverters.CountIsZero` does not
exist in the codebase, add a tiny `IValueConverter` (`CountIsZeroConverter`) in
`src/eThangAgent.Desktop/Views/` or bind from code-behind on
`vm.Tabs.CollectionChanged`. Pick whichever is least code; do not invent a framework.

Code-behind:

```csharp
// src/eThangAgent.Desktop/Views/ShellWindow.axaml.cs
using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

public partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;

    public ShellWindow() => InitializeComponent();

    public ShellWindow(ShellViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private async void OnOpenAgent(object? sender, RoutedEventArgs e)
        => await _vm.OpenAgentAsync();

    private async void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentTabViewModel tab })
            await _vm.CloseTabAsync(tab);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Graceful teardown of EVERY session before exit (spec §5).
        _ = Task.WhenAll(_vm.Tabs.Select(t => t.CloseAsync()))
              .ContinueWith(static t => Console.Error.WriteLine(t.Exception),
                  TaskContinuationOptions.OnlyOnFaulted);
        base.OnClosed(e);
    }
}
```

- [ ] **Step 4: Wire the real seams in App.axaml.cs**

```csharp
public override void OnFrameworkInitializationCompleted()
{
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = AgentConfiguration.Load();   // may throw → catch below
        var sharedDb = new AppDatabase();           // ONE database for the whole app

        var shell = new ShellViewModel(
            pickFolder: () => DesktopHost.PickWorkspaceFolderAsync(desktop.MainWindow!),
            showErrorDialog: msg => DesktopHost.ShowErrorDialogAsync(
                desktop.MainWindow!, "eThang Agent", msg),
            bootstrapAgent: (root, ct) => DesktopHost.PrepareAgentAsync(
                desktop, desktop.MainWindow!, settings, sharedDb, root),
            createTab: boot => CreateSessionTab(desktop, boot));

        var window = new ShellWindow(shell);
        desktop.MainWindow = window;
        window.Show();
    }
    base.OnFrameworkInitializationCompleted();
}

private static AgentTabViewModel CreateSessionTab(
    IClassicDesktopStyleApplicationLifetime desktop, AgentBootstrap boot)
{
    var channel = new AvaloniaClarifyChannel(q =>
        DesktopHost.PresentOnUIThread(() =>
            ((MainViewModel)boot.SessionViewModel!).PresentClarifyAsync(q)));
    var vm = AgentSessionViewModel.Create(boot, channel,
        requestTabClose: () => Dispatcher.UIThread.Post(() =>
            ((ShellViewModel)((ShellWindow)desktop.MainWindow!).DataContext!)
                .CloseTabCommandOrEquivalent(...)));
    ...
}
```

The exact glue above must resolve two circularities cleanly — decide at implementation:
(a) the channel needs the VM that the channel helps construct → use a mutable holder
exactly like today's `viewModel!` pattern in `DesktopHost.CreateMainWindow`;
(b) `requestTabClose` needs the shell → capture it after construction. Keep both inside
`App.axaml.cs` as small local functions; do NOT push UI-thread plumbing into the shell VM.

- [ ] **Step 5: Delete dead paths**

Delete `WorkspaceStartupFlow.cs`, `WorkspaceStartupFlowTests.cs`,
`Views/MainWindow.axaml(.cs)`. Remove now-unused usings everywhere they referenced them.

- [ ] **Step 6: Run all Desktop tests**

Run: `dotnet test tests/eThangAgent.Desktop.Tests`
Expected: PASS including new `ShellWindowTests`.

- [ ] **Step 7: Full build green + manual smoke**

Run: `dotnet build && dotnet test` (use `-c Release` if app running).
Manual smoke (human-visible): launch app → empty shell with sidebar; Open Agent → picker
→ placeholder → live transcript tab; open second agent in another directory → both tabs
stream independently; duplicate pick focuses existing tab; ✕ closes one tab gracefully;
window close exits.

- [ ] **Step 8: Commit**

Stage all touched files.
Commit: type `feat`, description `replace main window with multi-agent IDE shell`

---
### Task 7: /quit closes the tab; docs updated; final verification

**Files:**
- Modify: `src/eThangAgent.Desktop/ViewModels/AgentSessionViewModel.cs` (factory wiring)
- Modify: `README.md`, `AGENTS.md` (startup-flow descriptions)

**Interfaces:**
- Consumes: `MainViewModel` `/quit` handling → `_requestClose()` (unchanged); Task 4's `requestTabClose` seam.
- Produces: `/quit` in a tab closes THAT tab only. README + AGENTS.md describe the IDE shell.

- [ ] **Step 1: Wire requestTabClose through the factory**

In `App.axaml.cs`'s tab factory (Task 6), the `requestTabClose` action passed to
`AgentSessionViewModel.Create` must close the owning TAB:

```csharp
requestTabClose: () => Dispatcher.UIThread.Post(() =>
    shellVm.CloseTabAsync(tabVm))   // capture the AgentTabViewModel for THIS agent
```

Verify by reading the code: `MainViewModel.SubmitAsync` routes `/quit` to
`_requestClose()` → now posts `CloseTabAsync(tab)` instead of closing the window.
No `MainViewModel` change required — the seam already exists.

- [ ] **Step 2: Update README.md**

Update every statement that says the desktop app opens a workspace picker before any
window, has one agent per run, or exits on `/quit`. New description: app launches into
an empty IDE shell; "Open Agent" picks a workspace and opens it as a tab; multiple
agents run concurrently, one per directory; duplicate picks focus the existing tab;
✕ or `/quit` closes a tab; window close completes all sessions and exits.

- [ ] **Step 3: Update AGENTS.md**

Update any stale claims (e.g., if it describes the single-window startup flow or
process-global cwd alignment). Keep it describing how the system works TODAY.

- [ ] **Step 4: Full build green**

Run: `dotnet build && dotnet test` (use `-c Release` if app running).
Expected: zero errors, all suites pass.

- [ ] **Step 5: Commit**

Stage: `git add README.md AGENTS.md src/eThangAgent.Desktop/App.axaml.cs`
Commit: type `docs`, description `document multi-agent IDE shell behavior`

---

## Self-Review Record

1. **Spec coverage:** §2 shell+tabs → Tasks 3/4/6; §3 open-agent flow → Task 3 (+picker/dialog seams Task 5, wiring Task 6); §4 per-tab composition + shared DB → Tasks 1/5; exec cwd fix → Task 2; turns/threads unchanged → preserved verbatim in Task 4; §5 lifecycle (✕, clarify dot, shell-close fan-out, /quit) → Tasks 3/6/7; §6 error paths → Tasks 3/5/6; §7 testing layers → embedded per task + E2E fixtures updated in Task 5; §8 docs → Task 7. No gaps found.
2. **Placeholder scan:** deliberate implementation-time decisions are marked with exact decision criteria (ExecRunStatus member name; CountIsZero converter existence; circular-glue resolution in App). No TBDs.
3. **Type consistency:** `AgentBootstrap` fields used consistently across Tasks 3–6; `AddEThangAgentCore(..., AppDatabase?)` from Task 1 consumed in Task 5; `AgentHostOptions(..., execWorkspaceRoot, ...)` from Task 2 consumed in Task 5; `PrepareAgentForTests` produced and consumed within Task 5's test; `AgentSessionViewModel.Create(boot, channel, requestTabClose)` consistent between Tasks 4/6/7.
