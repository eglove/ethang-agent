namespace eThangAgent.AgentDomain;

/// <summary>The claim window on a <see cref="ChainStepStatus.Running"/> chain step
///     (issue #300 amendment, following Agensh's shared-context board:
///     <c>board-service/shared_lessons.py</c>, <c>DEFAULT_CLAIM_TTL_SECONDS = 3600</c>).
///     <para>A claim EXPIRES; it is not detected as dead. A liveness probe needs someone to
///     ask the question, and this database's history shows what that costs: 128 child rows
///     were marked Running with no run behind them, every one of them a claim nothing would
///     ever retract. A TTL answers by construction.</para>
///     <para>Expiry is computed on READ from the step's <c>ClaimedAt</c>, never written by a
///     sweeper: a crashed process cannot leave a claim alive and a sleeping one cannot
///     expire a live claim early.</para>
///     <para>The window is a NAMED CONSTANT, deliberately not a settings key — a configurable
///     claim window is one that can be set to never expire. Widening it is a product
///     decision that updates this constant and its test in the same change.</para></summary>
public static class ChainClaimPolicy
{
  /// <summary>How long a Running step's claim stays live without renewal: one hour.</summary>
  public static TimeSpan ClaimTtl { get; } = TimeSpan.FromHours(1);
}
