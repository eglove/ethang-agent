namespace eThangAgent.TestSupport;

/// <summary>The suite-wide leak check, as pure policy: given the processes that are
///     direct children of this test process, the host's image name, and the pids that
///     existed before the tests started, name the ones that leaked.
///     <para>Separated from the OS query and from xUnit so it can be tested with fakes:
///     every interesting question is about which candidates COUNT, and none of those
///     needs a real process to answer.</para></summary>
public static class ChildHostLeakGuard
{
  /// <summary>Children of this process whose image is <paramref name="hostImageName"/> and
  ///     which are not in <paramref name="baselinePids"/>. A pid observed before the tests
  ///     started is not this run's leak - it was already there, and blaming it would make
  ///     the guard fail on a machine where some unrelated long-lived host exists.</summary>
  public static IReadOnlyList<LiveProcess> FindLeaks(
      IReadOnlyList<LiveProcess> childrenOfThisProcess,
      string hostImageName,
      IReadOnlySet<int> baselinePids)
  {
    ArgumentNullException.ThrowIfNull(childrenOfThisProcess);
    ArgumentNullException.ThrowIfNull(hostImageName);
    ArgumentNullException.ThrowIfNull(baselinePids);

    return [.. childrenOfThisProcess
        .Where(child => string.Equals(child.ProcessName, hostImageName, StringComparison.OrdinalIgnoreCase))
        .Where(child => !baselinePids.Contains(child.ProcessId))
        .OrderBy(child => child.ProcessId)];
  }

  /// <summary>The failure message, or null when nothing leaked. It NAMES each leaked
  ///     process and says what to do about it, because a guard whose failure reads only as
  ///     'a test failed' costs the same investigation the guard exists to avoid.</summary>
  public static string? Describe(IReadOnlyList<LiveProcess> leaks, string hostImageName)
  {
    ArgumentNullException.ThrowIfNull(leaks);
    ArgumentNullException.ThrowIfNull(hostImageName);

    if (leaks.Count == 0)
    {
      return null;
    }

    string pids = string.Join(", ", leaks.Select(l => l.ProcessId));
    return $"{leaks.Count} {hostImageName} process(es) outlived the test run: pid {pids}. "
        + "A session OWNS its host, so disposing its container kills it; a host still alive "
        + "here means a test opened a session and never disposed it - an assertion that threw "
        + "before its teardown, or a teardown that was never written. Dispose sessions from "
        + "IAsyncLifetime.DisposeAsync rather than a finally inside the test body, and kill "
        + "the pids above before re-running: a surviving host holds its SQLite file and "
        + "silently slows every later run.";
  }
}
