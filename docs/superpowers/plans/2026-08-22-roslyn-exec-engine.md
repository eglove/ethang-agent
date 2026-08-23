# Roslyn C# Scripting Exec Engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `PowerShellExecEngine` with a Roslyn C# scripting engine. The `exec` tool accepts C# programs instead of PowerShell. `PsEvidenceRunner` becomes `CSharpEvidenceRunner`. The `PowerShell.ACL` project is deleted.

**Architecture:** A new `eThangAgent.Roslyn.ACL` project implements `IExecEngine` and `IEvidenceRunner` using `Microsoft.CodeAnalysis.CSharp.Scripting`. `ScriptGlobals` provides the model-facing API (`Workspace`, `Tools`, `Shell`, `Output`, `State`). `CSharpScriptExecEngine` compiles and runs scripts with timeout support. All domain contracts unchanged.

**Tech Stack:** .NET 10, C#, xUnit, Microsoft.CodeAnalysis.CSharp.Scripting 4.x, System.Text.Json

**Spec:** `docs/superpowers/specs/2026-08-22-untangle-powershell-design.md`

**Prerequisite:** Plan 1 (Direct File I/O) must be complete before Task 4 of this plan, since we delete `PowerShell.ACL` which FileSystem.ACL currently depends on. However, Tasks 1-3 (creating the Roslyn engine itself) can proceed in parallel with Plan 1.

## Global Constraints

- All scripts are PowerShell (`.ps1`). Build: `dotnet build`. Test: `dotnet test`.
- Domain contracts `IExecEngine`, `IEvidenceRunner`, `ExecTool`, `ExecOptions`, `ExecProgram`, `ExecRunResult`, `ExecRunStatus`, `ExecParseError`, `ExecActivity`, `ExecResultFormatter` are unchanged.
- Error format: `Error [Code]: message`. Timeout: `exec error [ExecTimeout]: ...`. Parse errors: `exec error [ExecParseError]: ...`.
- Every task leaves the build green.
- Tests use real Roslyn scripting (fast — no external process).

---

### Task 1: Create Roslyn.ACL project and ScriptGlobals

**Files:**

- Create: `src/eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj`
- Create: `src/eThangAgent.Roslyn.ACL/ScriptGlobals.cs`

**Interfaces:**

- Produces: `ScriptGlobals` with `Workspace`, `Tools`, `Shell`, `Output`, and `ScriptTools`

- [ ] **Step 1: Create the csproj**

Create `src/eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Scripting" Version="4.*" />
    <ProjectReference Include="../eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.SharedKernel/eThangAgent.SharedKernel.csproj" />
    <ProjectReference Include="../eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
    <ProjectReference Include="../eThangAgent.State.Domain/eThangAgent.State.Domain.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create ScriptGlobals**

Create `src/eThangAgent.Roslyn.ACL/ScriptGlobals.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Host object for Roslyn C# scripting. Public members are accessible as
/// top-level identifiers in the script: Workspace, Tools, Shell, Output, State.</summary>
public sealed class ScriptGlobals
{
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly ICapabilityRegistry _registry;
    private readonly bool _captureStdout;
    private TextWriter? _originalOut;

    public ScriptGlobals(ICapabilityRegistry registry, string workspace, string temp,
        bool captureStdout = true)
    {
        _registry = registry;
        Workspace = workspace;
        Temp = temp;
        _captureStdout = captureStdout;
        Tools = new ScriptTools(registry, this);
    }

    /// <summary>Workspace root directory.</summary>
    public string Workspace { get; }

    /// <summary>Temp directory for artifacts.</summary>
    public string Temp { get; }

    /// <summary>Tool-calling surface: Tools.read(...), Tools.Invoke(...), etc.</summary>
    public ScriptTools Tools { get; }

    /// <summary>Collected output lines from Output() calls and captured Console.Out.</summary>
    public IReadOnlyList<string> OutputLines => _outputLines.ToArray();

    /// <summary>Run an external process and return stdout, stderr, and exit code.</summary>
    public ShellResult Shell(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            return new ShellResult(p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ShellResult(-1, "", ex.Message);
        }
    }

    /// <summary>Append a line to the script output. Does not terminate the script.
    /// Also capture any text written to Console.Out.</summary>
    public void Output(object? value)
    {
        var text = value switch
        {
            null => "",
            string s => s,
            _ => System.Text.Json.JsonSerializer.Serialize(value)
        };
        _outputLines.Enqueue(text);
    }

