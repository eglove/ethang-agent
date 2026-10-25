using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using eThangAgent.StateDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Runs evidence-check scripts (simple boolean expressions over the
///     workspace) via the same collectible-ALC contract as the exec engine:
///     validation reads GetCompilation().GetDiagnostics() (pure analysis —
///     Script.Compile() materializes a loaded submission assembly in the default
///     non-collectible context as a side effect, the 2026-10-08 leak), the run
///     emits into a per-run collectible AssemblyLoadContext and invokes the entry
///     point directly, and the context unloads when the check settles.</summary>
public sealed class CSharpEvidenceRunner(EvidenceOptions options) : IEvidenceRunner
{
  private readonly EvidenceOptions _options = options ?? EvidenceOptions.Default;

  private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
      .AddImports("System", "System.IO")
      .AddReferences(typeof(CSharpEvidenceRunner).Assembly);

  public async Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
  {
    AssemblyLoadContext? alc = null;
    try
    {
      using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(_options.Timeout);

      Script<bool> script = CSharpScript.Create<bool>(command, ScriptOpts);
      Compilation compilation = script.GetCompilation();
      ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics(cts.Token);
      if (HasErrors(diagnostics))
      {
        // Retry as a statement: bare statements like "throw new ..." need a
        // trailing semicolon, while expressions must stay as-is.
        Script<bool> asStatement = CSharpScript.Create<bool>(command + ";", ScriptOpts);
        Compilation statementCompilation = asStatement.GetCompilation();
        ImmutableArray<Diagnostic> statementDiagnostics = statementCompilation.GetDiagnostics(cts.Token);
        if (!HasErrors(statementDiagnostics))
        {
          compilation = statementCompilation;
          diagnostics = statementDiagnostics;
        }
        else
        {
          diagnostics = statementDiagnostics;
        }
      }

      List<Diagnostic> errors = [.. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
      if (errors.Count > 0)
      {
        return new EvidenceResult(command, false,
            string.Join("\n", errors.Select(d => d.GetMessage(CultureInfo.InvariantCulture))));
      }

      // The native-interop gate (issue #282) applies here too: the evidence runner is a
      // SECOND independent compile path over the same Roslyn surface, so leaving it open
      // would make the gate a one-attribute-change bypass (store a native call as a
      // goal/check command and the evidence runner would compile and run it). Same
      // compilation, no extra compile, no assembly loaded.
      if (NativeInteropPolicy.Analyze(compilation, cts.Token) is { Count: > 0 } interopViolations)
      {
        return new EvidenceResult(command, false,
            string.Join("\n", interopViolations.Select(v => v.Message)));
      }

      IMethodSymbol? entryPoint = compilation.GetEntryPoint(cts.Token);
      if (entryPoint is null)
      {
        return new EvidenceResult(command, false, "Expression has no entry point.");
      }

      MemoryStream pe = new();
      EmitResult emit = compilation.Emit(pe, options: null, cancellationToken: cts.Token);
      if (!emit.Success)
      {
        return new EvidenceResult(command, false,
            string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage(CultureInfo.InvariantCulture))));
      }

      pe.Position = 0;
      alc = new AssemblyLoadContext("eThangAgent-evidence-" + Guid.NewGuid().ToString("N"), isCollectible: true);
      Assembly submissionAssembly = alc.LoadFromStream(pe);
      string entryTypeName = entryPoint.ContainingType.MetadataName;
      Type entryType = submissionAssembly.GetType(entryTypeName, throwOnError: true)!;
      // Named decision (S3011): the generated entry point is deliberately non-public —
      // reading it IS the submission contract (Roslyn's own ScriptBuilder does the same).
#pragma warning disable S3011 // Make sure that this accessibility bypass is safe here
      MethodInfo entryMethod = entryType.GetMethod(entryPoint.MetadataName,
          BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
          | BindingFlags.Instance | BindingFlags.DeclaredOnly)
          ?? throw new MissingMethodException(entryTypeName, entryPoint.MetadataName);
#pragma warning restore S3011 // Make sure that this accessibility bypass is safe here
      Func<object[], Task<bool>> executor =
          entryMethod.CreateDelegate<Func<object[], Task<bool>>>();

      object[] submissionStates = new object[2]; // globals slot (null — no globals type) + the submission slot the factory fills.
      bool passed = await executor(submissionStates).ConfigureAwait(false);
      return passed
          ? new EvidenceResult(command, true, string.Empty)
          : new EvidenceResult(command, false, "Expression evaluated to false.");
    }
    catch (OperationCanceledException)
    {
      return new EvidenceResult(command, false, "Timed out after " + _options.Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s.");
    }
    // Named decision (CA1031): a failed check is a failed check — the fault text
    // becomes the failure message so state.verify fails closed, never crashes.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return new EvidenceResult(command, false, ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      // Unload the collectible context once the check settles — same contract as
      // the exec engine (unload is asynchronous by design).
      alc?.Unload();
    }
  }

  private static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
      => diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
