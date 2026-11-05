namespace eThangAgent.TestSupport;

/// <summary>The suite-wide leak check, as pure policy: given the processes that are
///     direct children of this test process, the host's image name, and the pids that
///     existed before the tests started, name the ones still alive at the end of the run.
///     <para>IT REPORTS A PRESENCE, NOT A CAUSE. A host alive at teardown is either one a
///     test never disposed or one whose disposal was still completing - the bounded wait
///     in the supervisor's teardown does not make 'disposal returned' mean 'the process is
///     gone' on a loaded machine. Both look identical from here, so the message names the
///     pids and states both causes rather than asserting the first.</para>
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
    return $"{leaks.Count} {hostImageName} process(es) were still alive as children of this "
        + $"test process when the run ended: pid {pids}. "
        + "This check OBSERVES that they are alive; it cannot tell why, and two causes look "
        + "identical here: a test that opened a session and never disposed it (dispose "
        + "sessions from IAsyncLifetime.DisposeAsync rather than a finally inside the test "
        + "body), or a session that WAS disposed whose host outlived the bounded teardown "
        + "wait on a loaded machine. "
        + "Do NOT kill these by pid or image name: every host process is a live session - "
        + "the user's, another agent's, or the one running the command - and killing by "
        + "image name takes all of them. Surface the pids above to the user, who decides.";
  }
}