    /// <summary>Redirect Console.Out to capture writes like Console.WriteLine().
    /// Called before script execution, restored after.</summary>
    public void BeginCapture()
    {
        if (_captureStdout)
        {
            _originalOut = Console.Out;
            Console.SetOut(new CapturingWriter(this));
        }
    }

    public void EndCapture()
    {
        if (_originalOut is not null)
        {
            Console.SetOut(_originalOut);
            _originalOut = null;
        }
    }

    private sealed class CapturingWriter(ScriptGlobals globals) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => globals._outputLines.Enqueue(value.ToString());
        public override void Write(string? value)
        {
            if (value is not null) globals._outputLines.Enqueue(value);
        }
        public override void WriteLine(string? value)
        {
            if (value is not null) globals._outputLines.Enqueue(value);
            else globals._outputLines.Enqueue("");
        }
    }
}

/// <summary>Result of Shell() call.</summary>
public sealed record ShellResult(int ExitCode, string Stdout, string Stderr);
```

- [ ] **Step 3: Create ScriptTools**

Append to `src/eThangAgent.Roslyn.ACL/ScriptGlobals.cs`:

```csharp
/// <summary>Tool-calling surface exposed to C# scripts as Tools.read(...) etc.
/// Each registered action becomes a public method. The generic Invoke() is always
/// available for actions whose names aren't valid C# identifiers.</summary>
public sealed class ScriptTools
{
    private readonly ICapabilityRegistry _registry;
    private readonly ScriptGlobals _globals;

    // Per-tool convenience methods — generated once at construction
    private readonly Dictionary<string, Func<object?, string>> _methods = new();

    public ScriptTools(ICapabilityRegistry registry, ScriptGlobals globals)
    {
        _registry = registry;
        _globals = globals;

        foreach (var provider in registry.Providers)
        foreach (var action in provider.Actions)
        {
            var name = action.Name;
            // Only register as convenience method if the name is a valid C# identifier
            if (IsValidIdentifier(name))
                _methods[name] = args => InvokeCore(name, args);
        }
    }

    /// <summary>Invoke a tool by name and return the raw tool result text.</summary>
    public string Invoke(string name, object? args)
    {
        var resolved = _registry.Resolve(name);
        if (!resolved.IsSuccess)
            return $"Error [UnknownAction]: {resolved.Error!.Message}";

        var json = args switch
        {
            null => "{}",
            string s => s,
            _ => System.Text.Json.JsonSerializer.Serialize(args)
        };

        var result = resolved.Value!.InvokeAsync(json).GetAwaiter().GetResult();
        return result.Content;
    }

    /// <summary>Dynamic dispatch for convenience methods generated as public methods.
    /// Matches unknown method calls by name if the tool name is valid C#.</summary>
    public string read(object? args) => Invoke("read", args);
    public string write(object? args) => Invoke("write", args);
    public string edit(object? args) => Invoke("edit", args);
    public string search_files(object? args) => Invoke("search_files", args);
    public string exec(object? args) => Invoke("exec", args);
    public string git_status(object? args) => Invoke("git_status", args);
    public string working_diff(object? args) => Invoke("working_diff", args);
    public string git_commit(object? args) => Invoke("git_commit", args);

    public string List() => string.Join("\n", _registry.Providers.SelectMany(p => p.Actions)
        .Select(a => $"{a.Name}({string.Join(", ", a.Parameters.Select(p => $"{p.Name}: {p.Type}"))})"));

    public string Describe(string name)
    {
        var resolved = _registry.Resolve(name);
        if (!resolved.IsSuccess)
            return $"Error [UnknownAction]: {resolved.Error!.Message}";
        var action = resolved.Value!.Action;
        var sb = new StringBuilder($"{action.Name} — {action.Summary}\n\n{action.Description}");
        foreach (var p in action.Parameters)
            sb.Append($"\n- {p.Name}: {p.Type} — {p.Description}");
        return sb.ToString();
    }

    private string InvokeCore(string name, object? args) => Invoke(name, args);

