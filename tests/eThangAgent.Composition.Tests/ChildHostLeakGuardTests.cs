using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>The ChildHost leak guard's decision (issue #284 item 3, sharpened by issue
///     #367): which child processes count as leaks, and what the failure says. Pure policy
///     over fakes - the questions worth pinning are all 'does this candidate COUNT', and
///     none needs a real process.
///     <para>THE guard matters because a leaked host is invisible: no test fails, and the
///     only symptom is wall-clock time while the survivor holds its SQLite file (measured
///     during #271: over 4 minutes with a leaked host, 1m15s once killed).</para>
///     <para>#367's defect: the guard reported a PRESENCE and named both possible causes, so
///     a host whose kill had been requested and had not completed read exactly like a test
///     that never disposed - and the only way to tell them apart was to inspect the pid by
///     hand, which is the manual step the guard exists to remove. The supervisor now records
///     every host it starts and every host that outlived its bounded exit wait, and the guard
///     reads those facts: a host the supervisor waited for is a slow reap, a host it only
///     started is a leak, and the message names the session either way.</para></summary>
public class ChildHostLeakGuardTests
{
  private const string Host = "eThangAgent.ChildHost";

  private static readonly IReadOnlySet<int> NoBaseline = new HashSet<int>();

  private static readonly IReadOnlyList<HostProcessRecord> NoFacts = [];

  private static HostProcessRecord Spawn(int pid) =>
      new("session-" + pid, @"C:\work", pid, Waited: null);

  private static HostProcessRecord Reaping(int pid) =>
      new("session-" + pid, @"C:\work", pid, TimeSpan.FromSeconds(10));

  [Fact]
  public void FindLeaks_ChildHostProcess_IsALeak()
  {
    LiveProcess[] children = [new(4242, Host)];

    LiveProcess leak = Assert.Single(
        ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, NoFacts));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_PreExistingChildHost_IsNotThisRunsLeak()
  {
    // A host that existed before the tests started is somebody else's: failing on it
    // would break an innocent run on any machine with an unrelated long-lived host.
    LiveProcess[] children = [new(4242, Host), new(99, Host)];
    HashSet<int> baseline = [99];

    LiveProcess leak = Assert.Single(
        ChildHostLeakGuard.FindLeaks(children, Host, baseline, NoFacts));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_HostTheSupervisorWaitedFor_IsNotALeak()
  {
    // Issue #367's whole point: the supervisor asked this host to exit and is still waiting
    // for it. That is a slow reap on a loaded machine, not a test that skipped its disposal,
    // and failing the run for it sends the reader to fix correct code.
    LiveProcess[] children = [new(4242, Host)];

    Assert.Empty(ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, [Reaping(4242)]));
  }

  [Fact]
  public void FindLeaks_AHostTheSupervisorOnlyStarted_IsStillALeak()
  {
    // Recording a spawn must not excuse the host: the supervisor started it and never asked
    // it to exit, which is exactly the leak. Only a RECORDED WAIT separates the two.
    LiveProcess[] children = [new(4242, Host)];

    LiveProcess leak = Assert.Single(
        ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, [Spawn(4242)]));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_WaitedHostDoesNotHideAnUnwaitedOne()
  {
    // The waited host must be excluded BY PID, not by image: one slow reap must never absorb
    // a genuine leak that happens to share the image name.
    LiveProcess[] children = [new(4242, Host), new(5353, Host)];

    LiveProcess leak = Assert.Single(
        ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, [Reaping(4242), Spawn(5353)]));

    Assert.Equal(5353, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_OtherProcesses_AreNotLeaks()
  {
    // Straight from the measurement: the harness runs every conversation in a ChildHost, and
    // the guard sees only THIS process's children - so a sibling testhost, a broker, or a
    // build server is never blamed.
    LiveProcess[] children =
    [
        new(1, "eThangAgent.ComputerUse.Host"),
        new(2, "testhost"),
        new(3, "eThangAgent.Desktop"),
        new(4, "dotnet"),
    ];

    Assert.Empty(ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, NoFacts));
  }

