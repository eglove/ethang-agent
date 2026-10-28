using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>Criterion 9, end to end against REAL processes: killing one agent's host leaves every
///     other agent running.
///     <para>This is the criterion that cannot be proved by reading code, and it is the whole
///     point of one process per agent: before #305 a hung agent shared a process with its
///     siblings, so killing it killed them. Each agent now owns a host keyed by its own id,
///     with its own job object — so there is no shared object to take a sibling down with.</para>
///     <para>The test launches two real hosts through the same supervisor the runtime uses,
///     kills one, and asserts the other is still alive. It asserts on PROCESS LIVENESS, not on
///     a handle or a flag, because 'still running' is exactly the claim being made.</para></summary>
public class AgentHostKillIsolationTests
{
  [Fact]
  public async Task KillingOneAgentsHost_LeavesTheOtherRunning()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string root = Directory.CreateTempSubdirectory("ethang-kill-isolation").FullName;
    try
    {
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
          new SubAgentOptions(null, 2)).WithWorkspaceRoot(workspace);
      string database = Path.Combine(root, "app.db");

      // Two agents, each with its own identity — exactly what HostLaunchingRuntime does per start.
      Guid agentA = Guid.NewGuid();
      Guid agentB = Guid.NewGuid();
      RemoteHostSupervisor hostA = NewHost(agentA, root, settings, database);
      RemoteHostSupervisor hostB = NewHost(agentB, root, settings, database);
      try
      {
        // Attach both: this launches two REAL host processes and connects to each.
        _ = await hostA.AttachAsync(new RemoteAgentRuntime(), ct).ConfigureAwait(true);
        _ = await hostB.AttachAsync(new RemoteAgentRuntime(), ct).ConfigureAwait(true);

        int? pidA = hostA.HostProcessId;
        int? pidB = hostB.HostProcessId;
        _ = Assert.NotNull(pidA);
        _ = Assert.NotNull(pidB);
        // Criterion 1: two agents, two processes. Distinct pids is the whole claim.
        Assert.NotEqual(pidA, pidB);

        // Kill ONE agent's host.
        hostA.StopHost();

        // Criterion 9: the survivor is still running.
        Assert.True(IsAlive(pidB.Value), "killing one agent's host must leave the other agent running.");
        Assert.False(IsAlive(pidA.Value), "the killed agent's host must actually be gone.");
      }
      finally
      {
        await hostA.DisposeAsync().ConfigureAwait(true);
        await hostB.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      TryDelete(root);
    }
  }

  /// <summary>Builds a supervisor the way <see cref="HostLaunchingRuntime"/> does: keyed by the
  ///     AGENT's id for both the pipe name and the scratch folder.</summary>
  private static RemoteHostSupervisor NewHost(Guid agent, string root, AgentSettings settings, string database)
  {
    string identity = agent.ToString();
    string scratch = Path.Combine(root, RemoteHostSupervisor.ScratchFolderForConversation(identity));
    return new RemoteHostSupervisor(identity, scratch, settings, database, _ => { });
  }

  private static bool IsAlive(int pid)
  {
    try
    {
      using Process process = Process.GetProcessById(pid);
      return !process.HasExited;
    }
    catch (ArgumentException)
    {
      // No such process: it exited and was reaped.
      return false;
    }
  }

  private static void TryDelete(string path)
  {
    // Named decision (CA1031): a temp-directory cleanup must never fail the test it follows —
    // a host that is still releasing its SQLite handle is the expected transient.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Directory.Delete(path, recursive: true);
    }
    catch (Exception)
    {
      // Left behind deliberately: a passing test must not fail on cleanup.
    }
#pragma warning restore CA1031
  }
}