    private static bool IsValidIdentifier(string name)
        => !string.IsNullOrEmpty(name) && (char.IsLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
```

- [ ] **Step 4: Add project to solution**

Edit `eThangAgent.slnx` — add before `</Solution>`:

```xml
  <Project Path="src/eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj" />
```

- [ ] **Step 5: Build to verify**

```powershell
dotnet build src\eThangAgent.Roslyn.ACL --nologo
```

Expected: Build succeeds, no errors.

- [ ] **Step 6: Commit**

```bash
git add src/eThangAgent.Roslyn.ACL/ eThangAgent.slnx
git commit -m "feat: create Roslyn.ACL project with ScriptGlobals and ScriptTools"
```

---

### Task 2: CSharpScriptExecEngine

**Files:**

- Create: `src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs`
- Create: `tests/eThangAgent.Roslyn.ACL.Tests/eThangAgent.Roslyn.ACL.Tests.csproj`
- Create: `tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs`
- Create: `tests/eThangAgent.Roslyn.ACL.Tests/GlobalUsings.cs`

**Interfaces:**

- Consumes: `IExecEngine`, `ExecOptions`, `ExecProgram`, `ExecRunResult`, `ExecRunStatus`, `ICapabilityRegistry`
- Produces: `CSharpScriptExecEngine` implementing `IExecEngine`

- [ ] **Step 1: Create test project**

Create `tests/eThangAgent.Roslyn.ACL.Tests/eThangAgent.Roslyn.ACL.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj" />
    <ProjectReference Include="../../src/eThangAgent.Tool.Domain/eThangAgent.Tool.Domain.csproj" />
    <ProjectReference Include="../../src/eThangAgent.Capability.Domain/eThangAgent.Capability.Domain.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/eThangAgent.Roslyn.ACL.Tests/GlobalUsings.cs`:

```csharp
global using Xunit;
```

- [ ] **Step 2: Write failing tests**

Create `tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs`:

```csharp
using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

public class CSharpScriptExecEngineTests
{
    private static CSharpScriptExecEngine CreateEngine(ExecOptions? options = null)
        => new(CapabilityRegistry.Create([]), options ?? ExecOptions.Default);

    [Fact]
    public async Task StringReturnValue_BecomesOutput()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("\"hello from csharp\""));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("hello from csharp", run.Output);
        Assert.Empty(run.ErrorLines);
    }

    [Fact]
    public async Task IntReturnValue_SerializedToJson()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("42"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("42", run.Output);
    }

    [Fact]
    public async Task VoidScript_ReturnsEmptyOutput()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("var x = 1 + 1;"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("", run.Output);
    }

    [Fact]
    public async Task Output_CapturesLinesDuringExecution()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("Output(\"line1\"); Output(\"line2\"); 0"));

        Assert.Contains("line1", run.Output);
        Assert.Contains("line2", run.Output);
    }

    [Fact]
    public async Task CompileError_ReturnsInValidate()
    {
        var engine = CreateEngine();
        var errors = await engine.ValidateAsync(new ExecProgram("this is not valid csharp ??!!"));

        Assert.True(errors.IsSuccess);
        Assert.NotEmpty(errors.Value!);
    }

    [Fact]
    public async Task RuntimeException_BecomesError()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("throw new System.Exception(\"boom\");"));

        Assert.Equal(ExecRunStatus.Completed, run.Status); // completed with error lines
        Assert.NotEmpty(run.ErrorLines);
        Assert.Contains("boom", run.ErrorLines[0]);
    }

    [Fact]
    public async Task Shell_RunsCommand()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("var r = Shell(\"cmd\", \"/c\", \"echo hello\"); return r.Stdout;"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("hello", run.Output);
    }
}
```

Add test project to solution in `eThangAgent.slnx`:

```xml
  <Project Path="tests/eThangAgent.Roslyn.ACL.Tests/eThangAgent.Roslyn.ACL.Tests.csproj" />
```

- [ ] **Step 3: Run tests to verify they fail**

```powershell
dotnet test tests\eThangAgent.Roslyn.ACL.Tests --filter "CSharpScriptExecEngineTests" --nologo -v q
```

Expected: FAIL — `CSharpScriptExecEngine` does not exist yet.

- [ ] **Step 4: Implement CSharpScriptExecEngine**

Create `src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL;

public sealed class CSharpScriptExecEngine : IExecEngine
{
    private readonly Lazy<ICapabilityRegistry> _registry;
    private readonly ExecOptions _options;

    private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
        .AddImports("System", "System.IO", "System.Linq",
            "System.Collections.Generic", "System.Diagnostics",
            "System.Text", "System.Text.RegularExpressions")
        .AddReferences(typeof(CSharpScriptExecEngine).Assembly);

    public CSharpScriptExecEngine(Lazy<ICapabilityRegistry> registry, ExecOptions options)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public CSharpScriptExecEngine(ICapabilityRegistry registry, ExecOptions options)
        : this(new Lazy<ICapabilityRegistry>(() => registry), options) { }

    public async Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
        ExecProgram program, CancellationToken ct = default)
    {
        var script = CSharpScript.Create(program.Text, ScriptOpts, typeof(ScriptGlobals));
        var diagnostics = script.Compile(ct);
        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d =>
            {
                var loc = d.Location.GetMappedLineSpan();
                return new ExecParseError(
                    loc.StartLinePosition.Line + 1,
                    loc.StartLinePosition.Character + 1,
                    d.GetMessage());
            })
            .ToList();
        return Result<IReadOnlyList<ExecParseError>>.Success(errors);
    }

    public async Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
    {
        var globals = new ScriptGlobals(
            _registry.Value,
            Environment.CurrentDirectory,
            Path.GetTempPath());

        var script = CSharpScript.Create(program.Text, ScriptOpts, typeof(ScriptGlobals));
        var compileDiagnostics = script.Compile(ct);
        var compileErrors = compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (compileErrors.Count > 0)
        {
            return new ExecRunResult(ExecRunStatus.Completed, "",
                compileErrors.Select(d => d.GetMessage()).ToList());
        }

        globals.BeginCapture();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);

            var state = await script.RunAsync(globals, err =>
            {
                if (err is OperationCanceledException) return true;
                return false;
            }, cts.Token);

            var outputLines = new List<string>(globals.OutputLines);
            if (state.ReturnValue is not null && state.ReturnValue is not ScriptGlobals)
            {
                var text = state.ReturnValue switch
                {
                    string s => s,
                    _ => System.Text.Json.JsonSerializer.Serialize(state.ReturnValue)
                };
                if (!string.IsNullOrEmpty(text))
                    outputLines.Add(text);
            }

            var output = string.Join("\n", outputLines);
            return new ExecRunResult(ExecRunStatus.Completed, output, []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ExecRunResult(ExecRunStatus.Cancelled,
                string.Join("\n", globals.OutputLines), [],
                "Execution was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new ExecRunResult(ExecRunStatus.Timeout,
                string.Join("\n", globals.OutputLines), [],
                $"Execution timed out after {_options.Timeout.TotalSeconds:0} seconds; script stopped.");
        }
        catch (Exception ex)
        {
            var output = string.Join("\n", globals.OutputLines);
            return new ExecRunResult(ExecRunStatus.Completed, output,
                [$"Error [ScriptError]: {ex.Message}"]);
        }
        finally
        {
            globals.EndCapture();
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```powershell
dotnet test tests\eThangAgent.Roslyn.ACL.Tests --filter "CSharpScriptExecEngineTests" --nologo -v q
```

Expected: PASS (all 7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/eThangAgent.Roslyn.ACL/CSharpScriptExecEngine.cs tests/eThangAgent.Roslyn.ACL.Tests/ eThangAgent.slnx
git commit -m "feat: CSharpScriptExecEngine with compile, run, timeout, and Shell()"
```

---

### Task 3: CSharpEvidenceRunner

**Files:**

- Create: `src/eThangAgent.Roslyn.ACL/CSharpEvidenceRunner.cs`
- Modify: `tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs` (add evidence tests)

**Interfaces:**

- Consumes: `IEvidenceRunner`, `EvidenceOptions`
- Produces: `CSharpEvidenceRunner` implementing `IEvidenceRunner`

- [ ] **Step 1: Write evidence runner tests**

Append to `CSharpScriptExecEngineTests.cs`:

```csharp
public class CSharpEvidenceRunnerTests
{
    [Fact]
    public async Task TrueExpression_ReturnsConfirmed()
    {
        var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
        var r = await runner.RunAsync("1 + 1 == 2");

        Assert.True(r.Confirmed);
        Assert.Empty(r.Detail);
    }

    [Fact]
    public async Task FalseExpression_ReturnsNotConfirmed()
    {
        var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
        var r = await runner.RunAsync("1 == 2");

        Assert.False(r.Confirmed);
        Assert.NotEmpty(r.Detail);
    }

    [Fact]
    public async Task Exception_ReturnsNotConfirmed()
    {
        var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
        var r = await runner.RunAsync("throw new System.Exception(\"fail\")");

        Assert.False(r.Confirmed);
        Assert.Contains("fail", r.Detail);
    }

    [Fact]
    public async Task FileExists_Evidence_Works()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
            var r = await runner.RunAsync($"System.IO.File.Exists(@\"{tmp.Replace("\\", "\\\\")}\")");
            Assert.True(r.Confirmed);
        }
        finally { File.Delete(tmp); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests\eThangAgent.Roslyn.ACL.Tests --filter "CSharpEvidenceRunnerTests" --nologo -v q
```

Expected: FAIL — `CSharpEvidenceRunner` does not exist.

- [ ] **Step 3: Implement CSharpEvidenceRunner**

Create `src/eThangAgent.Roslyn.ACL/CSharpEvidenceRunner.cs`:

```csharp
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using eThangAgent.StateDomain;

namespace eThangAgent.Roslyn.ACL;

public sealed class CSharpEvidenceRunner : IEvidenceRunner
{
    private readonly EvidenceOptions _options;

    private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
        .AddImports("System", "System.IO")
        .AddReferences(typeof(CSharpEvidenceRunner).Assembly);

    public CSharpEvidenceRunner(EvidenceOptions options)
        => _options = options ?? EvidenceOptions.Default;

    public async Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);

            var script = CSharpScript.Create<bool>(command, ScriptOpts);
            var state = await script.RunAsync(cts.Token);

            if (state.ReturnValue)
                return new EvidenceResult(command, true, "");
            else
                return new EvidenceResult(command, false, "Expression evaluated to false.");
        }
        catch (OperationCanceledException)
        {
            return new EvidenceResult(command, false,
                $"Timed out after {_options.Timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            return new EvidenceResult(command, false, ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test tests\eThangAgent.Roslyn.ACL.Tests --filter "CSharpEvidenceRunnerTests" --nologo -v q
```

Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/eThangAgent.Roslyn.ACL/CSharpEvidenceRunner.cs tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs
git commit -m "feat: add CSharpEvidenceRunner"
```

---

### Task 4: ExecGuide rewrite and ExecTool description update

**Files:**

- Modify: `src/eThangAgent.Tool.Domain/ExecGuide.cs`
- Modify: `src/eThangAgent.Tool.Domain/ExecTool.cs`

**Interfaces:**

- Produces: Updated ExecGuide (C# instead of PowerShell) and ExecTool Definition description

- [ ] **Step 1: Rewrite ExecGuide**

Replace `src/eThangAgent.Tool.Domain/ExecGuide.cs` entirely:

```csharp
namespace eThangAgent.ToolDomain;

public static class ExecGuide
{
    public const string Version = "2.0";

    public const string Text = """
    ## exec — writing C# programs

    `exec` runs a C# program you write. Its only parameter is `program`, a string of
    C# text. The script runs in-process with Roslyn scripting. The return value becomes
    the output: strings verbatim, other values as one-line JSON. Write exactly what you
    want back and nothing else.

    ### Writing output

    Return a value to produce the final output:

        return "hello";
        return 42;
        return new { count = 5, name = "alpha" };  // serialized to JSON

    Call Output() during execution for intermediate lines:

        Output("processing...");
        // ... work ...
        return "done";

    Console.WriteLine() also works and its output is captured.

    ### Calling tools

    Tools are methods on the `Tools` object taking one anonymous object argument:

        Tools.read(new { path = "src/App.cs", startLine = 1, endLine = 50 });
        Tools.search_files(new { pattern = "TODO", regex = false, rootPath = ".", maxResults = 20, contextLines = 2 });

    Discover available tools:

        Tools.List()
        Tools.Describe("read")

    ### Running external commands

    Shell() runs an external process and returns exit code, stdout, and stderr:

        var r = Shell("dotnet", "build");
        if (r.ExitCode != 0) { Output(r.Stderr); return "build failed"; }
        return "build OK";

    Working directory is the agent workspace.

    ### File system and LINQ

    Use System.IO and System.Linq:

        var files = Directory.EnumerateFiles(Workspace, "*.cs", SearchOption.AllDirectories);
        return files.Count();

        var sizes = files.Select(f => new { Name = Path.GetFileName(f), Size = new FileInfo(f).Length });
        return string.Join("\n", sizes.Select(x => $"{x.Name}: {x.Size}"));

    ### Delegating subtasks

    agent.spawn is available via Tools.Invoke():

        var id = Tools.Invoke("agent.spawn", new { taskPrompt = "Summarize auth module", model = "provider/cheap-model", label = "research" });
        return id;

    Poll progress:

        Tools.Invoke("agent.status", new { id = "<guid>" });
        Tools.Invoke("agent.result", new { id = "<guid>" });

    ### Recalling earlier work

        Tools.Invoke("memory.sessions", new { });
        Tools.Invoke("memory.recall", new { query = "deploy rollback", scope = "global" });

    ### State

        Tools.Invoke("state.set", new { key = "current/head", value = "done" });
        Tools.Invoke("state.get", new { key = "current/head" });

    ### Errors

    Tool failures return error text: `Error [Code]: message`. Wrap in try/catch:

        try { Tools.read(new { path = "missing.txt", startLine = 1, endLine = 5 }); }
        catch (Exception ex) { Output("fallback: " + ex.Message); }

    ### Rules

    - Return value is the output. null/void produces empty output.
    - Output over 50,000 characters is truncated; full text saved to [exec:artifact <path>].
    - exec cannot call itself (no nested exec).
    - A 120s timeout stops the script.
    - Use anonymous objects for tool args: new { path = "...", startLine = 1 }.
    """;
}
```

- [ ] **Step 2: Update ExecTool Definition**

In `src/eThangAgent.Tool.Domain/ExecTool.cs`, replace the `ToolDefinition` description string with:

```csharp
    public ToolDefinition Definition { get; } = new(
        ToolName,
        """
        Execute a C# program in the agent workspace. The script runs in-process via Roslyn scripting. The return value is the result: strings verbatim, other objects as one-line JSON. Call Output() during execution for intermediate output. Console.WriteLine is also captured. Thrown exceptions mark the result as an error with exec error [ScriptError] lines. Output over 50,000 characters is truncated with both ends preserved and the full output saved to a file reported as [exec:artifact <path>] — read that file with the read tool. Compile errors report 'exec error [ExecParseError]:' followed by 'line N, col M: message' entries. Timeouts (120s) report 'exec error [ExecTimeout]:' with partial output. Tools are available as methods on the Tools object taking one anonymous object: Tools.read(new { path = "file.txt", startLine = 1, endLine = 50 }). Tools.Invoke("name", args) is the generic form. Tools.List() lists available tools. Nested exec is not available. Malformed arguments to exec itself report 'Error [Code]: ...'.
        """,
        [new ToolParameter("program", ToolParameterType.String,
            "The C# program text to execute.")]);
```

- [ ] **Step 3: Build to verify**

```powershell
dotnet build --nologo
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/eThangAgent.Tool.Domain/ExecGuide.cs src/eThangAgent.Tool.Domain/ExecTool.cs
git commit -m "docs: rewrite ExecGuide and ExecTool description for C# scripting"
```

---

### Task 5: Delete PowerShell.ACL, update solution and DI wiring

**Files:**

- Delete: `src/eThangAgent.PowerShell.ACL/` (entire directory)
- Delete: `tests/eThangAgent.PowerShell.ACL.Tests/` (entire directory)
- Modify: `eThangAgent.slnx`
- Modify: `src/eThangAgent.CLI/Program.cs` (DI registrations)
- Modify: `src/eThangAgent.CLI/eThangAgent.CLI.csproj`
- Modify: `src/eThangAgent.CLI/SuperpowersBootstrapPromptProvider.cs`
- Modify: `src/eThangAgent.Capability.Domain/CapabilityNameRules.cs`

**Interfaces:**

- Consumes: `CSharpScriptExecEngine`, `CSharpEvidenceRunner`
- Produces: Wired DI container, clean build, zero PowerShell references

- [ ] **Step 1: Update CLI csproj**

In `src/eThangAgent.CLI/eThangAgent.CLI.csproj`, remove:

```xml
    <ProjectReference Include="../eThangAgent.PowerShell.ACL/eThangAgent.PowerShell.ACL.csproj" />
```

Add:

```xml
    <ProjectReference Include="../eThangAgent.Roslyn.ACL/eThangAgent.Roslyn.ACL.csproj" />
```

- [ ] **Step 2: Update Program.cs DI registrations**

Replace lines 196 and 217-219:

- Change `PsEvidenceRunner` to `CSharpEvidenceRunner`
- Change `PowerShellExecEngine` to `CSharpScriptExecEngine`
- Add `using eThangAgent.Roslyn.ACL;` (replace `using eThangAgent.PowerShell.ACL;`)

The DI wiring becomes:

```csharp
            .AddSingleton<IEvidenceRunner, CSharpEvidenceRunner>()
```

And:

```csharp
            .AddSingleton<IExecEngine>(sp => new CSharpScriptExecEngine(
                new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>()),
                sp.GetRequiredService<ExecOptions>()))
```

- [ ] **Step 3: Update SuperpowersBootstrapPromptProvider**

In `src/eThangAgent.CLI/SuperpowersBootstrapPromptProvider.cs`, change:

```csharp
        - Run shell commands/tests/git plumbing -> exec (PowerShell only)
```

to:

```csharp
        - Run shell commands/tests/git plumbing -> exec (C# scripting)
```

- [ ] **Step 4: Update CapabilityNameRules comment**

In `src/eThangAgent.Capability.Domain/CapabilityNameRules.cs`, change the comment:

```csharp
    /// <summary>Action names become PowerShell function names — restrict to what is
    ///     safe to generate, reject rather than sanitize.</summary>
```

to:

```csharp
    /// <summary>Action names become C# method names — restrict to what is
    ///     safe to generate, reject rather than sanitize.</summary>
```

- [ ] **Step 5: Remove PowerShell projects from solution**

In `eThangAgent.slnx`, remove:

```xml
  <Project Path="src/eThangAgent.PowerShell.ACL/eThangAgent.PowerShell.ACL.csproj" />
  <Project Path="tests/eThangAgent.PowerShell.ACL.Tests/eThangAgent.PowerShell.ACL.Tests.csproj" />
```

Ensure Roslyn.ACL and Roslyn.ACL.Tests are present (added in Tasks 1 and 2).

- [ ] **Step 6: Delete PowerShell projects from disk**

```powershell
Remove-Item -Recurse -Force src\eThangAgent.PowerShell.ACL
Remove-Item -Recurse -Force tests\eThangAgent.PowerShell.ACL.Tests
```

- [ ] **Step 7: Build solution**

```powershell
dotnet build --nologo
```

Expected: Build succeeds with zero errors. No `System.Management.Automation` reference in any project.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: wire Roslyn.ACL, delete PowerShell.ACL, update all references"
```

---

### Task 6: Full verification — tests, publish, smoke test

**Files:**

- None (verification only)

- [ ] **Step 1: Run full test suite**

```powershell
dotnet test --nologo -v q
```

Expected: All test projects pass. No `PowerShell.ACL.Tests` project exists. Roslyn.ACL.Tests passes.

- [ ] **Step 2: Confirm zero PowerShell references**

```powershell
dotnet list package --include-transitive | Select-String "Automation"
```

Expected: No matches.

- [ ] **Step 3: Publish**

```powershell
dotnet publish src\eThangAgent.CLI -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true --nologo -v q
```

Expected: Success.

- [ ] **Step 4: Smoke-test the published exe**

```powershell
$env:OPENROUTER_API_KEY='dummy'
'quit' | & src\eThangAgent.CLI\bin\Release\net10.0\win-x64\publish\eThangAgent.CLI.exe 2>&1
```

Expected: Exe starts, prints prompt, exits cleanly — no crash dump, no ArgumentNullException.

- [ ] **Step 5: Verify exec tool with a C# program**

```powershell
$env:OPENROUTER_API_KEY='dummy'
'exec {"program":"return \"hello from csharp\";"}' | & src\eThangAgent.CLI\bin\Release\net10.0\win-x64\publish\eThangAgent.CLI.exe 2>&1
```

Expected: Output contains "hello from csharp".

- [ ] **Step 6: Commit if any final fixes were needed**

```bash
git add -A
git commit -m "fix: final adjustments from verification"
```
