using System.Reflection;
using System.Runtime.Loader;
using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Regression tests for the exec-engine memory leak (2026-10-08 incident:
///     9 GB -> 23 GB RSS over one session). Roslyn scripting materializes a loaded
///     submission assembly as a side effect of Script.Compile() and loads another on
///     the RunAsync path — both into the DEFAULT non-collectible AssemblyLoadContext,
///     where they can never be unloaded (measured 46-81 MB live per exec call whose
///     script carries a using directive; 151 submission assemblies after ~75 calls).
///     The engine contract: a submission assembly must NEVER sit in a non-collectible
///     context. Collectible contexts are fine — they unload when the run settles —
///     and the count here deliberately EXCLUDES them: unload is asynchronous by
///     design, so a concurrent test's not-yet-reclaimed collectible context is not a
///     leak, while any non-collectible #-assembly is.</summary>
#pragma warning disable S1215 // Do not force garbage collection — the assertion is about the collectible state of retained assemblies, observable only after a forced collection
public class ExecEngineAssemblyRetentionTests
{
  private static CSharpScriptExecEngine CreateEngine()
      => new(CapabilityRegistry.Create([]),
          workspaceRoot: () => AppContext.BaseDirectory);

  /// <summary>Submission assemblies (the ℛ*...#N dynamic naming) sitting in a
  ///     NON-collectible context — the leak signature. Collectible-context
  ///     submissions are excluded: they unload when their run settles, and a
  ///     concurrent test's in-flight context is not a leak.</summary>
  private static int NonCollectibleSubmissionCount()
  {
    int count = 0;
    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
    {
      try
      {
        if (a.GetName().Name?.Contains('#', StringComparison.Ordinal) != true)
        {
          continue;
        }

        AssemblyLoadContext? ctx = AssemblyLoadContext.GetLoadContext(a);
        if (ctx is null || !ctx.IsCollectible)
        {
          count++;
        }
      }
      catch (InvalidOperationException)
      {
        // A dynamic assembly can throw while being named; not a retention signal.
      }
    }

    return count;
  }

  /// <summary>The realistic leak shape: a using directive adds a metadata reference
  ///     the default-context loader must keep alive, which is what pinned the old
  ///     engine's submissions. Kept verbatim from the incident's probe.</summary>
  private const string RealisticScript = "using System.Diagnostics; var p = Process.GetCurrentProcess(); return p.Id;";

  [Fact]
  public async Task RepeatedRealisticRuns_NeverLoadSubmissionsIntoNonCollectibleContexts()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    for (int i = 0; i < 10; i++)
    {
      ExecRunResult run = await engine.ExecuteAsync(new ExecProgram(RealisticScript), ct: TestContext.Current.CancellationToken);
      Assert.Equal(ExecRunStatus.Completed, run.Status);
      Assert.NotEmpty(run.Output);
    }

    // Give any collectible unload a chance to complete (unload is asynchronous by design).
    for (int attempt = 0; attempt < 20; attempt++)
    {
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
      if (NonCollectibleSubmissionCount() == 0)
      {
        return;
      }

      await Task.Delay(100, TestContext.Current.CancellationToken);
    }

    Assert.True(NonCollectibleSubmissionCount() == 0,
        $"{NonCollectibleSubmissionCount()} submission assemblies sit in non-collectible contexts after 10 realistic runs; " +
        "the engine must never load submissions into the default non-collectible context");
  }
}
#pragma warning restore S1215 // Do not force garbage collection
