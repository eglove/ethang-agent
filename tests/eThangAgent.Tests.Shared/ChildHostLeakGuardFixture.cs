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

  /// <summary>The guard's whole decision, exposed so a test can pin it against fakes.
  ///     Returns the leaked processes (empty when the run was clean).</summary>
  public IReadOnlyList<LiveProcess> FindLeaks() =>
      ChildHostLeakGuard.FindLeaks(_ancestry.ChildrenOf(ParentProcessId), HostImageName, _baselinePids);

  public ValueTask InitializeAsync() => ValueTask.CompletedTask;

  public ValueTask DisposeAsync()
  {
    string? message = ChildHostLeakGuard.Describe(FindLeaks(), HostImageName);
    return message is null
        ? ValueTask.CompletedTask
        : throw new InvalidOperationException(message);
  }
}
