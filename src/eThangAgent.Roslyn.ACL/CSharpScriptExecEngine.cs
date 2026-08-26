using System.Diagnostics;
using System.Text;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace eThangAgent.Roslyn.ACL;

public sealed class CSharpScriptExecEngine : IExecEngine
{
    private readonly Func<ICapabilityRegistry> _registry;
    private readonly ExecOptions _options;
    private readonly Func<string> _workspaceRoot;

    private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
        .AddImports("System", "System.IO", "System.Linq",
            "System.Collections.Generic", "System.Diagnostics",
            "System.Text", "System.Text.RegularExpressions")
        .AddReferences(typeof(ScriptGlobals).Assembly);

    public CSharpScriptExecEngine(Func<ICapabilityRegistry> registry, ExecOptions options,
        Func<string>? workspaceRoot = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Both the capability surface and the workspace root are resolved per execution
        // rather than captured at construction: several agents share one process, each
        // with its own workspace root, and a sub-agent's run must resolve its own surface
        // (the composition root hides human-facing actions from child contexts).
        _workspaceRoot = workspaceRoot ?? ThrowMissingWorkspace;
    }

    public CSharpScriptExecEngine(ICapabilityRegistry registry, ExecOptions options,
        Func<string>? workspaceRoot = null)
        : this(() => registry, options, workspaceRoot) { }

    /// <summary>Executes against the ambient workspace identity when the host supplies
    ///     none. The scripts' globals must name the agent's own workspace root; without
    ///     an injected resolver there is no honest answer, so this fails loudly instead
    ///     of silently adopting the process-wide current directory.</summary>
    private static string ThrowMissingWorkspace() => throw new InvalidOperationException(
        "No workspace resolver was provided to the exec engine; scripts cannot resolve 'Workspace'. " +
        "Supply one at composition (the session's IWorkspaceContext).");

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
        // The token source exists ONLY to hand the flowing cancellation token to
        // synchronous script surfaces (Shell). No engine-side budget is imposed:
        // the required per-call timeoutSeconds is the sole authority, enforced by the
        // tool layer, which also owns classification (Error [ToolTimeout]).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var globals = new ScriptGlobals(
            _registry(),
            _workspaceRoot(),
            Path.GetTempPath(),
            shellToken: cts.Token);

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
            // Scripts are synchronous model-authored code; they may legitimately block
            // (Tools.Invoke, Shell). Schedule the submission on the worker pool with the
            // caller's execution context suppressed: Task.Run alone still FLOWS the
            // ambient SynchronizationContext (.NET 6+), which would make every internal
            // await post back to a pump that synchronous script code may be blocking.
            // Suppressed, every continuation resumes on pool threads, never on a UI pump.
            Task<ScriptState<object>> scheduled;
            using (ExecutionContext.SuppressFlow())
                scheduled = Task.Run(() => script.RunAsync(globals,
                    err => err is OperationCanceledException, cts.Token));
            // The ACL is context-free by contract: its resumptions must never depend
            // on the caller's pump, so shed the captured context here as well.
            var state = await scheduled.ConfigureAwait(false);

            // An OCE thrown from synchronous script surfaces (Shell killed by the budget or
            // a user stop) does NOT propagate: Roslyn's cancelOnError predicate ends the
            // submission loop gracefully, so RunAsync returns an empty state instead.
            // Classify the outcome explicitly rather than trusting the completion shape.
            // Single budget authority: propagate rather than classify. An elapsed
            // per-call budget surfaces as Error [ToolTimeout] at the tool layer; a
            // user stop flows to the turn loop. (Roslyn ends submissions gracefully
            // when synchronous script surfaces throw, so check explicitly.)
            ct.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException)
        {
            // Propagate for classification at the tool layer - see the comment above.
            throw;
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
