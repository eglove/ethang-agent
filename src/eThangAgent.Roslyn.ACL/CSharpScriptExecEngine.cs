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
