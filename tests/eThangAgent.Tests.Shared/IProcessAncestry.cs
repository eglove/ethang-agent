namespace eThangAgent.TestSupport;

/// <summary>One process the guard can report: the id and image name, so a failure names
///     the culprit concretely enough to find it by hand.</summary>
public sealed record LiveProcess(int ProcessId, string ProcessName);

/// <summary>The one fact the leak guard needs from the OS: which processes are DIRECT
///     children of a given process. A session OWNS a host process - its container's
///     supervisor spawns it directly - so a host whose parent is this test process and
///     which outlives the tests is a leak BY CONSTRUCTION, never an inference from a
///     name it happens to share.
///     <para>Exact ancestry is what makes the guard usable at all: the harness itself runs
///     every conversation in a ChildHost, so asking 'is any host alive?' would fail on
///     every run (measured 2026-10-25: a probe saw the running conversation's own host
///     alive throughout a test that created and disposed its own). Asking only about OUR
///     children excludes it structurally rather than by heuristic.</para>
///     <para>Named decision: ancestry is queried through NtQueryInformationProcess rather
///     than System.Management, because WMI spins up a COM stack and queries its whole
///     provider per call - far too heavy for an end-of-run check, and it would add a
///     package dependency to every test assembly. The repo already uses this exact
///     P/Invoke surface for the same question (the Roslyn ACL's ProcessTree).</para></summary>
public interface IProcessAncestry
{
  /// <summary>Every live process whose DIRECT parent is <paramref name="parentProcessId"/>.
  ///     A process that cannot be queried simply does not appear - an unreadable
  ///     candidate is not evidence of a leak, and failing on it would make the guard
  ///     fire on access-denied rather than on a real leak.</summary>
  IReadOnlyList<LiveProcess> ChildrenOf(int parentProcessId);
}
