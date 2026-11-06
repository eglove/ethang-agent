namespace eThangAgent.Composition;

/// <summary>What a supervisor knows about one host process: the session it belongs to, the
///     workspace it serves, its process id, how long the supervisor waited for it to exit
///     (null while it has not waited), and - in a test process - the test that opened the
///     session it serves.</summary>
/// <param name="SessionId">The conversation the host serves.</param>
/// <param name="Workspace">The workspace that conversation belongs to.</param>
/// <param name="ProcessId">The host process.</param>
/// <param name="Waited">How long the supervisor waited for the exit, or null when it has not
///     waited.</param>
/// <param name="TestOwner">The test that opened the session, or null outside a test process.</param>
public sealed record HostProcessRecord(string SessionId, string Workspace, int ProcessId,
    TimeSpan? Waited, string? TestOwner = null);

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

  /// <summary>The test whose scope this async flow runs inside, or null outside a test process.
  ///     <para>IT IS AMBIENT BECAUSE THE CALL SITE CANNOT KNOW IT. A session's container is built
  ///     on a thread whose execution context was deliberately suppressed, so the test framework's
  ///     own ambient context is null there; a test that wants its name on the fact it caused
  ///     states it once, and every host spawned under that scope carries it.</para>
  ///     <para>THE SCOPE IS A SNAPSHOT, NOT A LIVE CELL, because the suppression that hides the
  ///     framework's context hides an AsyncLocal too. A live value would read null through a
  ///     suppressed flow and every fact would lose its owner; a snapshot taken on the flow that
  ///     still has one travels into the suppressed body as an ordinary captured string.</para></summary>
  private static readonly AsyncLocal<OwnerScope?> CurrentScope = new();

  /// <summary>The test that owns the hosts recorded on this async flow, or null when nothing
  ///     claimed them.</summary>
  public static string? TestOwner => CurrentScope.Value?.TestName;

  /// <summary>The owner a suppressed flow must carry into its body, or null when nothing claimed
  ///     it. Taken BEFORE <c>ExecutionContext.SuppressFlow</c>, because after it this reads null.</summary>
  public static OwnerScope? CaptureOwner() => CurrentScope.Value;

  /// <summary>Installs <paramref name="scope"/> as this flow's claim, for a body whose execution
  ///     context was cut and could not carry it.</summary>
  /// <param name="scope">The claim captured before the cut, or null.</param>
  public static void RestoreOwner(OwnerScope? scope) => CurrentScope.Value = scope;

  /// <summary>Names the test that owns every host recorded on this async flow until the
  ///     returned scope is disposed, which restores the previous owner.</summary>
  /// <param name="testName">The test's display name.</param>
  public static IDisposable OwnedBy(string testName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(testName);
    OwnerScope? previous = CurrentScope.Value;
    CurrentScope.Value = new OwnerScope(testName, previous);
    return CurrentScope.Value;
  }

  private static readonly Lock Gate = new();
  private static readonly List<HostProcessRecord> Facts = [];

  /// <summary>Records one fact about a host: a spawn (Waited null) or a bounded exit wait that
  ///     elapsed (Waited set). A fact that names no test owner is stamped with the ambient one,
  ///     so a caller that has never heard of tests records the same thing it always did.</summary>
  public static void Record(HostProcessRecord record)
  {
    ArgumentNullException.ThrowIfNull(record);
    HostProcessRecord stamped = record.TestOwner is null && TestOwner is { } owner
        ? record with { TestOwner = owner }
        : record;
    lock (Gate)
    {
      Facts.Add(stamped);
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

/// <summary>One test's claim over the hosts its sessions spawn, and the claim it replaced.
///     <para>A VALUE, so a suppressed flow can carry it: the claim is captured before the
///     suppression and restored inside it, which is the only way an owner survives a context
///     that was deliberately cut.</para></summary>
/// <param name="TestName">The test's display name.</param>
/// <param name="Previous">The claim this one replaced, or null.</param>
public sealed record OwnerScope(string TestName, OwnerScope? Previous) : IDisposable
{
  /// <summary>Restores the claim this one replaced, on the flow that disposes it.</summary>
  public void Dispose() => HostProcessLedger.RestoreOwner(Previous);
}
