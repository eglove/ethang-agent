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

    /// <summary>Run an external command line and return stdout, stderr, and exit code.
    ///     Every argument after <paramref name="exe"/> is one token of a single native
    ///     command line. Execution routes through PowerShell (-NoProfile) with the whole
    ///     line passed via -EncodedCommand, so a multi-token piece such as "build -c
    ///     Release" is re-parsed as separate tokens instead of reaching the exe as one
    ///     quoted literal argument, and the native exit code propagates verbatim
    ///     (bare -Command maps inner failures to 1; the explicit exit guard prevents
    ///     that). No intermediate quoting layer exists: the line travels base64-encoded.</summary>
    public ShellResult Shell(string exe, params string[] args)
    {
        var commandLine = string.Join(" ", new[] { exe }.Concat(args));
        var script =
            commandLine + "\n" +
            "if ($LASTEXITCODE -is [int]) { exit $LASTEXITCODE }\n" +
            "elseif ($?) { exit 0 } else { exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);

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

        // Offload to the worker pool before blocking: scripts are synchronous but the
        // registry is async, and awaiting inline deadlocks whenever this runs on a thread
        // whose SynchronizationContext must pump (e.g. Avalonia's UI thread). Task.Run
        // alone still FLOWS the ambient context (.NET 6+), so the flow is suppressed —
        // the invocation's continuation resumes on the pool, never on a blocked pump.
        Task<CapabilityInvocationResult> scheduled;
        using (ExecutionContext.SuppressFlow())
            scheduled = Task.Run(() => _registry.InvokeAsync(resolved.Value!, json));
        var result = scheduled.GetAwaiter().GetResult();
        return result.Content;
    }

    /// <summary>Dynamic dispatch for convenience methods generated as public methods.
    /// Matches unknown method calls by name if the tool name is valid C#. The argument
    /// defaults to null so zero-argument actions bind without a dummy object —
    /// Tools.git_status() and Tools.Invoke("git_status", null) are equivalent.</summary>
    public string read(object? args = null) => Invoke("read", args);
    public string write(object? args = null) => Invoke("write", args);
    public string edit(object? args = null) => Invoke("edit", args);
    public string search_files(object? args = null) => Invoke("search_files", args);
    public string exec(object? args = null) => Invoke("exec", args);
    public string git_status(object? args = null) => Invoke("git_status", args);
    public string working_diff(object? args = null) => Invoke("working_diff", args);
    public string git_commit(object? args = null) => Invoke("git_commit", args);

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
