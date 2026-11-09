using Xunit;

namespace eThangAgent.TestSupport;

/// <summary>The assembly-level guard that turns 'a leaked language-server daemon silently
///     degrades the suite' into a NAMED failure.
///     <para>The defect it exists for: a daemon is a real child process of whichever
///     process started it, and it serves a named pipe until it is stopped. A test that
///     opens a code workspace and never disposes its host leaves that daemon alive after
///     the test host exits - it holds its pipe and its SQLite handles, and the next run
///     joins it instead of starting its own. Nothing fails; the only symptom is a
///     process table that grows by one daemon per run.</para>
///     <para>WHY ANCESTRY, like <see cref="ChildHostLeakGuardFixture"/>: a daemon the
///     harness or the user started belongs to a different process tree, so asking only
///     about THIS process's children excludes it structurally rather than by name.
///     </para></summary>
public sealed class LspDaemonLeakGuardFixture : IAsyncLifetime
{
  /// <summary>The daemon image name without its extension: what
  ///     <see cref="System.Diagnostics.Process.ProcessName"/> reports.</summary>
  public const string DaemonImageName = "eThangAgent.Lsp.Host";

  /// <summary>How long the guard keeps re-reading the process table before it fails. A
  ///     daemon whose kill was requested can still be present for a moment afterwards,
  ///     so a single read at teardown would blame a daemon already on its way out.</summary>
  internal static readonly TimeSpan ReapGracePeriod = TestCeiling.Value;

  /// <summary>How often the process table is re-read during the grace period.</summary>
  internal static readonly TimeSpan ReapPollInterval = TimeSpan.FromMilliseconds(250);

  private readonly IProcessAncestry _ancestry;
  private readonly HashSet<int> _baselinePids;

  public LspDaemonLeakGuardFixture()
      : this(new WindowsProcessAncestry(), Environment.ProcessId)
  {
  }

  /// <summary>The seam a test drives directly: no real processes, no real baseline.</summary>
  internal LspDaemonLeakGuardFixture(IProcessAncestry ancestry, int parentProcessId)
  {
    _ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
    ParentProcessId = parentProcessId;
    _baselinePids = [.. ancestry.ChildrenOf(parentProcessId)
        .Where(c => string.Equals(c.ProcessName, DaemonImageName, StringComparison.OrdinalIgnoreCase))
        .Select(c => c.ProcessId)];
  }

  /// <summary>The process whose direct children are examined: the test host itself.</summary>
  public int ParentProcessId { get; }

  /// <summary>The daemons that were already running when this assembly started. Recorded
  ///     once, before any test runs - a baseline taken later could absorb a leak.</summary>
  public IReadOnlyCollection<int> BaselinePids => _baselinePids;

  /// <summary>The guard's decision at this instant: the leaked daemons (empty when the
  ///     run was clean).</summary>
  internal IReadOnlyList<LiveProcess> FindLeaks() =>
      ChildHostLeakGuard.FindLeaks(_ancestry.ChildrenOf(ParentProcessId), DaemonImageName,
          _baselinePids, []);

  public ValueTask InitializeAsync() => ValueTask.CompletedTask;

  /// <summary>Fails the assembly when a daemon outlived the test that started it. The
  ///     process table is re-read until the grace period elapses, so a daemon merely
  ///     still being reaped is not reported as a leak.</summary>
  public async ValueTask DisposeAsync()
  {
    IReadOnlyList<LiveProcess> leaks = await FindLeaksAfterGraceAsync().ConfigureAwait(false);
    string? message = ChildHostLeakGuard.Describe(leaks, DaemonImageName, []);
    if (message is not null)
    {
      throw new InvalidOperationException(message);
    }
  }

  /// <summary>Re-reads the process table until nothing leaked, or until the grace period
  ///     elapses - then answers what is still there. The bound is a clock, so a daemon
  ///     that never leaves cannot hold teardown open.</summary>
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
