using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #368: container teardown waits for a retire that is already in flight.
///     <para>THE DEFECT THIS PINS. <c>RetireAsync</c> removed an agent from the live set under
///     the lock and then disposed its supervisor OUTSIDE it, so a retire in progress was
///     invisible to teardown: the entry was already gone from the only place teardown looked,
///     while the supervisor was still inside its bounded exit wait. Container disposal then
///     returned with that host alive, the test finished, and the suite's leak guard found a
///     process no supervisor had ever been recorded waiting for.</para>
///     <para>WHAT IS ASSERTED IS THE CONTRACT, NOT A DURATION: when <c>DisposeAsync</c>
///     returns, a host this runtime launched is no longer in the process table - the same
///     contract <see cref="RemoteHostSupervisorDisposalTests.DisposeAsync_DoesNotReturnUntilTheHostProcessIsGone"/>
///     pins one level down. The retire is started and NOT awaited, which is the only shape that
///     exercises the race: an awaited retire would have disposed the supervisor before teardown
///     ran, and that is the case that already worked.</para></summary>
[Collection("EnvironmentSensitive")]
public class HostLaunchingRuntimeRetireTests
{
  private static AgentSettings Settings(string workspace) => new AgentSettings(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new AgentRunOptions(null, 2))
      .WithWorkspaceRoot(workspace)
      .WithWorkspaceId(workspace);

  [Fact]
  public async Task DisposeAsync_DoesNotReturnWhileARetireIsStillDisposingTheHost()
  {
    string root = Directory.CreateTempSubdirectory("ethang-retire-race").FullName;
    bool disposed = false;
    HostLaunchingRuntime? runtime = null;
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      string database = Path.Combine(root, "app.db");
      AgentSettings settings = Settings(workspace);
      // The parent owns and migrates the schema before a host is launched: a child host is a
      // GUEST in an existing database.
      _ = new AppDatabase(database);
      // Named decision (CA2000): ownership transfers to the `runtime` local, disposed in the
      // finally below - the analyzer cannot see across the assignment to a declared local.
#pragma warning disable CA2000 // Ownership transfers to the finally below.
      runtime = new HostLaunchingRuntime(settings, database, new HostAdmissionGate(4), _ => { });
#pragma warning restore CA2000
      AgentRecord record = AgentRecord.Started(AgentId.NewId(), "mock/retire-race",
          label: "retire-race", taskPrompt: "do the work", DateTimeOffset.UtcNow,
          contract: null, provider: Providers.OpenRouter, workspaceId: workspace);

      // NOT AWAITED TO COMPLETION: the start's own settle watch retires the host the moment the
      // agent settles, and that retire is what this test races against teardown.
      Task<Result<AgentId>> started = runtime.Start(record, TestContext.Current.CancellationToken);
      int pid = await WaitForHostPidAsync(record.Id).ConfigureAwait(true);
      Assert.True(IsPresent(pid), "the launched host must be running before teardown");

      await runtime.DisposeAsync().ConfigureAwait(true);
      disposed = true;

      Assert.False(IsPresent(pid),
          $"teardown returned while host {pid} was still in the process table - a caller cannot "
          + "distinguish that from a leak.");
      _ = await started.ConfigureAwait(true);
    }
    finally
    {
      if (!disposed && runtime is not null)
      {
        await runtime.DisposeAsync().ConfigureAwait(true);
      }

      TryDelete(root);
    }
  }

  /// <summary>The pid of the host this runtime launched for <paramref name="agent"/>, read from
  ///     the ledger the supervisor records every spawn in. The pipe name it keys the record by is
  ///     derived from the agent id, so the lookup is exact. Bounded by the shared ceiling so a
  ///     launch that never happens fails this test instead of hanging the suite.</summary>
  private static async Task<int> WaitForHostPidAsync(AgentId agent)
  {
    string pipe = RemoteHostSupervisor.PipeNameForConversation(agent.Value.ToString());
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (DateTimeOffset.UtcNow < deadline)
    {
      HostProcessRecord? spawned = HostProcessLedger.Snapshot()
          .LastOrDefault(fact => string.Equals(fact.SessionId, pipe, StringComparison.Ordinal));
      if (spawned is not null)
      {
        return spawned.ProcessId;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

    throw new TimeoutException($"agent {agent} never launched a host process.");
  }

  /// <summary>Is this pid in the process table at all? A presence question, deliberately not a
  ///     liveness one - the leak guard enumerates processes, so a terminated-but-present process
  ///     still costs it an answer.</summary>
  private static bool IsPresent(int pid)
  {
    foreach (Process candidate in Process.GetProcesses())
    {
      using (candidate)
      {
        try
        {
          if (candidate.Id == pid)
          {
            return true;
          }
        }
        catch (InvalidOperationException)
        {
          // Exited between enumeration and the read: not present any more.
        }
      }
    }

    return false;
  }

  private static void TryDelete(string path)
  {
    // Named decision (CA1031): a temp-directory cleanup must never fail the test it follows.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Directory.Delete(path, recursive: true);
    }
    catch
    {
      // Left behind deliberately: a host still releasing its SQLite handle is the transient.
    }
#pragma warning restore CA1031
  }
}
