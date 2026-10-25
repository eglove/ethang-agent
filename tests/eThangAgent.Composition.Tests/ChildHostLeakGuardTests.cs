using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>The ChildHost leak guard's decision (issue #284 item 3): which child processes
///     count as leaks, and what the failure says. Pure policy over fakes - the questions
///     worth pinning are all 'does this candidate COUNT', and none needs a real process.
///     <para>THE guard matters because a leaked host is invisible: no test fails, and the
///     only symptom is wall-clock time while the survivor holds its SQLite file (measured
///     during #271: over 4 minutes with a leaked host, 1m15s once killed).</para></summary>
public class ChildHostLeakGuardTests
{
  private const string Host = "eThangAgent.ChildHost";

  private static readonly IReadOnlySet<int> NoBaseline = new HashSet<int>();

  [Fact]
  public void FindLeaks_ChildHostProcess_IsALeak()
  {
    LiveProcess[] children = [new(4242, Host)];

    LiveProcess leak = Assert.Single(ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_PreExistingChildHost_IsNotThisRunsLeak()
  {
    // A host that existed before the tests started is somebody else's: failing on it
    // would break an innocent run on any machine with an unrelated long-lived host.
    LiveProcess[] children = [new(4242, Host), new(99, Host)];
    HashSet<int> baseline = [99];

    LiveProcess leak = Assert.Single(ChildHostLeakGuard.FindLeaks(children, Host, baseline));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_OtherProcesses_AreNotLeaks()
  {
    // Straight from the measurement: the harness runs every conversation in a ChildHost,
    // and the guard sees only THIS process's children - so a sibling testhost, a broker,
    // or a build server is never blamed.
    LiveProcess[] children =
    [
        new(1, "eThangAgent.ComputerUse.Host"),
        new(2, "testhost"),
        new(3, "eThangAgent.Desktop"),
        new(4, "dotnet"),
    ];

    Assert.Empty(ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline));
  }

  [Fact]
  public void FindLeaks_ImageNameMatch_IsCaseInsensitive()
  {
    // ProcessName's casing is an OS detail, not a contract: a guard that required exact
    // case would silently stop guarding the day the image name's casing changed.
    LiveProcess[] children = [new(7, "ethangagent.childhost")];

    _ = Assert.Single(ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline));
  }

  [Fact]
  public void FindLeaks_CleanRun_ReportsNothing() =>
      Assert.Empty(ChildHostLeakGuard.FindLeaks([], Host, NoBaseline));

  [Fact]
  public void FindLeaks_MultipleLeaks_AreOrderedByPid_SoTheMessageIsStable()
  {
    LiveProcess[] children = [new(300, Host), new(100, Host), new(200, Host)];

    int[] pids = [.. ChildHostLeakGuard.FindLeaks(children, Host, NoBaseline).Select(l => l.ProcessId)];

    Assert.Equal([100, 200, 300], pids);
  }

  [Fact]
  public void Describe_CleanRun_IsNull() =>
      Assert.Null(ChildHostLeakGuard.Describe([], Host));

  [Fact]
  public void Describe_NamesEachLeakedPid_AndSaysWhatToDo()
  {
    LiveProcess[] leaks = [new(4242, Host), new(5353, Host)];

    string message = ChildHostLeakGuard.Describe(leaks, Host)!;

    // A guard whose failure reads only as 'a test failed' costs the same investigation
    // it exists to avoid, so the message must carry the facts that end the search.
    Assert.Contains("4242", message, StringComparison.Ordinal);
    Assert.Contains("5353", message, StringComparison.Ordinal);
    Assert.Contains("2 " + Host, message, StringComparison.Ordinal);
    Assert.Contains("IAsyncLifetime.DisposeAsync", message, StringComparison.Ordinal);
    Assert.Contains("holds its SQLite file", message, StringComparison.Ordinal);
  }
}
