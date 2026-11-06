namespace eThangAgent.Composition;

/// <summary>What a supervisor knows about one host process: the session it belongs to, the
///     workspace it serves, its process id, and how long the supervisor waited for it to exit
///     (null while it has not waited).</summary>
public sealed record HostProcessRecord(string SessionId, string Workspace, int ProcessId, TimeSpan? Waited);

/// <summary>Process-wide record of the host processes supervisors started and of the ones
///     that outlived their bounded exit wait.
///     <para>IT CARRIES THE FACT A PROCESS TABLE CANNOT. A host still present at some later
///     moment is one of two things, and only one is a defect: a host nothing ever asked to
///     exit (a leak - the session that owned it was never disposed), or a host whose kill
///     was requested and had not completed within <c>ExitWaitTimeout</c> (a slow reap on a
///     loaded machine). A spawn with no exit and no wait names the first by SESSION, so a
///     caller that finds the process can say what leaked instead of inspecting a pid by
///     hand. In production the same record is what makes a host that ignores a kill a
///     visible defect rather than a silently swallowed timeout.</para>
///     <para>Bounded: the most recent <see cref="Capacity"/> facts are kept, because this
///     exists to explain a presence, not to archive a history. Thread-safe: disposal runs on
///     whichever thread closed the session.</para></summary>
public static class HostProcessLedger
{
  /// <summary>How many facts are kept.</summary>
  private const int Capacity = 256;

  private static readonly Lock Gate = new();
  private static readonly List<HostProcessRecord> Facts = [];

  /// <summary>Records one fact about a host: a spawn (Waited null) or a bounded exit wait that
  ///     elapsed (Waited set).</summary>
  public static void Record(HostProcessRecord record)
  {
    ArgumentNullException.ThrowIfNull(record);
    lock (Gate)
    {
      Facts.Add(record);
      if (Facts.Count > Capacity)
      {
        Facts.RemoveRange(0, Facts.Count - Capacity);
      }
    }
  }

  /// <summary>A copy of the facts held now, oldest first.</summary>
  public static IReadOnlyList<HostProcessRecord> Snapshot()
  {
    lock (Gate)
    {
      return [.. Facts];
    }
  }

  /// <summary>Drops every fact.</summary>
  public static void Clear()
  {
    lock (Gate)
    {
      Facts.Clear();
    }
  }
}
