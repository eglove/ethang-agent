namespace eThangAgent.AgentDomain;

/// <summary>What a chain that cannot move on its own needs.
///     <para>A RUNNING CHAIN IS SUPPOSED TO MOVE BY ITSELF: a session completes a step and the
///     advance starts the next one. When no session holds the step the cursor would start, that
///     loop has nothing to run - and a Running chain with nothing running is indistinguishable
///     from one that is working, which is why it sits there forever.</para>
///     <para>THE CHAIN RECORD ALREADY SAYS EVERYTHING NEEDED. This policy reads only the record:
///     no session, no clock, no store. The repair it names is enacted by the caller, so the rule
///     is unit-testable and cannot acquire a side effect.</para></summary>
public static class ChainStuckPolicy
{
  /// <summary>The repair a chain needs, or null when it does not need one.
  ///     <para>ORDER IS THE ORDER THE ANSWERS MATTER IN:</para>
  ///     <list type="bullet">
  ///     <item>a chain that is not Running is not stuck - Parked is waiting for a human on
  ///     purpose, and Completed or Abandoned are finished</item>
  ///     <item>a chain with a RUNNING step is working: some session holds it, and its completion
  ///     is what moves the cursor. Repairing here would start a second session for work somebody
  ///     is already doing</item>
  ///     <item>a chain with a step the advance can start needs it started</item>
  ///     <item>a chain whose steps are all settled but whose status says Running needs only to be
  ///     marked Completed: the work finished and the last write did not land</item>
  ///     <item>otherwise the chain is blocked on a step that will never settle by itself - it is
  ///     Failed or Parked - and the honest repair is to park the chain naming it</item>
  ///     </list></summary>
  /// <param name="chain">The chain as the record stands.</param>
  public static ChainRepair? RepairFor(Chain chain)
  {
    ArgumentNullException.ThrowIfNull(chain);
    return chain.Status != ChainStatus.Running ? null : Repair(chain);
  }

  /// <summary>The repair for a RUNNING chain, or null when it is working or has nothing to
  ///     repair. Split out so the status guard above reads as one statement.</summary>
  /// <param name="chain">The chain as the record stands.</param>
  private static ChainRepair? Repair(Chain chain)
  {
    if (chain.Steps.Any(step => step.Status == ChainStepStatus.Running))
    {
      return null;
    }

    List<ChainStep> unsettled = [.. chain.Steps
        .Where(step => step.Status is not (ChainStepStatus.Done or ChainStepStatus.Skipped))
        .OrderBy(step => step.Position)];
    if (unsettled.Count == 0)
    {
      return new ChainRepair(ChainRepairKind.CompleteChain, null, null);
    }

    // A STEP THAT CANNOT SETTLE BLOCKS EVERYTHING AFTER IT. The chain is a sequence, so starting
    // step 3 while step 2 is Failed would build on work that did not land - and the cursor would
    // happily offer it, because 'available' only means 'Pending'. The repair for a blocked chain
    // is to park it, naming what stopped it.
    return chain.NextAvailableStep() is { } next && unsettled[0].Position >= next.Position
        ? new ChainRepair(ChainRepairKind.StartStep, next.Position, null)
        : new ChainRepair(ChainRepairKind.ParkChain, unsettled[0].Position, ParkReason(chain, unsettled));
  }

  /// <summary>The park reason, naming every step that cannot settle by itself and the exits a
  ///     reader has.
  ///     <para>IT NAMES ALL OF THEM, not only the first: a chain blocked on two steps needs both
  ///     fixed, and naming one would make the next repair report the other as though it were
  ///     new.</para></summary>
  /// <param name="chain">The chain being parked.</param>
  /// <param name="unsettled">Its unsettled steps, in position order.</param>
  private static string ParkReason(Chain chain, List<ChainStep> unsettled) =>
      $"chain #{chain.Id} cannot advance: step(s) "
      + string.Join(", ", unsettled.Select(step => $"{step.Position} ({step.Status})"))
      + " are neither Done nor Skipped and no session holds them, so nothing will complete them. "
      + "Resume the chain to re-arm the step, or abandon it to drop the work.";
}

/// <summary>What a stuck chain needs done to it.</summary>
public enum ChainRepairKind
{
  /// <summary>Start a session for the step at <see cref="ChainRepair.Position"/>.</summary>
  StartStep,

  /// <summary>Every step settled but the chain still says Running: mark it Completed.</summary>
  CompleteChain,

  /// <summary>Park the chain, naming the step at <see cref="ChainRepair.Position"/>.
  ///     <para>A PARK IS A REPAIR, not a failure. It converts 'Running with nothing running',
  ///     which nothing can act on, into 'Parked waiting for a human', which
  ///     <c>chain.resume-parked</c> and <c>chain.abandon</c> can both act on.</para></summary>
  ParkChain,
}

/// <summary>The repair a stuck chain needs: what to do, which step it is about, and why the chain
///     parked when that is the answer.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Position">The 1-based step position the repair is about, or null when it is about
///     the chain as a whole.</param>
/// <param name="Reason">Why the chain parked, or null.</param>
public sealed record ChainRepair(ChainRepairKind Kind, int? Position, string? Reason);
