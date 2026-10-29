namespace eThangAgent.AgentDomain;

/// <summary>What the harness does next after a completion fact (issue #301 D1/D2). The
///     ADVANCE is mechanical: a session marking its assigned step Done is the TRIGGER, and
///     the chain record's cursor decides what follows. No model call decides to continue.
///     <para>PURE, like <see cref="WatchdogPolicy"/>: facts in, decision out, no I/O and no
///     clock. The enactment (close the session, start the next one) lives in the application
///     layer, so the rule is unit-testable with fakes and cannot acquire a side effect by
///     accident.</para>
///     <para>Each check is its own helper returning a REFUSAL OR NULL, and the entry point
///     folds them with <c>??</c>. That shape is deliberate: it keeps every refusal a separate
///     named rule with its own code, instead of one long guard chain whose branches drift
///     apart as the rules grow.</para></summary>
public static class ChainAdvancePolicy
{
  /// <summary>Decides what follows a completion fact.
  ///     <para>Order matters and each branch is a different answer:</para>
  ///     <list type="bullet">
  ///     <item>a FOREIGN step (one this session was not assigned) is REFUSED, never acted on</item>
  ///     <item>a LAPSED claim is refused with its own code, because the assignment is no
  ///     longer current even though it is the right step (issue #301 amendment)</item>
  ///     <item>a step that is not Done is refused, so an intention cannot advance the cursor</item>
  ///     <item>otherwise the chain advances to the next available step, or completes</item>
  ///     </list></summary>
  /// <param name="chain">The chain as it stands AFTER the completion was recorded.</param>
  /// <param name="completedPosition">The step position the completion named.</param>
  /// <param name="sessionId">The session that reported the completion.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  public static ChainAdvance Decide(Chain chain, int completedPosition, string sessionId, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(chain);
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

    ChainStep? step = chain.StepAt(completedPosition);
    return step is null
      ? ChainAdvance.Refused(ChainAdvanceCode.UnknownStep,
          $"chain #{chain.Id} has no step {completedPosition}; it has {chain.Steps.Count} step(s).")
      : RefusalFor(chain, step, sessionId, now) ?? Next(chain, now);
  }

  /// <summary>Where the cursor goes from a settled chain: the next available step, or
  ///     completion when nothing is left to run. A chain with a PARKED or FAILED step is
  ///     NOT complete - it is blocked, and #303 owns what happens then (this issue refuses
  ///     to guess).</summary>
  public static ChainAdvance Next(Chain chain, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(chain);
    if (chain.NextAvailableStep(now) is { } next)
    {
      return new ChainAdvance(ChainAdvanceKind.StartStep, next.Position, null, null);
    }

    List<ChainStep> unsettled = [.. chain.Steps
        .Where(step => step.Status is not (ChainStepStatus.Done or ChainStepStatus.Skipped))
        .OrderBy(step => step.Position)];
    return unsettled.Count == 0
      ? new ChainAdvance(ChainAdvanceKind.CompleteChain, null, null, null)
      : new ChainAdvance(ChainAdvanceKind.Blocked, null, ChainAdvanceCode.StepsUnsettled,
          $"chain #{chain.Id} cannot advance: step(s) "
          + string.Join(", ", unsettled.Select(s => $"{s.Position} ({s.Status})"))
          + " are neither Done nor Skipped. A parked or failed step is #303's lifecycle to resolve.");
  }

  /// <summary>The first refusal that applies, or null when the completion is legitimate.
  ///     The order is the order they matter in: ownership before currency before outcome.</summary>
  private static ChainAdvance? RefusalFor(Chain chain, ChainStep step, string sessionId,
      DateTimeOffset now)
    => ForeignStep(chain, step, sessionId) ?? LapsedClaim(chain, step, now) ?? NotDone(chain, step, now);

  /// <summary>D3: the ownership check. The chain records the assignment on the step, so this
  ///     compares the session that REPORTED the completion to the session that CLAIMED it.
  ///     The message names the assigned session, because a session told only 'no' retries.</summary>
  private static ChainAdvance? ForeignStep(Chain chain, ChainStep step, string sessionId)
  {
    if (string.Equals(step.SessionId, sessionId, StringComparison.Ordinal))
    {
      return null;
    }

    string owner = step.SessionId is { } assigned ? $"session {assigned}" : "no session";
    return ChainAdvance.Refused(ChainAdvanceCode.ForeignStep,
        $"chain #{chain.Id} step {step.Position} is assigned to {owner}, not to session {sessionId}; only the assigned session may complete it.");
  }

  /// <summary>The amendment's check: the assignment exists but its claim window has closed,
  ///     so it is no longer current. Re-claiming records the new claimant.
  ///     <para>A Done step has already cleared <c>ClaimedAt</c> (settling nulls it), so the
  ///     lapsed case is a Running step whose window closed.</para></summary>
  private static ChainAdvance? LapsedClaim(Chain chain, ChainStep step, DateTimeOffset now)
    => step.Status == ChainStepStatus.Running && !step.IsClaimLive(now)
      ? ChainAdvance.Refused(ChainAdvanceCode.ClaimLapsed,
          $"chain #{chain.Id} step {step.Position}'s claim lapsed at "
          + (step.ClaimedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
              ?? "an unrecorded time")
          + $" (claim window {ChainClaimPolicy.ClaimTtl}); re-claim the step before completing it.")
      : null;

  /// <summary>The step must be Done: the advance reads a completion, never an intention.</summary>
  private static ChainAdvance? NotDone(Chain chain, ChainStep step, DateTimeOffset now)
    => step.Status == ChainStepStatus.Done
      ? null
      : ChainAdvance.Refused(ChainAdvanceCode.StepNotDone,
          $"chain #{chain.Id} step {step.Position} is {step.EffectiveStatus(now)}, not Done; the advance reads a completion, never an intention.");
}
