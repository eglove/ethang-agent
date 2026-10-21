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

  /// <summary>The latest observation. A breach's flags are sticky: the run was killed
  ///     for that reason, so a later sample can never erase the reason.</summary>
  public ProcessTreeGuardOutcome Outcome { get; private set; } = new(false, false, 0, 0);

  private void Sample()
  {
    // Re-entrancy guard: a sample that outruns the interval must not stack.
    if (Interlocked.Exchange(ref _sampling, 1) == 1)
    {
      return;
    }

    try
    {
      long bytes = 0;
      int count = 0;
      foreach (Process candidate in Process.GetProcesses())
      {
        try
        {
          if (candidate.Id == _rootId || IsDescendantOfRoot(candidate.Id))
          {
            bytes += candidate.WorkingSet64;
            count++;
          }
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

      _peakBytes = Math.Max(_peakBytes, bytes);
      _peakProcesses = Math.Max(_peakProcesses, count);

      bool memory = bytes > _memoryCeilingBytes;
      bool processes = count > _processCeiling;
      if (memory || processes)
      {
        Outcome = new ProcessTreeGuardOutcome(memory, processes, _peakBytes, _peakProcesses);
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

  /// <summary>Walks the candidate's parent chain toward the root. The depth cap bounds
  ///     the walk (a pid-reuse cycle cannot hang the sampler); a chain that does not
  ///     reach the root within it is not part of this tree.</summary>
  private bool IsDescendantOfRoot(int candidateId)
  {
    int current = ProcessTree.ParentOf(candidateId);
    for (int depth = 0; depth < 16 && current > 0; depth++)
    {
      if (current == _rootId)
      {
        return true;
      }

      current = ProcessTree.ParentOf(current);
    }

    return false;
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
