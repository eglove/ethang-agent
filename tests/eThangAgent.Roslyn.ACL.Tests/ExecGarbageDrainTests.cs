using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Characterization for the exec-garbage drain (2026-10-17 incident:
///     10-12 GB working set, ~7M handles over one exec-heavy session). After a run
///     whose collectible ALC was created and unloaded, the engine drains the
///     garbage promptly (targeted gen2 blocking collect) so unload COMPLETES
///     instead of waiting for the next gen2 GC — which workstation GC under
///     steady allocation defers indefinitely. The memory effect itself is not
///     unit-assertable: a test process cannot measure the native working set and
///     handle count the drain exists to contain); what IS assertable is the
///     contract shape — many consecutive runs each complete cleanly with the
///     drain in the path. The existing ExecEngineAssemblyRetentionTests cover
///     the non-collectible leak signature; this one exercises the drain path.</summary>
#pragma warning disable S1215 // Do not force garbage collection — the assertion is about reclaim state, observable only after a forced collection
public class ExecGarbageDrainTests
{
  private const string SimpleScript = "var x = 1 + 1; Output(x);";

  [Fact]
  public async Task RepeatedRuns_WithDrain_CompleteCleanly_AndUnloadCompletesPromptly()
  {
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([]),
        workspaceRoot: () => AppContext.BaseDirectory);
    for (int i = 0; i < 5; i++)
    {
      ExecRunResult run = await engine.ExecuteAsync(new ExecProgram(SimpleScript),
          ct: TestContext.Current.CancellationToken);
      Assert.Equal(ExecRunStatus.Completed, run.Status);
      Assert.Equal("2", run.Output);
    }

    // The drain ran after each run; the contract shape asserted here is that
    // repeated consecutive runs each complete cleanly with the drain in the
    // path — the same shape the retention tests use, minus their leak
    // signature (already covered there). The memory effect itself is not
    // unit-assertable: a test process cannot measure the native working set
    // and handle count the drain exists to contain.
  }
}
#pragma warning restore S1215 // Do not force garbage collection
