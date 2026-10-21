using System.Reflection;
using System.Runtime.Loader;
using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Regression tests for the 2026-10-19 exec-engine memory leak
///     (15 GB working set, 9.3 MILLION retained handles, over roughly 200 exec
///     calls in one session).
///
///     <para><b>What the existing test could not see.</b>
///     <c>ExecEngineAssemblyRetentionTests</c> asserts that no submission
///     assembly lands in a NON-collectible context, and it deliberately
///     EXCLUDES collectible ones as 'not a leak'. That exclusion is exactly
///     where this leak lived: the context WAS collectible, but it was never
///     reclaimed, so the exclusion hid a context-per-call leak.</para>
///
///     <para><b>The leak.</b> A script that hands a type it DECLARED to
///     System.Text.Json - its return value, an <c>Output(...)</c> argument, or a
///     tool's argument object - makes the serializer cache that type
///     STRONGLY in its static options singleton (<c>_lastTypeInfo</c> /
///     <c>CachingContext</c>). A cached type holds its <c>m_keepalive</c>, which
///     holds the <c>LoaderAllocator</c>, which holds the collectible submission
///     assembly - so the context can never be reclaimed and the submission's
///     IL, metadata and assembly leak for the process lifetime, one per exec
///     call. The runtime's per-context handle and thread bookkeeping grows to
///     match: the 9.3 MILLION retained handles.</para>
///
///     <para><b>How these tests measure it.</b> Each script declares a type
///     with a name unique to this test class. The assertion counts the
///     collectible contexts that STILL hold an assembly declaring that type
///     after the runs settle and a forced aggressive reclaim. The unique name
///     makes the count immune to the OTHER test classes running concurrently
///     in the same process: only a submission that declared THIS marker can
///     match. Before the fix the count grows by one per call and never
///     returns to its baseline; after the fix it stays at the baseline.</para>
/// </summary>
#pragma warning disable S1215 // Do not force garbage collection - the assertion is about the RECLAIM state of the collectible contexts, observable only after a forced collection
public class ExecEngineLeakRegressionTests
{
  /// <summary>A type name unique to this test class: no other test and no
  /// other submission can declare it, so the count below measures only THIS
  /// test's own leaked contexts.</summary>
  private const string MarkerTypeName = "ExecLeakRegressionMarker_c41d7e";

  private static CSharpScriptExecEngine CreateEngine()
      => new(CapabilityRegistry.Create([]),
          workspaceRoot: () => AppContext.BaseDirectory);

  /// <summary>Counts the collectible contexts that STILL hold an assembly
  ///     declaring the marker type - the leak signature. A context that was
  ///     unloaded but never reclaimed still shows up here; a reclaimed one
  ///     does not (the whole assembly, marker type included, is gone).</summary>
  private static int LiveMarkerContexts()
  {
    HashSet<nint> contexts = [];
    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
      if (!DeclaresMarker(assembly))
      {
        continue;
      }

      AssemblyLoadContext? context = null;
      try
      {
        context = AssemblyLoadContext.GetLoadContext(assembly);
      }
      catch (InvalidOperationException)
      {
        // A dynamic assembly can throw while being named; not a retention signal.
      }

      if (context is { IsCollectible: true })
      {
        _ = contexts.Add(context.GetHashCode());
      }
    }

