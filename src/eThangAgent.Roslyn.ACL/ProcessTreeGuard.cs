using System.Diagnostics;

namespace eThangAgent.Roslyn.ACL;

/// <summary>What a guard observed over one run's lifetime.</summary>
public sealed record ProcessTreeGuardOutcome(bool MemoryExceeded, bool ProcessCountExceeded, long PeakBytes, int PeakProcesses);

/// <summary>Samples a spawned process TREE against its ceilings (spec D10). A Job
///     Object memory limit alone would surface as an allocation failure inside the
///     child — a half-alive program that keeps failing is worse for the model than a
///     clean death it can react to — so the guard samples the tree's working set and
///     kills it outright on breach. The kill is the contract; the Job Object limit
///     stays as a backstop for a child that allocates faster than one sample interval.
///     Additive to containment, never a replacement: the caller's kill-on-close Job
///     Object still owns the tree's lifetime.</summary>
internal sealed class ProcessTreeGuard : IDisposable
{
  private readonly Process _root;
  private readonly long _memoryCeilingBytes;
  private readonly int _processCeiling;
  private readonly Timer _timer;
  private readonly int _rootId;
  private long _peakBytes;
  private int _peakProcesses;
  private int _sampling;
  private int _breached;

  public ProcessTreeGuard(Process root, long memoryCeilingBytes, int processCeiling, TimeSpan sampleInterval)
  {
    ArgumentNullException.ThrowIfNull(root);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryCeilingBytes);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processCeiling);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleInterval.Ticks);

    _root = root;
    _rootId = root.Id;
    _memoryCeilingBytes = memoryCeilingBytes;
    _processCeiling = processCeiling;
    _timer = new Timer(static state => ((ProcessTreeGuard)state!).Sample(), this, sampleInterval, sampleInterval);
  }

  /// <summary>The latest observation. A breach is TERMINAL and its flags are sticky:
  ///     the run was killed for that reason, so no later tick can erase it. This is
  ///     load-bearing, not cosmetic — the timer is periodic and keeps firing after the
  ///     kill, so without the latch a post-kill sample of the (now empty) tree would
  ///     overwrite the breach with a clean outcome and the caller would report a bare
  ///     non-zero exit with no ceiling error at all.</summary>
  public ProcessTreeGuardOutcome Outcome { get; private set; } = new(false, false, 0, 0);

  private void Sample()
  {
    // Terminal latch: once a breach has been observed the guard stops sampling for
    // good, so the reason the tree died can never be cleared by a later tick. The
    // re-entrancy check keeps a sample that outruns the interval from stacking.
    if (Volatile.Read(ref _breached) == 1 || Interlocked.Exchange(ref _sampling, 1) == 1)
    {
      return;
    }

    try
    {
      (long bytes, int count) = MeasureTree();
      _peakBytes = Math.Max(_peakBytes, bytes);
      _peakProcesses = Math.Max(_peakProcesses, count);

      bool memory = bytes > _memoryCeilingBytes;
      bool processes = count > _processCeiling;
      if (memory || processes)
      {
        Outcome = new ProcessTreeGuardOutcome(memory, processes, _peakBytes, _peakProcesses);
        // Published BEFORE the kill so no tick can observe a breached tree without
        // the latch set.
        Volatile.Write(ref _breached, 1);
        KillTree();
        return;
      }

      Outcome = new ProcessTreeGuardOutcome(false, false, _peakBytes, _peakProcesses);
    }
    // Named decision (CA1031): sampling must never fault the run it is guarding.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // A failed sample is a missed sample; the next tick retries.
    }
    finally
    {
      _ = Interlocked.Exchange(ref _sampling, 0);
    }
  }

  /// <summary>One sample's working-set sum and process count for the tree rooted at
  ///     the guarded process. Parent ids are read ONCE per process per sample and the
  ///     tree is then resolved entirely in memory: walking each candidate's ancestor
  ///     chain instead would re-query the same parents once per candidate per hop —
  ///     on the order of thousands of P/Invoke round-trips per tick on a busy machine.</summary>
  private (long Bytes, int Count) MeasureTree()
  {
    Dictionary<int, int> parentOf = [];
    foreach (Process candidate in Process.GetProcesses())
    {
      try
      {
        parentOf[candidate.Id] = ProcessTree.ParentOf(candidate.Id);
      }
      // Named decision (CA1031): a process that exits mid-enumeration is expected.
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        // Exited between enumeration and the read; it is simply not part of the tree.
      }
      finally
      {
        candidate.Dispose();
      }
    }

    HashSet<int> tree = TreeMembers(parentOf, _rootId);
    long bytes = 0;
    foreach (int memberId in tree)
    {
      bytes += WorkingSetOf(memberId);
    }

    return (bytes, tree.Count);
  }

  /// <summary>The root plus every process whose parent chain reaches it, resolved over
  ///     the already-read edge map. The closure is bounded by the edge count, so a
  ///     pid-reuse cycle cannot loop forever.</summary>
  private static HashSet<int> TreeMembers(Dictionary<int, int> parentOf, int rootId)
  {
    Dictionary<int, List<int>> childrenOf = [];
    foreach ((int id, int parentId) in parentOf)
    {
      if (!childrenOf.TryGetValue(parentId, out List<int>? children))
      {
        children = [];
        childrenOf[parentId] = children;
      }

      children.Add(id);
    }

    HashSet<int> members = [rootId];
    Queue<int> frontier = new();
    frontier.Enqueue(rootId);
    while (frontier.Count > 0)
    {
      if (!childrenOf.TryGetValue(frontier.Dequeue(), out List<int>? children))
      {
        continue;
      }

      // members.Add is the filter AND the bookkeeping: only a newly discovered child
      // joins the frontier, so each process is visited exactly once.
      foreach (int childId in children.Where(members.Add))
      {
        frontier.Enqueue(childId);
      }
    }

    return members;
  }

  /// <summary>The member's working set, or 0 when it exited between the enumeration
  ///     and the read (a departed process contributes nothing to the tree's size).</summary>
  private static long WorkingSetOf(int processId)
  {
    // Named decision (CA1031): the process may exit at any point during the sample.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      using Process member = Process.GetProcessById(processId);
      return member.WorkingSet64;
    }
    catch (Exception)
    {
      return 0;
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  private void KillTree()
  {
    // Named decision (CA1031): a failed kill must not crash the run — the process may
    // already have exited, or the job may have reaped it first.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      _root.Kill(entireProcessTree: true);
    }
    catch (Exception)
    {
      // Already exited.
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  public void Dispose() => _timer.Dispose();
}
