using eThangAgent.Composition;

namespace eThangAgent.TestSupport;

/// <summary>The suite-wide leak check, as pure policy: given the processes that are direct
///     children of this test process, the host's image name, the pids that existed before the
///     tests started, and what the supervisors recorded about the hosts they started, name the
///     ones that leaked.
///     <para>IT NAMES A CAUSE, NOT A PRESENCE. A host alive at teardown is one of two things,
///     and the supervisors already know which: a host nothing ever asked to exit (a test that
///     opened a session and never disposed it) or a host whose kill was requested and had not
///     completed within ExitWaitTimeout on a loaded machine. Only the first is a leak. The
///     second is read from the supervisor's own record and reported as a slow reap, so a
///     correctly finished test is never failed for it.</para>
///     <para>Separated from the OS query and from xUnit so it can be tested with fakes: every
///     interesting question is about which candidates COUNT, and none of those needs a real
///     process to answer.</para></summary>
public static class ChildHostLeakGuard
{
  /// <summary>Children of this process whose image is <paramref name="hostImageName"/>,
  ///     which are not in <paramref name="baselinePids"/>, and which no supervisor recorded
  ///     as having waited for. A pid observed before the tests started is not this run's
  ///     leak - it was already there, and blaming it would make the guard fail on a machine
  ///     where some unrelated long-lived host exists.</summary>
  public static IReadOnlyList<LiveProcess> FindLeaks(
      IReadOnlyList<LiveProcess> childrenOfThisProcess,
      string hostImageName,
      IReadOnlySet<int> baselinePids,
      IReadOnlyList<HostProcessRecord> hostFacts)
  {
    ArgumentNullException.ThrowIfNull(childrenOfThisProcess);
    ArgumentNullException.ThrowIfNull(hostImageName);
    ArgumentNullException.ThrowIfNull(baselinePids);
    ArgumentNullException.ThrowIfNull(hostFacts);

    HashSet<int> waited = [.. hostFacts
        .Where(fact => fact.Waited is not null)
        .Select(fact => fact.ProcessId)];

    return [.. childrenOfThisProcess
        .Where(child => string.Equals(child.ProcessName, hostImageName, StringComparison.OrdinalIgnoreCase))
        .Where(child => !baselinePids.Contains(child.ProcessId))
        .Where(child => !waited.Contains(child.ProcessId))
        .OrderBy(child => child.ProcessId)];
  }

  /// <summary>The failure message, or null when nothing leaked. It NAMES each leaked
  ///     process and says what to do about it, because a guard whose failure reads only as
  ///     'a test failed' costs the same investigation the guard exists to avoid.
  ///     <para>A slow reap is not a leak: it is named as such, with the session that owned it
  ///     and how long its supervisor waited, so the reader learns what the machine did without
  ///     being sent to fix a test that was already correct.</para></summary>
  public static string? Describe(
      IReadOnlyList<LiveProcess> leaks,
      string hostImageName,
      IReadOnlyList<HostProcessRecord> hostFacts)
  {
    ArgumentNullException.ThrowIfNull(leaks);
    ArgumentNullException.ThrowIfNull(hostImageName);
    ArgumentNullException.ThrowIfNull(hostFacts);

    if (leaks.Count == 0)
    {
      return null;
    }

    string pids = string.Join(", ", leaks.Select(l => l.ProcessId));
    return $"{leaks.Count} {hostImageName} process(es) were still alive as children of this "
        + $"test process when the run ended, and no supervisor ever waited for them to exit: pid {pids}. "
        + "A host nothing asked to exit is a LEAK: a test opened a session and never disposed "
        + "it (dispose sessions from IAsyncLifetime.DisposeAsync rather than a finally inside "
        + "the test body, so a failing assertion cannot skip it). "
        + DescribeOwners(leaks, hostFacts)
        + DescribeReaping(hostFacts)
        + "Do NOT kill these by pid or image name: every host process is a live session - "
        + "the user's, another agent's, or the one running the command - and killing by "
        + "image name takes all of them. Surface the pids above to the user, who decides.";
  }

  /// <summary>The session and workspace each leaked host belongs to, as the supervisors
  ///     recorded them - so the failure names what leaked instead of only which pid did.</summary>
  private static string DescribeOwners(IReadOnlyList<LiveProcess> leaks, IReadOnlyList<HostProcessRecord> hostFacts)
  {
    string[] owners = [.. leaks
        .Select(leak => hostFacts.LastOrDefault(fact => fact.ProcessId == leak.ProcessId))
        .OfType<HostProcessRecord>()
        .Select(fact => $"pid {fact.ProcessId} (session {fact.SessionId}, workspace {fact.Workspace})")];
    return owners.Length == 0
        ? "No supervisor recorded starting these, so nothing in this run owns them. "
        : "The supervisors recorded these hosts as: " + string.Join("; ", owners) + ". ";
  }

  /// <summary>What the supervisors recorded as slow reaps: empty when they recorded
  ///     nothing, so a clean message does not carry an empty clause.</summary>
  private static string DescribeReaping(IReadOnlyList<HostProcessRecord> hostFacts)
  {
    HostProcessRecord[] reaping = [.. hostFacts.Where(fact => fact.Waited is not null)];
    if (reaping.Length == 0)
    {
      return "No supervisor recorded a host awaiting its exit, so no kill was requested for these. ";
    }

    string recorded = string.Join("; ", reaping.Select(fact =>
        $"pid {fact.ProcessId} (session {fact.SessionId}, waited {fact.Waited!.Value.TotalSeconds:F1}s)"));
    return "The supervisors also recorded hosts still present when their bounded exit wait elapsed - "
        + $"those are slow reaps, not leaks, and are not counted above: {recorded}. ";
  }
}
