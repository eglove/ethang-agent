using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Verification;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;

namespace eThangAgent.Roslyn.ACL;

public sealed class CSharpScriptExecEngine(Func<ICapabilityRegistry> registry,
    Func<string>? workspaceRoot = null, Func<IVerificationLedger>? verificationSink = null) : IExecEngine
{
  private readonly Func<ICapabilityRegistry> _registry = registry ?? throw new ArgumentNullException(nameof(registry));
  private readonly Func<string> _workspaceRoot = workspaceRoot ?? ThrowMissingWorkspace;
  private readonly Func<IVerificationLedger>? _verificationSink = verificationSink;

  private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
      .AddImports("System", "System.IO", "System.Linq",
          "System.Collections.Generic", "System.Diagnostics",
          "System.Text", "System.Text.RegularExpressions")
      .AddReferences(typeof(ScriptGlobals).Assembly);

  public CSharpScriptExecEngine(ICapabilityRegistry registry,
      Func<string>? workspaceRoot = null, Func<IVerificationLedger>? verificationSink = null)
      : this(() => registry, workspaceRoot, verificationSink) { }

  /// <summary>Executes against the ambient workspace identity when the host supplies
  ///     none. The scripts' globals must name the agent's own workspace root; without
  ///     an injected resolver there is no honest answer, so this fails loudly instead
  ///     of silently adopting the process-wide current directory.</summary>
  private static string ThrowMissingWorkspace() => throw new InvalidOperationException(
      "No workspace resolver was provided to the exec engine; scripts cannot resolve 'Workspace'. " +
      "Supply one at composition (the session's IWorkspaceContext).");

  // THE LEAK CONTRACT (2026-10-08 incident, 9 GB -> 23 GB RSS over one session):
  // Roslyn scripting materializes a loaded submission assembly as a SIDE EFFECT of
  // Script.Compile(), and loads another through the default InteractiveAssemblyLoader
  // on the RunAsync path — both into the DEFAULT non-collectible AssemblyLoadContext,
  // where they can never be unloaded. A realistic script (one carrying a using
  // directive) pins its metadata through that assembly: measured 46-81 MB live heap
  // per exec call, 151 submission assemblies after ~75 calls. So this engine NEVER
  // calls Script.Compile() and NEVER lets Roslyn load the submission: validation
  // reads GetCompilation().GetDiagnostics() (pure analysis, no assembly), and the run
  // path emits the compilation into a per-run COLLECTIBLE AssemblyLoadContext and
  // invokes the compiled entry point directly — the same
  // GetEntryPoint -> CreateDelegate<Func<object[], Task<T>>> contract Roslyn's own
  // ScriptBuilder.CreateExecutor uses (the entry point is the generated <Factory>,
  // which news the submission and runs <Initialize>; ScriptExecutionState puts the
  // globals at index 0 and the factory stores the submission instance into the next
  // slot itself). When the run settles, the context unloads and the submission's
  // assembly, metadata, and IL return to the OS.

  public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
      ExecProgram program, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(program);
    Script<object> script = CSharpScript.Create(program.Text, ScriptOpts, typeof(ScriptGlobals));
    ImmutableArray<Diagnostic> diagnostics = script.GetCompilation().GetDiagnostics(ct);
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
    // synchronous script surfaces (Shell). No engine-side budget is imposed: the
    // exec tool layer is the sole authority, enforced by the tool layer, which also
    // owns classification (Error [ToolTimeout]). The stated budget flows to the
    // globals so nested tool calls can inherit it when they omit timeoutSeconds.
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    ArgumentNullException.ThrowIfNull(program);

    string launchDirectory = Directory.GetCurrentDirectory();
    ScriptGlobals globals = BuildGlobals(program, cts);
    Script<object> script = CSharpScript.Create(program.Text, ScriptOpts, typeof(ScriptGlobals));

    globals.BeginCapture();
    AssemblyLoadContext? alc = null;
    try
    {
      Compilation compilation = script.GetCompilation();
      if (CompileFailure(compilation, ct) is { } compileFailure)
      {
        return compileFailure;
      }

      IMethodSymbol? entryPoint = compilation.GetEntryPoint(ct);

      // A script with no executable top-level code (declarations only) has no entry
      // point: Roslyn's own executor collapses to Task.FromResult(default) — parity
      // here is a completed run with empty output.
      if (entryPoint is null)
      {
        return NoEntryPointResult(globals, launchDirectory);
      }

      MemoryStream pe = new();
      if (EmitFailure(compilation, pe, globals, launchDirectory, ct) is { } emitFailure)
      {
        return emitFailure;
      }

      pe.Position = 0;
      alc = new AssemblyLoadContext("eThangAgent-exec-" + Guid.NewGuid().ToString("N"), isCollectible: true);
      Assembly submissionAssembly = alc.LoadFromStream(pe);
      Func<object[], Task<object>> executor = BindExecutor(submissionAssembly, entryPoint);

      object? returnValue = await RunSubmission(executor, globals, cts, ct).ConfigureAwait(false);

      // An OCE thrown from synchronous script surfaces (Shell killed by the budget or
      // a user stop) does NOT propagate as a fault: check the token explicitly and
      // classify the outcome rather than trusting the completion shape.
      ct.ThrowIfCancellationRequested();

      return CompletedResult(returnValue, globals, launchDirectory);
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
      return FaultResult(ex, globals, launchDirectory);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      globals.EndCapture();
      UnloadSubmission(alc);
    }
  }

  private ScriptGlobals BuildGlobals(ExecProgram program, CancellationTokenSource cts) =>
      new(_registry(),
          _workspaceRoot(),
          Path.GetTempPath(),
          shellToken: cts.Token,
          verificationSink: _verificationSink?.Invoke(),
          execBudget: program.Budget);

  /// <summary>Compile-diagnostic failure result, or null when the compilation is
  ///     clean. Validation reads GetDiagnostics only — pure analysis, never
  ///     Script.Compile() (the leak contract above).</summary>
  private static ExecRunResult? CompileFailure(Compilation compilation, CancellationToken ct)
  {
    ImmutableArray<Diagnostic> compileDiagnostics = compilation.GetDiagnostics(ct);
    List<Diagnostic> compileErrors = [.. compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
    return compileErrors.Count == 0
        ? null
        : new ExecRunResult(ExecRunStatus.Completed, string.Empty,
            [.. compileErrors.Select(d => d.GetMessage(CultureInfo.InvariantCulture))]);
  }

  private ExecRunResult NoEntryPointResult(ScriptGlobals globals, string launchDirectory) =>
      new(ExecRunStatus.Completed, string.Empty, [],
          NestedDispatchCount: globals.NestedDispatchCount,
          WorkspaceRoot: _workspaceRoot(), LaunchDirectory: launchDirectory);

  /// <summary>Emit-diagnostic failure result, or null when the PE image emitted
  ///     cleanly. <paramref name="pe"/> is left at position 0 for the caller.</summary>
  private ExecRunResult? EmitFailure(
      Compilation compilation, MemoryStream pe, ScriptGlobals globals, string launchDirectory, CancellationToken ct)
  {
    EmitResult emit = compilation.Emit(pe, options: null, cancellationToken: ct);
    if (emit.Success)
    {
      return null;
    }

    List<string> emitErrors = [.. emit.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => d.GetMessage(CultureInfo.InvariantCulture))];
    return new ExecRunResult(ExecRunStatus.Completed, string.Empty, emitErrors,
        NestedDispatchCount: globals.NestedDispatchCount,
        WorkspaceRoot: _workspaceRoot(), LaunchDirectory: launchDirectory);
  }

  /// <summary>Resolves the entry point exactly as ScriptBuilder.GetEntryPointRuntimeMethod
  ///     does: the containing type's metadata name (scripts live in the global
  ///     namespace) and the method's metadata name.</summary>
  private static Func<object[], Task<object>> BindExecutor(Assembly submissionAssembly, IMethodSymbol entryPoint)
  {
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
    return entryMethod.CreateDelegate<Func<object[], Task<object>>>();
  }

  /// <summary>Runs the compiled submission off the caller's synchronization context
  ///     and races it against the caller's cancellation token.</summary>
  private static async Task<object?> RunSubmission(
      Func<object[], Task<object>> executor, ScriptGlobals globals, CancellationTokenSource cts, CancellationToken ct)
  {
    // Scripts are synchronous model-authored code; they may legitimately block
    // (Tools.Invoke, Shell). Schedule the submission on the worker pool with the
    // caller's SynchronizationContext shed INSIDE the delegate: Task.Run alone still
    // FLOWS the ambient SynchronizationContext (.NET 6+), which would make every
    // internal await post back to a pump that synchronous script code may be
    // blocking. Shedding inside keeps every continuation on pool threads, never on
    // a UI pump — while ExecutionContext (AsyncLocal ambient state such as
    // SubAgentSpawner.RunningChild) still flows: suppressing it broke
    // agent.spawn parent resolution from inside exec scripts (2026-10-04 incident).
    Task<object?> scheduled = Task.Run(async () =>
    {
      SynchronizationContext? callerContext = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(null);
      try
      {
        // First-submission states layout: index 0 is the globals; the generated
        // <Factory> stores the submission instance into the next slot itself.
        object[] submissionStates = new object[2];
        submissionStates[0] = globals;
        return (object?)await executor(submissionStates).ConfigureAwait(false);
      }
      finally
      {
        SynchronizationContext.SetSynchronizationContext(callerContext);
      }
    }, cts.Token);

    // The await races the submission against the caller's token: script cancellation
    // is cooperative (a script stuck in synchronous IL — a tight spin, a blocking
    // call that ignores the token — cannot be interrupted). Awaiting the submission
    // directly would wedge the turn busy forever despite a user stop.
    // Named decision: on a token win the submission task is abandoned, not
    // awaited — the spinning thread and its script object graph stay alive until
    // process exit (bounded by the pool, not accumulated per call). The turn
    // settling is worth more than the leak; the budget path already tolerated it.
    // Event-driven cancel arm (doctrine R5.1: no timed waits): the token
    // completes a one-shot task; WhenAny races it against the submission.
    TaskCompletionSource<object?> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using (cts.Token.Register(
        static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), cancelled).ConfigureAwait(false))
    {
      Task completed = await Task.WhenAny(scheduled, cancelled.Task).ConfigureAwait(false);
      if (completed != scheduled)
      {
        throw new OperationCanceledException(ct);
      }
    }

    // scheduled completed here, so this await resumes synchronously. The entry-point
    // delegate's result IS the script's return value (the <Initialize> task's result —
    // no ScriptState wrapper exists on this path).
    return await scheduled.ConfigureAwait(false);
  }

  private ExecRunResult CompletedResult(object? returnValue, ScriptGlobals globals, string launchDirectory)
  {
    List<string> outputLines = [.. globals.OutputLines];
    if (returnValue is not null and not ScriptGlobals)
    {
      // The projection (ScriptValueJson) is what keeps the collectible
      // submission assembly reclaimable: System.Text.Json must never see a type
      // declared in the submission - it would cache that type in a static
      // options singleton and pin the whole context for the process lifetime
      // (the 2026-10-19 leak: 15 GB working set, 9.3M handles).
      string text = ScriptValueJson.Serialize(returnValue);
      if (!string.IsNullOrEmpty(text))
      {
        outputLines.Add(text);
      }
    }

    string output = string.Join("\n", outputLines);
    return new ExecRunResult(ExecRunStatus.Completed, output, [],
        NestedDispatchCount: globals.NestedDispatchCount,
        WorkspaceRoot: _workspaceRoot(), LaunchDirectory: launchDirectory);
  }

  private ExecRunResult FaultResult(Exception ex, ScriptGlobals globals, string launchDirectory)
  {
    string output = string.Join("\n", globals.OutputLines);
    // No engine-side [ScriptError] tag: ScriptToolException messages already carry
    // their Error [CODE] tag, and the result formatter owns the "exec error
    // [ScriptError]:" gutter — tagging here too double-wraps the surface.
    return new ExecRunResult(ExecRunStatus.Completed, output, [ex.Message],
        NestedDispatchCount: globals.NestedDispatchCount,
        WorkspaceRoot: _workspaceRoot(), LaunchDirectory: launchDirectory);
  }

  /// <summary>Unloads the collectible context once the run settles: the submission's
  ///     assembly, metadata, and IL return to the OS. Unload is asynchronous by
  ///     design — it marks the context; collection reclaims it when the last
  ///     reference dies (an abandoned spin keeps its context alive, bounded by
  ///     the pool — the named decision above).</summary>
  private static void UnloadSubmission(AssemblyLoadContext? alc)
  {
    alc?.Unload();
    // Only a run whose ALC was created needs the drain: a compile-only
    // failure path (or a no-entry-point script) never allocated one, so
    // there is nothing unloaded to reclaim.
    if (alc is not null)
    {
      DrainExecGarbage();
    }
  }
  // THE DRAIN CONTRACT (2026-10-17 incident, 10-12 GB working set and ~7M handles
  // over one exec-heavy session): alc.Unload() only MARKS the collectible context
  // for unload — the submission's assembly, metadata, and IL stay alive until the
  // GC next collects, and workstation GC under steady allocation defers gen2
  // collection indefinitely. Per-exec compilations therefore accumulated between
  // gen2 GCs (measured: the managed heap stayed ~200 MB while native memory and
  // transient ALC handles grew 1-2k per exec call, 7M handles over hours).
  //
  // Named decision — GC.Collect is normally a smell; here it is the correct tool:
  // the engine KNOWS it just created a short-lived collectible ALC whose unload
  // must complete before the next exec starts, and no other component can know
  // when to collect on its behalf. The collect is targeted (generation 2,
  // aggressive, blocking, compacting — the mode that actually reclaims unloaded
  // ALCs), and it costs tens of milliseconds against an exec call that already
  // costs hundreds. The finalizer wait plus the second collect release the
  // context's finalizable remnants so the next exec starts from a clean slate.
  private static void DrainExecGarbage()
  {
    // Named decision (S1215): see the drain contract above — the deliberate,
    // documented exception. The engine just unloaded a collectible ALC it created;
    // a targeted blocking collect is the only way that unload completes promptly.
#pragma warning disable S1215 // Do not force garbage collection
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect();
#pragma warning restore S1215 // Do not force garbage collection
  }
}
