namespace eThangAgent.AgentDomain;

/// <summary>One session's spend as the telemetry report reads it (issue #309 D1).
///     <para>A SESSION THE CHAIN STARTED IS ONE ROW HERE, keyed by the id the chain recorded.
///     The cost itself is the usage ledger's — this carries the ledger's own answer, never a
///     second computation, because a second cost number would eventually disagree with the
///     ledger and the ledger is the one that is reconciled.</para>
///     <para><paramref name="Cost"/> IS NULLABLE AND NULL MEANS UNPRICED, never free: a session
///     whose calls could not be priced contributes no cost and is counted as unpriced, exactly
///     as <c>UsageTotal</c> treats it.</para></summary>
/// <param name="SessionId">The session the chain started.</param>
/// <param name="Cost">The ledger's total for that session, or null when nothing could be priced.
///     </param>
/// <param name="PricedCalls">How many calls contributed to <paramref name="Cost"/>.</param>
/// <param name="UnpricedCalls">How many calls could not be priced.</param>
public sealed record ChainTelemetrySessionCost(string SessionId, decimal? Cost, int PricedCalls,
    int UnpricedCalls)
{
  /// <summary>True when the ledger priced at least one call, so the figure is a measurement
  ///     rather than an absence.</summary>
  public bool IsPriced => PricedCalls > 0 && Cost is not null;
}

/// <summary>One recorded plan review as the telemetry report reads it (issue #309, #216 D7).
///     <para>THE SAME ROWS THE CHAIN CURSOR READS, through the same store: a calibration number
///     and an advance decision cannot then disagree about what a reviewer said.</para></summary>
/// <param name="PlanId">The plan the verdict judged.</param>
/// <param name="PlanVersion">The exact version it judged.</param>
/// <param name="Verdict">What the reviewer concluded.</param>
public sealed record ChainTelemetryReview(int PlanId, int PlanVersion, ChainReviewVerdict Verdict)
{
  /// <summary>True when the reviewer said no. Approve-with-conditions is NOT a denial — it lets
  ///     the work advance and carries its conditions forward, which is the distinction the
  ///     verdict type exists to express.</summary>
  public bool IsDenial => Verdict == ChainReviewVerdict.Deny;
}

/// <summary>The review rows plus the fact that makes 'did the deny change anything' answerable
///     (issue #309): each reviewed plan's CURRENT version.
///     <para>WHY BOTH: the review record is keyed on the version it JUDGED (#216 D1), so a
///     denial that produced a revision is only visible by comparing that version against the
///     plan's current one. Reading the reviews alone would let every denial look like it changed
///     nothing.</para></summary>
/// <param name="Reviews">Every review in the workspace, in creation order.</param>
/// <param name="CurrentPlanVersions">Each reviewed plan's current version.</param>
public sealed record ChainTelemetryReviewFacts(IReadOnlyList<ChainTelemetryReview> Reviews,
    IReadOnlyDictionary<int, int> CurrentPlanVersions)
{
  /// <summary>An empty set: nothing reviewed, nothing to compare.</summary>
  public static ChainTelemetryReviewFacts Empty { get; } =
      new([], new Dictionary<int, int>());

  /// <summary>True when <paramref name="review"/>'s plan has moved past the version it judged,
  ///     i.e. the review produced a revision.
  ///     <para>A PLAN THAT IS GONE READS AS UNCHANGED rather than as changed: the dictionary
  ///     cannot answer, and inventing an answer would count a deleted plan as a caught defect.
  ///     </para></summary>
  public bool ChangedAfter(ChainTelemetryReview review)
  {
    ArgumentNullException.ThrowIfNull(review);
    return CurrentPlanVersions.TryGetValue(review.PlanId, out int current)
        && current > review.PlanVersion;
  }
}
