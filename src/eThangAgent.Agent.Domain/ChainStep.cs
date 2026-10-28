namespace eThangAgent.AgentDomain;

/// <summary>
/// One ordered step of a chain: the EXECUTION record for a plan step position (issue #300
/// D2/D3). It names the plan step by position and never copies its title or detail — the
/// plan is read at brief time, so a plan edit reaches the next step with no sync.
/// <para>A plan step says what should be true; a chain step says what happened. A <c>Done</c>
/// plan step with three failed chain attempts is a coherent, truthful state, and collapsing
/// the two would lose it.</para>
/// <para>Immutable: every mutation returns a copy. <see cref="ClaimedAt"/> is the claim
/// clock <see cref="ChainClaimPolicy"/> reads; a Running step whose claim has lapsed reads
/// as available through <see cref="EffectiveStatus"/> rather than being rewritten by a
/// sweeper.</para>
/// </summary>
public sealed record ChainStep(
  int Position,
  ChainStepStatus Status,
  string? SessionId,
  int Attempts,
  string? Outcome,
  ChainActor Actor,
  DateTimeOffset? ClaimedAt,
  DateTimeOffset? CompletedAt)
{
  /// <summary>A fresh Pending step. The actor starts as the harness: nothing has run yet.</summary>
  public static ChainStep Pending(int position)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    return new ChainStep(position, ChainStepStatus.Pending, null, 0, null, ChainActor.Harness, null, null);
  }

  /// <summary>True while this step's Running claim is still live. A Running step with NO
  ///     claim clock is treated as LAPSED, not as an eternal claim: the TTL exists so a
  ///     claim cannot outlive its process, and a missing clock is exactly that case.</summary>
  public bool IsClaimLive(DateTimeOffset now)
    => Status == ChainStepStatus.Running
        && ClaimedAt is { } claimed
        && now - claimed < ChainClaimPolicy.ClaimTtl;

  /// <summary>The status a reader should act on: a Running step whose claim lapsed reads as
  ///     <see cref="ChainStepStatus.Pending"/> (available again), every other status is
  ///     itself. Computed on read, never persisted — the stored row keeps the truth about
  ///     what was attempted.</summary>
  public ChainStepStatus EffectiveStatus(DateTimeOffset now)
    => Status == ChainStepStatus.Running && !IsClaimLive(now) ? ChainStepStatus.Pending : Status;

  /// <summary>True when the step may be claimed now: Pending, or a Running step whose claim
  ///     has lapsed. Done, Failed, Skipped and Parked steps are not claimable.</summary>
  public bool IsAvailable(DateTimeOffset now) => EffectiveStatus(now) == ChainStepStatus.Pending;

  /// <summary>Claims this step for a session: Pending (or a lapsed claim) becomes Running,
  ///     the attempt count increments, and the claim clock starts. A claim is an ATTEMPT —
  ///     that is what makes the count bound a session that keeps failing the same way.</summary>
  public ChainStep Claim(string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return this with
    {
      Status = ChainStepStatus.Running,
      SessionId = sessionId.Trim(),
      Attempts = Attempts + 1,
      Actor = ChainActor.Session(sessionId),
      ClaimedAt = now,
      CompletedAt = null,
    };
  }

  /// <summary>Records this step's outcome. <paramref name="status"/> must be one of the
  ///     SETTLED step statuses (Done, Failed, Skipped, Parked); a chain step does not move
  ///     back to Pending or Running through an outcome, it is claimed instead.</summary>
  public ChainStep Settle(ChainStepStatus status, ChainActor actor, string? outcome, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(actor);
    return status is ChainStepStatus.Done or ChainStepStatus.Failed
            or ChainStepStatus.Skipped or ChainStepStatus.Parked
      ? this with
      {
        Status = status,
        Outcome = outcome,
        Actor = actor,
        ClaimedAt = null,
        CompletedAt = now,
      }
      : throw new ArgumentException(
        $"'{status}' is not a settled chain step status; settle with Done, Failed, Skipped, or Parked.",
        nameof(status));
  }
}
