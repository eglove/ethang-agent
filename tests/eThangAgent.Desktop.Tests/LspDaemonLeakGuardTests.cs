using eThangAgent.Composition;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>The daemon leak guard's decision: a language-server daemon that outlived the
///     test process is a leak, and one that was already running is somebody else's.
///     <para>WHY IT EXISTS: the ChildHost guard watches one image name, so a stranded
///     daemon is invisible to it. A daemon serves a named pipe until it is stopped, and
///     the next run joins it instead of starting its own - nothing fails, and the only
///     symptom is a process table that grows by one per run.</para></summary>
public class LspDaemonLeakGuardTests
{
  private const string Daemon = LspDaemonLeakGuardFixture.DaemonImageName;

  private static readonly IReadOnlySet<int> NoBaseline = new HashSet<int>();

  private static readonly IReadOnlyList<HostProcessRecord> NoFacts = [];

  [Fact]
  public void FindLeaks_DaemonStillAlive_IsALeak()
  {
    LiveProcess[] children = [new(4242, Daemon)];

    LiveProcess leak = Assert.Single(
        ChildHostLeakGuard.FindLeaks(children, Daemon, NoBaseline, NoFacts));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_DaemonAlreadyRunningBeforeTheTests_IsNotThisRunsLeak()
  {
    LiveProcess[] children = [new(4242, Daemon), new(99, Daemon)];
    HashSet<int> baseline = [99];

    LiveProcess leak = Assert.Single(
        ChildHostLeakGuard.FindLeaks(children, Daemon, baseline, NoFacts));

    Assert.Equal(4242, leak.ProcessId);
  }

  [Fact]
  public void FindLeaks_AChildHostIsNotADaemonLeak()
  {
    LiveProcess[] children = [new(77, "eThangAgent.ChildHost")];

    Assert.Empty(ChildHostLeakGuard.FindLeaks(children, Daemon, NoBaseline, NoFacts));
  }

  [Fact]
  public void Describe_ANamedLeak_SaysNotToKillItByImageName()
  {
    LiveProcess[] leaks = [new(4242, Daemon)];

    string message = Assert.IsType<string>(ChildHostLeakGuard.Describe(leaks, Daemon, NoFacts));

    Assert.Contains("4242", message, StringComparison.Ordinal);
    Assert.Contains("Do NOT kill", message, StringComparison.Ordinal);
  }
}
