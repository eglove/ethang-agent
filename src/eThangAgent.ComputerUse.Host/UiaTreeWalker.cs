
namespace eThangAgent.ComputerUse.Host;

/// <summary>One walked element row (task 17): the spec 3.1 wire fields. Index is
///     assigned at walk order; bounds is the screen-rect int[4] {x,y,w,h}; actions are
///     pattern-derived (Invoke => press/invoke); container sampling caps children and
///     records children_total/children_shown/children_offset.</summary>
#pragma warning disable CA1819 // Named decision: bounds is the wire cell int[4] {x,y,w,h} copied verbatim; a copy per row allocates for nothing.
public sealed record UiaElementRow(
  int Index,
  string Role,
  string Kind,
  string? Title,
  string? Value,
  int[] Bounds,
  bool Enabled,
  bool Editable,
  IReadOnlyList<string> Actions,
  bool Focused,
  bool Selected,
  bool Pressable,
  bool HasMenu,
  int? ChildrenTotal,
  int? ChildrenShown,
  int? ChildrenOffset)
{
  /// <summary>The container-sampling cap: above this many children the walker records
  ///     totals and samples evenly instead of walking every child.</summary>
  public const int ContainerChildCap = 100;
#pragma warning restore CA1819
};

/// <summary>The per-request UIA walk worker (task 17): every request runs on a fresh
///     STA thread (COM UIA requires it), joined with the 45-second deadline cap; a
///     timed-out worker is abandoned (its thread dies with the process; COM teardown on
///     an abandoned STA is unsafe by design, so we orphan it deliberately). The walk
///     itself goes through CUIAutomation (IUIAutomation, TreeWalker/FindAllBuildCache).
///     NOT unit-testable without a live desktop - Task 18 integration exercises the
///     real walk; the deadline mechanics here are unit-covered.</summary>
public sealed class UiaTreeWalker(Func<string[]?>? walkCore = null)
{
  /// <summary>The hard deadline for one walk request.</summary>
  public static readonly TimeSpan WalkDeadline = TimeSpan.FromSeconds(45);

  private readonly Func<string[]?> _walkCore = walkCore ?? RealWalkCore;

  /// <summary>Runs one walk with the deadline. Returns the element rows (wire JSON
  ///     strings), or null when the walk timed out - the caller answers timeout.</summary>
  public string[]? WalkWithDeadline(TimeSpan? deadline = null)
  {
    TimeSpan cap = deadline ?? WalkDeadline;
    string[]? result = null;
    Exception? fault = null;
    using ManualResetEventSlim done = new(false);
    ThreadStart body = RunWorker;
    Thread worker = new(new ThreadStart(body));
    worker.SetApartmentState(ApartmentState.STA);
    worker.IsBackground = true;
    worker.Start();
    if (!done.Wait(cap))
    {
      return null; // deadline exceeded: the worker is abandoned by design
    }

#pragma warning disable CA1508 // Named decision: fault IS written on the worker thread; the analyzer cannot see the closure write ordering.
    return fault is null ? result : throw new InvalidOperationException("UIA walk failed on the STA worker.", fault);
#pragma warning restore CA1508

    void RunWorker()
    {
      try
      {
        result = _walkCore();
      }
#pragma warning disable CA1031 // Named decision: the STA worker boundary must never crash the process; the fault is delivered to the caller as internal.
      catch (Exception ex)
      {
        fault = ex;
      }
#pragma warning restore CA1031
      done.Set();
    }
  }

  private static string[]? RealWalkCore() => null; // task 18: the real CUIAutomation walk lands here
};
