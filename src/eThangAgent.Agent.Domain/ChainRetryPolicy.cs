namespace eThangAgent.AgentDomain;

/// <summary>The retry bound on a chain step (issue #303 D2): three attempts, counted per step,
///     on the chain record.
///     <para>WHY A CONSTANT AND NOT A SETTING: the count is the only bound on a session that
///     keeps failing the same way. A configurable cap is one that can be set to never fire,
///     which is the same as having none - the rule <see cref="ChainClaimPolicy.ClaimTtl"/>
///     already follows for the claim window. Widening it is a product decision that updates
///     this constant and its test in the same change.</para>
///     <para>PURE, like <see cref="ChainAdvancePolicy"/>: attempts in, decision out. The
///     enactment (resume the session, or park the chain) is the application layer's.</para></summary>
public static class ChainRetryPolicy
{
  /// <summary>How many attempts a step may take before the chain parks. A claim IS an
  ///     attempt (<see cref="ChainStep.Claim"/> increments the count), so the first run
  ///     spends attempt 1 and two retries remain.</summary>
  public const int MaxAttempts = 3;

  /// <summary>True when <paramref name="attempts"/> has not yet reached the cap, so another
  ///     attempt is allowed.
  ///     <para>The boundary is the cap itself: at exactly <see cref="MaxAttempts"/> attempts
  ///     the budget is SPENT, because the count is attempts already made, not attempts
  ///     remaining.</para></summary>
  /// <param name="attempts">Attempts already recorded on the step.</param>
  public static bool CanRetry(int attempts)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(attempts);
    return attempts < MaxAttempts;
  }

  /// <summary>The retry decision for a step that just failed: resume the same session when
  ///     the budget allows, park the chain when it does not (D1 + D2).</summary>
  /// <param name="attempts">Attempts already recorded on the step.</param>
  public static ChainRetryDecision Decide(int attempts)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(attempts);
    return CanRetry(attempts) ? ChainRetryDecision.Retry : ChainRetryDecision.Park;
  }

  /// <summary>The note a resumed session is re-entered with (D7).
  ///     <para>DELIBERATELY NOT <see cref="RootSupervisionPolicy.Nudge"/>: that message is about
  ///     IDLENESS and tells a session it appears stalled. A step failure is about the WORK, and
  ///     reusing the nudge would tell a session something untrue about why it was restarted.</para>
  ///     <para>It names the attempt so the session knows how much budget is left, and it names
  ///     the failure so the session does not re-derive what already went wrong.</para></summary>
  /// <param name="chainId">The chain the step belongs to.</param>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="attempt">The attempt number this resume starts (1-based).</param>
  /// <param name="failure">What the failed attempt recorded, when it recorded anything.</param>
  public static string FailureNote(int chainId, int position, int attempt, string? failure)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(chainId, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
    string detail = string.IsNullOrWhiteSpace(failure)
      ? "The previous attempt ended without recording a reason."
      : "The previous attempt recorded: " + failure.Trim();
    return string.Join(' ',
        $"[harness] This is attempt {attempt} of {MaxAttempts} for chain #{chainId} step {position}.",
        "An earlier attempt did not complete, and this session is being RESUMED with its transcript intact",
        "- so the work already done here is still yours.",
        detail,
        "Continue from where the work stopped rather than starting over, and mark the step Done when it is finished.",
        "If the same failure happens again the chain will park for a human.");
  }
}

/// <summary>What a failed step's retry budget decides (issue #303 D1/D2).</summary>
public enum ChainRetryDecision
{
  /// <summary>Resume the same session: the budget allows another attempt.</summary>
  Retry,

  /// <summary>The budget is spent: park the chain waiting for a human (D3).</summary>
  Park,
}
