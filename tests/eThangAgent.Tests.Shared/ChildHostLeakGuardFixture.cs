using eThangAgent.Composition;
using Xunit;

namespace eThangAgent.TestSupport;

/// <summary>The assembly-level guard that turns 'a leaked ChildHost silently degrades the
///     suite' into a NAMED failure (issue #284 item 3).
///     <para>The defect it exists for: a test that opens a session and throws before its
///     teardown leaves a real eThangAgent.ChildHost.exe running. The leak is INVISIBLE -
///     no test fails, `dotnet test` reports normal progress, and the only symptom is
///     wall-clock time, because the survivor holds its SQLite file. Measured during
///     #271: the composition suite took over 4 minutes with a leaked host alive and 1
///     minute 15 seconds once the orphan was killed.</para>
///     <para>IT DISTINGUISHES A LEAK FROM A SLOW REAP (issue #367). A host still in the
///     process table at teardown is either one nothing asked to exit or one whose kill had
///     not completed; the supervisor knows which and records it in
///     <see cref="HostProcessLedger"/>, so the guard reads that fact instead of inferring
///     a cause from a presence. A host the supervisor recorded is reported as a slow reap
///     and does not fail the run; a host nothing recorded is a leak and does. The grace
///     period below removes the other half of the false positive: the process table is
///     re-read before failing, so a host reaped microseconds earlier is never blamed.</para>
///     <para>WHY THIS SHAPE. Detecting leaks means asking the OS which processes are
///     children of THIS process, because that is what a session's host is: the
///     supervisor starts it directly. Ancestry gives exact attribution, so the guard
///     cannot fire on the harness's own host (a different process tree) and needs no
///     path or name heuristics. The baseline set of pre-existing pids covers the
///     remaining case: a host that was already running when this assembly started is
///     somebody else's, and blaming it would fail an innocent run.</para>
///     <para>A fixture failing in DisposeAsync is reported by xUnit as an assembly-level
///     failure, so the message reaches the operator rather than only the log.</para></summary>
public sealed class ChildHostLeakGuardFixture : IAsyncLifetime
{
  /// <summary>The host image name without its extension: what
  ///     <see cref="System.Diagnostics.Process.ProcessName"/> reports.</summary>
  internal const string HostImageName = "eThangAgent.ChildHost";

  /// <summary>How long the guard keeps re-reading the process table before it fails. A
  ///     host whose kill was requested can still be present for a moment afterwards, and
  ///     the supervisor's own wait is bounded - so a single read at teardown would blame a
  ///     host that was already on its way out.</summary>
  internal static readonly TimeSpan ReapGracePeriod = TimeSpan.FromSeconds(10);

  /// <summary>How often the process table is re-read during the grace period.</summary>
  internal static readonly TimeSpan ReapPollInterval = TimeSpan.FromMilliseconds(250);

  private readonly IProcessAncestry _ancestry;
  private readonly HashSet<int> _baselinePids;

  public ChildHostLeakGuardFixture()
      : this(new WindowsProcessAncestry(), Environment.ProcessId)
  {
  }

  /// <summary>The seam a test drives directly: no real processes, no real baseline.</summary>
  internal ChildHostLeakGuardFixture(IProcessAncestry ancestry, int parentProcessId)
  {
    _ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
    ParentProcessId = parentProcessId;
    _baselinePids = [.. ancestry.ChildrenOf(parentProcessId)
        .Where(c => string.Equals(c.ProcessName, HostImageName, StringComparison.OrdinalIgnoreCase))
        .Select(c => c.ProcessId)];
  }

  /// <summary>The process whose direct children are examined: the test host itself.</summary>
  public int ParentProcessId { get; }

  /// <summary>The hosts that were already running when this assembly started. Recorded
  ///     once, before any test runs - a baseline taken later could absorb a leak.</summary>
  public IReadOnlyCollection<int> BaselinePids => _baselinePids;

  /// <summary>What the supervisors recorded about the hosts they started and waited for.</summary>
  internal static IReadOnlyList<HostProcessRecord> HostFacts() => HostProcessLedger.Snapshot();

  /// <summary>The guard's decision at this instant: the leaked processes (empty when the run
  ///     was clean).</summary>
  internal IReadOnlyList<LiveProcess> FindLeaks() =>
      ChildHostLeakGuard.FindLeaks(_ancestry.ChildrenOf(ParentProcessId), HostImageName,
          _baselinePids, HostFacts());

  public ValueTask InitializeAsync() => ValueTask.CompletedTask;

  /// <summary>Fails the assembly when a host outlived its session with nothing having asked
  ///     it to exit. The process table is re-read until the grace period elapses, so a host
  ///     that is merely still being reaped is not reported as a leak.</summary>
  public async ValueTask DisposeAsync()
  {
    IReadOnlyList<LiveProcess> leaks = await FindLeaksAfterGraceAsync().ConfigureAwait(false);
    string? message = ChildHostLeakGuard.Describe(leaks, HostImageName, HostFacts());
    if (message is not null)
    {
      throw new InvalidOperationException(message);
    }
  }

  /// <summary>Re-reads the process table until nothing leaked, or until the grace period
  ///     elapses - then answers what is still there. The bound is a clock, so a host that
  ///     never leaves cannot hold teardown open.</summary>
  private async Task<IReadOnlyList<LiveProcess>> FindLeaksAfterGraceAsync()
  {
    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
      IReadOnlyList<LiveProcess> leaks = FindLeaks();
      if (leaks.Count == 0 || clock.Elapsed >= ReapGracePeriod)
      {
        return leaks;
      }

      await Task.Delay(ReapPollInterval).ConfigureAwait(false);
    }
  }
}
