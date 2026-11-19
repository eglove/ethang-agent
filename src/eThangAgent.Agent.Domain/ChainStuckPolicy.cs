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
  /// <param name="sessionIsLive">Answers 'is the session holding this step still alive', or
  ///     null when the caller cannot tell.
  ///     <para>A RUNNING STEP IS NOT PROOF OF PROGRESS. The record says a session holds the step;
  ///     it says nothing about whether that session still exists. A session that died leaves the
  ///     step Running forever, and the chain then looks busy while nothing will ever complete it -
  ///     which is exactly how a finished batch sat Running for two hours.</para>
  ///     <para>NULL MEANS 'NOT SAID', never 'dead': a caller that cannot answer is not allowed to
  ///     reap a live session's work, so the old answer (nothing to repair) stands.</para></param>
  public static ChainRepair? RepairFor(Chain chain, Func<string, bool>? sessionIsLive = null)
  {
    ArgumentNullException.ThrowIfNull(chain);
    return chain.Status != ChainStatus.Running ? null : Repair(chain, sessionIsLive);
  }

  /// <summary>The repair for a RUNNING chain, or null when it is working or has nothing to
  ///     repair. Split out so the status guard above reads as one statement.</summary>
  /// <param name="chain">The chain as the record stands.</param>
  /// <param name="sessionIsLive">Answers whether a step's session is still alive, or null.</param>
  private static ChainRepair? Repair(Chain chain, Func<string, bool>? sessionIsLive)
  {
    // A STRANDED STEP IS RE-ARMED, NOT LEFT RUNNING. A Running step whose session is gone is the
    // one shape nothing else can move: no session can complete it, resume-parked refuses a chain
    // that is not parked, and the advance sees a step somebody holds. Re-arming it is the repair.
    if (Stranded(chain, sessionIsLive) is { } stranded)
    {
      return new ChainRepair(ChainRepairKind.RearmStep, stranded.Position, StrandedReason(chain, stranded));
    }

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

  /// <summary>The first RUNNING step whose session is gone, or null when every running step is
  ///     still held by a live session (or the caller cannot tell).
  ///     <para>A PLACEHOLDER CLAIMANT IS ALWAYS STRANDED. The harness claims a step under a
  ///     provisional id before the session exists, and corrects it once the launch answers - so a
  ///     step still holding one is a launch whose correction never landed, and no session will
  ///     ever complete it.</para></summary>
  /// <param name="chain">The chain as the record stands.</param>
  /// <param name="sessionIsLive">Answers whether a session is alive, or null when unknown.</param>
  private static ChainStep? Stranded(Chain chain, Func<string, bool>? sessionIsLive) =>
      chain.Steps.FirstOrDefault(step => step.Status == ChainStepStatus.Running
          && (step.HoldsPlaceholderClaim
              || (sessionIsLive is { } live && step.SessionId is { } session && !live(session))));

  /// <summary>Why a stranded step was re-armed, naming the session that no longer holds it.</summary>
  /// <param name="chain">The chain being repaired.</param>
  /// <param name="step">The stranded step.</param>
  private static string StrandedReason(Chain chain, ChainStep step) =>
      step.HoldsPlaceholderClaim
          ? $"chain #{chain.Id} step {step.Position} is Running under a placeholder claim ({step.SessionId}), so no session holds it. Re-armed for a fresh session."
          : $"chain #{chain.Id} step {step.Position} is Running but its session {step.SessionId} is no longer alive, so nothing will complete it. Re-armed for a fresh session.";

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

  /// <summary>Re-arm the step at <see cref="ChainRepair.Position"/> and start a fresh session for
  ///     it: the step's session is gone, so its claim is cleared and the work is offered again.
  ///     <para>DISTINCT FROM <see cref="StartStep"/>, which starts a step that was already
  ///     available. This one has to take the step back from a session that no longer exists
  ///     first, which is why it carries a reason.</para></summary>
  RearmStep,

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
