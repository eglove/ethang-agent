using System.Collections.Immutable;
using System.Globalization;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace eThangAgent.Roslyn.ACL;

public sealed class CSharpScriptExecEngine(Func<ICapabilityRegistry> registry, ExecOptions options,
    Func<string>? workspaceRoot = null) : IExecEngine
{
  private readonly Func<ICapabilityRegistry> _registry = registry ?? throw new ArgumentNullException(nameof(registry));
  private readonly Func<string> _workspaceRoot = workspaceRoot ?? ThrowMissingWorkspace;
#pragma warning disable IDE0051 // Remove unread private member
  private ExecOptions? OptionsUnused => options; // retained for API compatibility
#pragma warning restore IDE0051

  private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
      .AddImports("System", "System.IO", "System.Linq",
          "System.Collections.Generic", "System.Diagnostics",
          "System.Text", "System.Text.RegularExpressions")
      .AddReferences(typeof(ScriptGlobals).Assembly);

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

  public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
      ExecProgram program, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(program);
    Script<object> script = CSharpScript.Create(program.Text, ScriptOpts, typeof(ScriptGlobals));
    ImmutableArray<Diagnostic> diagnostics = script.Compile(ct);
    List<ExecParseError> errors = [.. diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d =>
        {
          FileLinePositionSpan loc = d.Location.GetMappedLineSpan();
          return new ExecParseError(
                  loc.StartLinePosition.Line + 1,
                  loc.StartLinePosition.Character + 1,
                  d.GetMessage(CultureInfo.InvariantCulture));
        })];
    return Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>(errors));
  }

  public async Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
  {
    // The token source exists ONLY to hand the flowing cancellation token to
    // synchronous script surfaces (Shell). No engine-side budget is imposed:
    // the required per-call timeoutSeconds is the sole authority, enforced by the
    // tool layer, which also owns classification (Error [ToolTimeout]).
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    ArgumentNullException.ThrowIfNull(program);

    ScriptGlobals globals = new(
        _registry(),
        _workspaceRoot(),
        Path.GetTempPath(),
        shellToken: cts.Token);

    Script<object> script = CSharpScript.Create(program.Text, ScriptOpts, typeof(ScriptGlobals));
    ImmutableArray<Diagnostic> compileDiagnostics = script.Compile(ct);
    List<Diagnostic> compileErrors = [.. compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
    if (compileErrors.Count > 0)
    {
      return new ExecRunResult(ExecRunStatus.Completed, "",
          [.. compileErrors.Select(d => d.GetMessage(CultureInfo.InvariantCulture))]);
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
      {
        // The linked token on Task.Run itself: a cancellation that fires before the
        // delegate starts yields a Canceled task instead of executing the script.
        scheduled = Task.Run(() => script.RunAsync(globals,
            err => err is OperationCanceledException, cts.Token), cts.Token);
      }
      // The ACL is context-free by contract: its resumptions must never depend
      // on the caller's pump, so shed the captured context here as well.
      ScriptState<object> state = await scheduled.ConfigureAwait(false);

      // An OCE thrown from synchronous script surfaces (Shell killed by the budget or
      // a user stop) does NOT propagate: Roslyn's cancelOnError predicate ends the
      // submission loop gracefully, so RunAsync returns an empty state instead.
      // Classify the outcome explicitly rather than trusting the completion shape.
      // Single budget authority: propagate rather than classify. An elapsed
      // per-call budget surfaces as Error [ToolTimeout] at the tool layer; a
      // user stop flows to the turn loop. (Roslyn ends submissions gracefully
      // when synchronous script surfaces throw, so check explicitly.)
      ct.ThrowIfCancellationRequested();

      List<string> outputLines = [.. globals.OutputLines];
      if (state.ReturnValue is not null and not ScriptGlobals)
      {
        string text = state.ReturnValue switch
        {
          string s => s,
          _ => System.Text.Json.JsonSerializer.Serialize(state.ReturnValue)
        };
        if (!string.IsNullOrEmpty(text))
        {
          outputLines.Add(text);
        }
      }

      string output = string.Join("\n", outputLines);
      return new ExecRunResult(ExecRunStatus.Completed, output, []);
    }
    catch (OperationCanceledException)
    {
      // Propagate for classification at the tool layer - see the comment above.
      throw;
    }
    // Named decision (CA1031): script faults surface as EngineFailure run results.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      string output = string.Join("\n", globals.OutputLines);
      return new ExecRunResult(ExecRunStatus.Completed, output,
          [$"Error [ScriptError]: {ex.Message}"]);
    }
    finally
    {
      globals.EndCapture();
    }
  }
}
