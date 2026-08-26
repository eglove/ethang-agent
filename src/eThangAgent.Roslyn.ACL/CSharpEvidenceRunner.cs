using System.Collections.Immutable;
using eThangAgent.StateDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using System.Globalization;

namespace eThangAgent.Roslyn.ACL;

public sealed class CSharpEvidenceRunner(EvidenceOptions options) : IEvidenceRunner
{
  private readonly EvidenceOptions _options = options ?? EvidenceOptions.Default;

  private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
      .AddImports("System", "System.IO")
      .AddReferences(typeof(CSharpEvidenceRunner).Assembly);

  public async Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
  {
    try
    {
      using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(_options.Timeout);

      Script<bool> script = CSharpScript.Create<bool>(command, ScriptOpts);
      ImmutableArray<Diagnostic> diagnostics = script.Compile(cts.Token);
      if (HasErrors(diagnostics))
      {
        // Retry as a statement: bare statements like "throw new ..." need a
        // trailing semicolon, while expressions must stay as-is.
        Script<bool> asStatement = CSharpScript.Create<bool>(command + ";", ScriptOpts);
        ImmutableArray<Diagnostic> statementDiagnostics = asStatement.Compile(cts.Token);
        if (!HasErrors(statementDiagnostics))
        {
          script = asStatement;
          diagnostics = statementDiagnostics;
        }
      }

      List<Diagnostic> errors = [.. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
      if (errors.Count > 0)
      {
        return new EvidenceResult(command, false,
            string.Join("\n", errors.Select(d => d.GetMessage(CultureInfo.InvariantCulture))));
      }

      ScriptState<bool> state = await script.RunAsync(cancellationToken: cts.Token).ConfigureAwait(false);

      return state.ReturnValue
        ? new EvidenceResult(command, true, "")
        : new EvidenceResult(command, false, "Expression evaluated to false.");
    }
    catch (OperationCanceledException)
    {
      return new EvidenceResult(command, false,
          $"Timed out after {_options.Timeout.TotalSeconds:0}s.");
    }
    // Named decision (CA1031): evidence checks must surface any fault as a failed
    // check result for state.verify fail-closed handling, never crash the turn.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return new EvidenceResult(command, false, ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  private static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
      => diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
