namespace eThangAgent.AgentDomain;

/// <summary>One chain as the telemetry report sees it (issue #309): the chain's own facts plus
///     its steps' EXECUTION record. A READ model — nothing here mutates a chain.
///     <para>WHY A SEPARATE SHAPE AND NOT <see cref="Chain"/>: the report must survive a chain
///     row it cannot fully materialize, and it must read chains the caller did not load one at a
///     time. This carries exactly what the metrics need, so a metric cannot quietly start
///     depending on a field the query never asked for.</para></summary>
/// <param name="ChainId">The chain's id.</param>
/// <param name="Title">The chain's title, which derives its ledger slug.</param>
/// <param name="Status">The chain's status when it was last written.</param>
/// <param name="ParkReason">Why the chain parked, or null when it never did. The report groups
///     on this, so it is the record's own reason rather than one re-derived from a step.</param>
/// <param name="CreatedAt">When the chain was created — the wall-clock start.</param>
/// <param name="UpdatedAt">When the chain last moved — the wall-clock end for a settled chain.
///     </param>
/// <param name="Steps">The chain's steps, in position order.</param>
public sealed record ChainTelemetryChain(int ChainId, string Title, ChainStatus Status,
    string? ParkReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ChainTelemetryStep> Steps);

/// <summary>One chain step's execution record, as the report reads it (issue #309).
///     <para><paramref name="Attempts"/> IS THE RETRY METRIC and <paramref name="ReviewAttempts"/>
///     IS THE REVIEW METRIC: Q14 keeps those budgets separate, so the report reads them
///     separately. Summing them would report a demanding reviewer as a flaky session.</para>
///     <para>THE CLAIM AND COMPLETION CLOCKS ARE BOTH CARRIED, because a step that was CLAIMED
///     and never COMPLETED is the silent loss the started-settled ratio exists to expose — and
///     a null completion clock is how that step identifies itself.</para></summary>
/// <param name="Position">The 1-based plan step position this step executes.</param>
/// <param name="Status">The step's stored execution status.</param>
/// <param name="SessionId">The session the harness started for this step, or null when none
///     was ever assigned (a step still Pending). The telemetry report prices the ledger over
///     exactly these ids, so a null here is 'no session to ask about' rather than a zero.</param>
/// <param name="Attempts">How many times the step was claimed (the retry counter).</param>
/// <param name="ReviewAttempts">How many times a review sent the step back (the review counter,
///     separate from <paramref name="Attempts"/> by Q14).</param>
/// <param name="Role">The role assigned to the step.</param>
/// <param name="ClaimedAt">When the current (or last) claim started, or null.</param>
/// <param name="CompletedAt">When the step settled, or null when it never did.</param>
public sealed record ChainTelemetryStep(int Position, ChainStepStatus Status, string? SessionId,
    int Attempts, int ReviewAttempts, ChainStepRole Role, DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt)
{
  /// <summary>True when this step settled: Done, Failed, Skipped or Parked.
  ///     <para>DERIVED FROM THE STATUS, not from the completion clock, because the clock is what
  ///     a corrupt or legacy row can be missing while the status is what the state machine
  ///     enforces. A step whose status says settled and whose clock is absent is counted as
  ///     settled for the ratio and EXCLUDED from the wall-clock median, which is the honest
  ///     reading of a row that says it finished but not when.</para></summary>
  public bool IsSettled => Status is ChainStepStatus.Done or ChainStepStatus.Failed
      or ChainStepStatus.Skipped or ChainStepStatus.Parked;

  /// <summary>True when this step's work was accepted: Done, or Skipped as a deliberate exit.
  ///     <para>SKIPPED COUNTS AS COMPLETED and that is the #271 rule rather than a leniency:
  ///     'deliberately not doing this' is a truthful exit, and counting it as unfinished would
  ///     push a session toward marking real work Done to keep a number green.</para></summary>
  public bool IsCompleted => Status is ChainStepStatus.Done or ChainStepStatus.Skipped;

  /// <summary>True when this step exhausted the retry budget.
  ///     <para>COMPARED AGAINST <see cref="ChainRetryPolicy.MaxAttempts"/> rather than against a
  ///     literal, so the report and the cap cannot drift apart.</para></summary>
  public bool ReachedRetryCap => Attempts >= ChainRetryPolicy.MaxAttempts;

  /// <summary>True when the step was sent back by a review at least once.</summary>
  public bool WasReviewedBack => ReviewAttempts > 0;
}
