using eThangAgent.StateDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

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
            var diagnostics = script.Compile(cts.Token);
            if (HasErrors(diagnostics))
            {
                // Retry as a statement: bare statements like "throw new ..." need a
                // trailing semicolon, while expressions must stay as-is.
                var asStatement = CSharpScript.Create<bool>(command + ";", ScriptOpts);
                var statementDiagnostics = asStatement.Compile(cts.Token);
                if (!HasErrors(statementDiagnostics))
                {
                    script = asStatement;
                    diagnostics = statementDiagnostics;
                }
            }

            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                return new EvidenceResult(command, false,
                    string.Join("\n", errors.Select(d => d.GetMessage())));
            }

            var state = await script.RunAsync(cancellationToken: cts.Token);

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

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
        => diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