    return contexts.Count;
  }

  /// <summary>True when the assembly declares the marker type. The script's
  ///     type declaration is nested inside the generated submission class, so
  ///     the name is matched, not the full name.</summary>
  private static bool DeclaresMarker(Assembly assembly)
  {
    try
    {
      foreach (Type type in assembly.GetTypes())
      {
        if (string.Equals(type.Name, MarkerTypeName, StringComparison.Ordinal))
        {
          return true;
        }
      }
    }
    catch (ReflectionTypeLoadException)
    {
      // A partially loadable dynamic assembly is not this test's marker.
    }
    catch (InvalidOperationException)
    {
      // A dynamic assembly can throw while being named; not a retention signal.
    }

    return false;
  }

  /// <summary>Forces a reclaim of the collectible contexts and waits for it.
  ///     Unload is asynchronous by design: the context is reclaimed only when
  ///     the last reference dies AND a collection runs. The aggressive
  ///     compacting gen2 collect is the mode that actually reclaims an
  ///     unloaded context; the finalizer wait releases the context's
  ///     finalizable remnants; the second collect takes what they freed.</summary>
  private static async Task<int> ReclaimedMarkerContextsAsync()
  {
    for (int attempt = 0; attempt < 40; attempt++)
    {
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      GC.WaitForPendingFinalizers();
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      int live = LiveMarkerContexts();
      if (live == 0)
      {
        return 0;
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    return LiveMarkerContexts();
  }

  /// <summary>THE REGRESSION TEST, return-value vector. Ten consecutive runs
  ///     of a script that RETURNS a type it declares must leak NO collectible
  ///     context: after the runs settle and a forced reclaim, no collectible
  ///     context may still hold the marker type.
  ///
  ///     <para>Before the fix this fails with the count equal to the number of
  ///     runs: each call's serializer cached the script-declared type in a
  ///     static options singleton, pinning its collectible context forever.</para>
  /// </summary>
  [Fact]
  public async Task RepeatedRuns_ReturningAScriptDeclaredType_LeakNoCollectibleContext()
  {
    CSharpScriptExecEngine engine = CreateEngine();

    int baseline = await ReclaimedMarkerContextsAsync();

    const int runs = 10;
    for (int i = 0; i < runs; i++)
    {
      ExecRunResult run = await engine.ExecuteAsync(
          new ExecProgram(ReturnMarkerScript),
          ct: TestContext.Current.CancellationToken);
      Assert.Equal(ExecRunStatus.Completed, run.Status);
      Assert.NotEmpty(run.Output);
    }

    int leaked = await ReclaimedMarkerContextsAsync();

    Assert.True(leaked <= baseline,
        $"{leaked} collectible contexts still hold the marker type after {runs} runs " +
        $"(baseline {baseline}); each run must release its context. A context that survives a " +
        "forced aggressive collect is ROOTED - the run's return value was serialized with a " +
        "serializer that cached the script-declared type in a static options singleton, pinning " +
        "the collectible assembly for the process lifetime (the 2026-10-19 leak: 15 GB working " +
        "set, 9.3M handles).");
  }

  /// <summary>The same leak through the REAL tool path (validate + run),
  ///     which compiles the script twice and is the path the model actually
  ///     drives. It leaks no collectible context either.</summary>
  [Fact]
  public async Task RepeatedToolRuns_ReturningAScriptDeclaredType_LeakNoCollectibleContext()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecTool tool = new(engine, ExecOptions.Default, new NullExecActivitySink());

    int baseline = await ReclaimedMarkerContextsAsync();

    const int runs = 10;
    for (int i = 0; i < runs; i++)
    {
      string json = System.Text.Json.JsonSerializer.Serialize(new
      {
        program = ReturnMarkerScript,
        title = "leak probe",
        timeoutSeconds = 60,
      });
      ToolResult result = await tool.ExecuteAsync(new RawToolInput("exec", json),
          ct: TestContext.Current.CancellationToken);
      Assert.False(result.IsError, result.Content);
    }

    int leaked = await ReclaimedMarkerContextsAsync();

    Assert.True(leaked <= baseline,
        $"{leaked} collectible contexts still hold the marker type after {runs} tool runs " +
        $"(baseline {baseline}); each run must release its context.");
  }

  /// <summary>The SAME leak through the Output vector: a script that calls
  ///     <c>Output(...)</c> with a type it declares. The value is serialized
  ///     DURING the run, so the serializer cached the script-declared type
  ///     while the collectible context was still live - and pinned it for the
  ///     process lifetime. This path leaks independently of the return value,
  ///     so it needs its own coverage.</summary>
  [Fact]
  public async Task RepeatedRuns_OutputtingAScriptDeclaredType_LeakNoCollectibleContext()
  {
    CSharpScriptExecEngine engine = CreateEngine();

    int baseline = await ReclaimedMarkerContextsAsync();

    const int runs = 10;
    for (int i = 0; i < runs; i++)
    {
      ExecRunResult run = await engine.ExecuteAsync(
          new ExecProgram(OutputMarkerScript),
          ct: TestContext.Current.CancellationToken);
      Assert.Equal(ExecRunStatus.Completed, run.Status);
      Assert.NotEmpty(run.Output);
    }

    int leaked = await ReclaimedMarkerContextsAsync();

    Assert.True(leaked <= baseline,
        $"{leaked} collectible contexts still hold the marker type after {runs} runs " +
        $"(baseline {baseline}); each run must release its context. The Output path " +
        "serializes a script-declared type while the collectible context is live, so a " +
        "serializer that caches that type pins the context for the process lifetime.");
  }

  /// <summary>The THIRD leak vector, and the most common script shape there
  ///     is: a script that passes an ANONYMOUS OBJECT to a tool
  ///     (<c>Tools.Invoke("read", new { path = "x" })</c>). The arguments are
  ///     serialized by ScriptTools, so the same serializer pinned the
  ///     script-declared anonymous type - independently of the return value
  ///     and of the Output path.</summary>
  [Fact]
  public async Task RepeatedRuns_PassingAScriptDeclaredTypeToATool_LeakNoCollectibleContext()
  {
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([new EchoProvider()]),
        workspaceRoot: () => AppContext.BaseDirectory);

    int baseline = await ReclaimedMarkerContextsAsync();

    const int runs = 10;
    for (int i = 0; i < runs; i++)
    {
      ExecRunResult run = await engine.ExecuteAsync(
          new ExecProgram(ToolArgsMarkerScript),
          ct: TestContext.Current.CancellationToken);
      Assert.Equal(ExecRunStatus.Completed, run.Status);
      Assert.Empty(run.ErrorLines);
    }

    int leaked = await ReclaimedMarkerContextsAsync();

    Assert.True(leaked <= baseline,
        $"{leaked} collectible contexts still hold the marker type after {runs} runs " +
        $"(baseline {baseline}); each run must release its context. The tool-arguments path " +
        "serializes a script-declared anonymous type, so a serializer that caches that type " +
        "pins the collectible context for the process lifetime.");
  }

  /// <summary>The script that declares the marker type and RETURNS it - the
  ///     return-value vector.</summary>
  private static string ReturnMarkerScript =>
      $"public class {MarkerTypeName} {{ public int X {{ get; set; }} }}\n" +
      $"return new {MarkerTypeName} {{ X = 5 }};";

  /// <summary>The script that declares the marker type and OUTPUTS it - the
  ///     Output vector.</summary>
  private static string OutputMarkerScript =>
      $"public class {MarkerTypeName} {{ public int X {{ get; set; }} }}\n" +
      $"Output(new {MarkerTypeName} {{ X = 5 }});";

  /// <summary>The script that declares the marker type and passes an
  ///     anonymous object CONTAINING it to a tool - the tool-arguments
  ///     vector. The argument object is the anonymous type the serializer
  ///     used to cache.</summary>
  private static string ToolArgsMarkerScript =>
      $"public class {MarkerTypeName} {{ public int X {{ get; set; }} }}\n" +
      $"return Tools.Invoke(\"echo\", new {{ timeoutSeconds = 30, key = \"k\", marker = new {MarkerTypeName} {{ X = 5 }} }});";

  /// <summary>A provider exposing one action the script can call.</summary>
  private sealed class EchoProvider : ICapabilityProvider
  {
    public string Id => "echo";

    public IReadOnlyList<ActionDescriptor> Actions =>
    [
      new ActionDescriptor(
        "echo",
        "Echo",
        "Echoes the arguments.",
        [new ActionParameter("timeoutSeconds", "WholeNumber", "The budget."),
         new ActionParameter("key", "Text", "A key."),
         new ActionParameter("marker", "Text", "A marker value.")]),
    ];

    public Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
        => Task.FromResult(CapabilityInvocationResult.Ok("echoed"));
  }

  /// <summary>A sink that drops the activity records - the test measures the
  /// engine, not the activity ledger.</summary>
  private sealed class NullExecActivitySink : IExecActivitySink
  {
    public Task RecordAsync(ExecActivity activity, CancellationToken ct = default) => Task.CompletedTask;
  }
}
#pragma warning restore S1215 // Do not force garbage collection