  [Fact]
  public void FindLeaks_ImageNameMatch_IsCaseInsensitive()
  {
    // ProcessName's casing is an OS detail, not a contract: a guard that required exact case
    // would silently stop guarding the day the image name's casing changed.
    LiveProcess[] children = [new(7, "ethangagent.childhost")];

    _ = Assert.Single(ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, NoFacts));
  }

  [Fact]
  public void FindLeaks_CleanRun_ReportsNothing() =>
      Assert.Empty(ChildHostLeakGuard.FindLeaks([], Host, NoBaseline, NoFacts));

  [Fact]
  public void FindLeaks_MultipleLeaks_AreOrderedByPid_SoTheMessageIsStable()
  {
    LiveProcess[] children = [new(300, Host), new(100, Host), new(200, Host)];

    int[] pids = [.. ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline, NoFacts)
        .Select(l => l.ProcessId)];

    Assert.Equal([100, 200, 300], pids);
  }

  [Fact]
  public void Describe_CleanRun_IsNull() =>
      Assert.Null(ChildHostLeakGuard.Describe([], Host, NoFacts));

  [Fact]
  public void Describe_NamesEachLeakedPid_AndSaysWhatToDo()
  {
    LiveProcess[] leaks = [new(4242, Host), new(5353, Host)];

    string message = ChildHostLeakGuard.Describe(leaks, Host, NoFacts)!;

    // A guard whose failure reads only as 'a test failed' costs the same investigation it
    // exists to avoid, so the message must carry the facts that end the search.
    Assert.Contains("4242", message, StringComparison.Ordinal);
    Assert.Contains("5353", message, StringComparison.Ordinal);
    Assert.Contains("2 " + Host, message, StringComparison.Ordinal);
    Assert.Contains("IAsyncLifetime.DisposeAsync", message, StringComparison.Ordinal);
  }

  [Fact]
  public void Describe_NamesTheCause_WhenNothingEverAskedTheHostToExit()
  {
    // Issue #367: the old message named both causes and left the reader to choose. A host no
    // supervisor waited for had no kill requested for it, so the cause is not in doubt.
    string message = ChildHostLeakGuard.Describe([new(4242, Host)], Host, NoFacts)!;

    Assert.Contains("never disposed", message, StringComparison.Ordinal);
    Assert.Contains("No supervisor recorded a host awaiting its exit", message, StringComparison.Ordinal);
  }

  [Fact]
  public void Describe_NamesTheSessionThatOwnedALeakedHost()
  {
    // The fact a process table cannot carry: which SESSION the survivor belonged to. That is
    // what turns 'pid 4242 is alive' into 'this test's session was never disposed'.
    string message = ChildHostLeakGuard.Describe([new(4242, Host)], Host, [Spawn(4242)])!;

    Assert.Contains("session-4242", message, StringComparison.Ordinal);
    Assert.Contains(@"C:\work", message, StringComparison.Ordinal);
  }

  [Fact]
  public void Describe_ReportsWaitedHostsAsSlowReaps_NotAsLeaks()
  {
    // A waited host is reported for what it is: killed, still being reaped, and named by
    // session and wait so the reader learns what the machine did.
    string message = ChildHostLeakGuard.Describe([new(4242, Host)], Host, [Reaping(99)])!;

    Assert.Contains("slow reaps, not leaks", message, StringComparison.Ordinal);
    Assert.Contains("pid 99", message, StringComparison.Ordinal);
    Assert.Contains("session-99", message, StringComparison.Ordinal);
  }

  [Fact]
  public void Describe_NeverInstructsAKill()
  {
    // README: a host process is a live session - the user's, another agent's, or the one
    // running the command - and killing by image name takes all of them. A guard that told the
    // reader to kill the pids would be instructing the one forbidden action.
    string message = ChildHostLeakGuard.Describe([new(4242, Host)], Host, NoFacts)!;

    Assert.Contains("Do NOT kill", message, StringComparison.Ordinal);
    Assert.Contains("Surface the pids", message, StringComparison.Ordinal);
    Assert.DoesNotContain("kill the pids above", message, StringComparison.OrdinalIgnoreCase);
  }
}
