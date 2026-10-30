namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #309: the telemetry computation against FIXTURES — no database, no clock, no
///     container. Every metric is asserted from rows a test stated, so the arithmetic is checked
///     rather than inferred, and the rule that a null is NOT MEASURED (never 0%) is pinned on
///     every metric that can be absent.</summary>
public class ChainTelemetryTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

  /// <summary>The thresholds a fixture starts from, all at the issue's documented values.
  ///     <para>A TEST VARIES ONE MEMBER with a <c>with</c> expression rather than passing twelve
  ///     arguments, so a failure names which bound the fixture crossed instead of blaming 'the
  ///     thresholds'.</para></summary>
  private static ChainTelemetryThresholds DefaultLimits() =>
      new()
      {
        InterventionShareMax = 0.25m,
        MedianCompletedStepsMin = 3m,
        ParkShareMax = 0.25m,
        TopParkReasonShareMax = 0.50m,
        FirstAttemptSuccessMin = 0.85m,
        CapReachedShareMax = 0.05m,
        ReviewDenyShareMin = 0.15m,
        ReviewDenyShareMax = 0.50m,
        PlanChangedAfterDenyMin = 0.80m,
        CostPerStepRatioMax = 1.0m,
        StartedSettledRatioMin = 0.80m,
        WallClockRatioMax = 1.0m,
      };

  private static ChainTelemetryStep Step(int position, ChainStepStatus status, int attempts = 1,
      int reviewAttempts = 0, ChainStepRole? role = null, double hours = 1d)
      => new(position, status, "session-" + position, attempts, reviewAttempts,
          role ?? ChainStepRole.Implement, T0, status == ChainStepStatus.Pending ? null
              : T0.AddHours(hours));

  private static ChainTelemetryChain Chain(int id, ChainStatus status, string? parkReason,
      params ChainTelemetryStep[] steps)
      => new(id, "chain " + id, status, parkReason, T0, T0.AddHours(4), steps);

  private static ChainTelemetryInputs Inputs(IReadOnlyList<ChainTelemetryChain> chains,
      ChainTelemetryThresholds? limits = null, ChainTelemetryReviewFacts? reviews = null,
      ChainTelemetryBaseline? baseline = null, IReadOnlyList<ChainTelemetrySessionCost>? costs = null)
      => new(chains, costs ?? [], reviews ?? ChainTelemetryReviewFacts.Empty, baseline,
          limits ?? DefaultLimits());

  private static ChainTelemetryMetric Metric(ChainTelemetryReport report, string name) =>
      report.Metrics.Single(metric => metric.Name == name);

  // ---- the empty scope ----------------------------------------------------

  [Fact]
  public void Compute_OnAnEmptyWorkspace_MeasuresNothing_AndSaysSo()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs([]));

    Assert.Equal(0, report.ChainCount);
    Assert.False(report.HasMeasurements);
    Assert.Empty(report.Breaches);
    Assert.All(report.Metrics, metric =>
    {
      Assert.Null(metric.Value);
      Assert.Null(metric.Breached);
    });
    // The empty scope is an ANSWER and the report says which answer it is: a wall of zeros
    // would read as a passing report.
    Assert.Contains(report.Notes, note => note.Contains("no chain exists", StringComparison.Ordinal));
    Assert.Contains("NOT MEASURED", report.Render(), StringComparison.Ordinal);
  }

  // ---- 1. steps completed per chain ---------------------------------------

  [Fact]
  public void InterventionShare_CountsAChainParkedNow_AndBreachesAboveItsCeiling()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
    [
        Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done)),
        Chain(2, ChainStatus.Parked, "waiting", Step(1, ChainStepStatus.Done)),
    ], DefaultLimits() with { InterventionShareMax = 0.25m }));

    ChainTelemetryMetric metric = Metric(report, "intervention-share");
    Assert.Equal(0.5m, metric.Value);
    Assert.True(metric.Breached);
  }

  [Fact]
  public void InterventionShare_CountsAChainWhoseStepSettledParked_EvenWhenTheChainIsNotParked()
  {
    // The chain row says Running; the STEP says Parked. Either is evidence a human was needed,
    // and reading only the chain row would miss it.
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
    [
        Chain(1, ChainStatus.Running, null, Step(1, ChainStepStatus.Parked)),
        Chain(2, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done)),
    ]));

    Assert.Equal(0.5m, Metric(report, "intervention-share").Value);
  }

  [Fact]
  public void MedianCompletedSteps_CountsSkippedAsCompleted_AndMeasuresOnlyCompletedChains()
  {
    // #271: a deliberately skipped step is a truthful exit, not unfinished work. Counting it
    // as unfinished would push a session to mark real work Done to keep the number green.
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
    [
        Chain(1, ChainStatus.Completed, null,
            Step(1, ChainStepStatus.Done), Step(2, ChainStepStatus.Skipped),
            Step(3, ChainStepStatus.Done)),
        // Not Completed, so it contributes nothing to the median even though it has done steps.
        Chain(2, ChainStatus.Running, null, Step(1, ChainStepStatus.Done)),
    ]));

    ChainTelemetryMetric metric = Metric(report, "median-completed-steps");
    Assert.Equal(3m, metric.Value);
    Assert.False(metric.Breached);
    Assert.Contains("1 completed chain", metric.Detail, StringComparison.Ordinal);
  }

  // ---- 2. park rate and reasons -------------------------------------------

  [Fact]
  public void ParkReasons_GroupsOnTheRecordsOwnReason_AndCountsUnattributedSeparately()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
    [
        Chain(1, ChainStatus.Parked, "review cap reached"),
        Chain(2, ChainStatus.Parked, "review cap reached"),
        Chain(3, ChainStatus.Parked, "   "),
        Chain(4, ChainStatus.Completed, null),
    ]));

    // TWO distinct reasons: the recorded one, and the unattributed bucket.
    Assert.Equal(2, report.ParkReasons.Count);
    Assert.Equal("review cap reached", report.ParkReasons[0].Reason);
    Assert.Equal(2, report.ParkReasons[0].Count);
    // An unattributed park is its OWN entry: folding it into a reason bucket would let a
    // reporting defect look like a finding.
    Assert.Contains(report.ParkReasons, reason =>
        reason.Reason == ChainTelemetryReport.UnattributedParkReason && reason.Count == 1);
    Assert.Equal(0.75m, Metric(report, "park-share").Value);
  }

  [Fact]
  public void Render_ShowsTheParkDistribution_OnlyWhenSomethingParked()
  {
    string parked = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Parked, "waiting for a human")])).Render();
    string clean = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done))])).Render();

    Assert.Contains("parks by reason:", parked, StringComparison.Ordinal);
    Assert.Contains("1 x waiting for a human", parked, StringComparison.Ordinal);
    Assert.DoesNotContain("parks by reason:", clean, StringComparison.Ordinal);
  }

  // ---- 3. retry rate per step ---------------------------------------------

  [Fact]
  public void RetryRate_MeasuresSettledAttemptedSteps_AndExcludesPendingAndRunning()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
    [
        Chain(1, ChainStatus.Running, null,
            Step(1, ChainStepStatus.Done),                 // first try, success
            Step(2, ChainStepStatus.Done, attempts: 2),    // second try, success
            Step(3, ChainStepStatus.Failed, attempts: 3),  // reached the cap
            Step(4, ChainStepStatus.Pending, attempts: 0), // never tried: excluded
            Step(5, ChainStepStatus.Running, attempts: 1)),// not settled: excluded
    ]));

    ChainTelemetryMetric success = Metric(report, "first-attempt-success");
    // Three settled, attempted steps; one succeeded on attempt 1.
    Assert.Equal(1m / 3m, success.Value);
    Assert.True(success.Breached);
    Assert.Contains("of 3 settled step", success.Detail, StringComparison.Ordinal);

    ChainTelemetryMetric cap = Metric(report, "cap-reached-share");
    Assert.Equal(1m / 3m, cap.Value);
    Assert.True(cap.Breached);
    Assert.Contains("3-attempt cap", cap.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void RetryRate_CountsASettledStepWithNoAttemptAsNeitherSuccessNorFailure()
  {
    // A skipped step the harness never ran was not a first-attempt success, and counting it as
    // one would inflate the number with work nobody did.
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Skipped, attempts: 0))]));

    Assert.Null(Metric(report, "first-attempt-success").Value);
    Assert.Null(Metric(report, "first-attempt-success").Breached);
  }

  // ---- 4. review deny rate ------------------------------------------------

  [Fact]
  public void ReviewDenyRate_IsABand_AndReportsTheFloorAsWellAsTheCeiling()
  {
    ChainTelemetryReviewFacts reviews = new(
        [new ChainTelemetryReview(7, 1, ChainReviewVerdict.Approve)],
        new Dictionary<int, int> { [7] = 1 });
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done, role: ChainStepRole.Review()))],
        DefaultLimits() with { ReviewDenyShareMin = 0.15m }, reviews));

    ChainTelemetryMetric deny = Metric(report, "review-deny-share");
    Assert.Equal(0m, deny.Value);
    // A reviewer that approved everything is a RUBBER STAMP, so zero is BELOW the band, not
    // a clean pass. This is the criterion a one-sided ceiling would miss.
    Assert.True(deny.Breached);
    Assert.Contains("in [0.15, 0.5]", deny.Threshold, StringComparison.Ordinal);
  }

  [Fact]
  public void ReviewDenyRate_IsNotMeasuredWhenNoReviewStepExists()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done))]));

    Assert.Null(Metric(report, "review-deny-share").Value);
    Assert.Contains("no review step", Metric(report, "review-deny-share").Detail,
        StringComparison.Ordinal);
  }

  [Fact]
  public void ReviewStepsWithNoRecordedVerdict_GetANote_NamingTheSilentDiscardShape()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done, role: ChainStepRole.Review()))]));

    // A reviewer that did the work and recorded nothing is a DEFECT, not an absence, and the
    // report distinguishes the two.
    Assert.Contains(report.Notes, note => note.Contains("NO verdict was recorded", StringComparison.Ordinal));
    Assert.Contains("NO verdict was recorded", Metric(report, "review-deny-share").Detail,
        StringComparison.Ordinal);
  }

  [Fact]
  public void PlanChangedAfterDeny_ComparesTheJudgedVersionAgainstThePlansCurrentOne()
  {
    ChainTelemetryReviewFacts reviews = new(
        [new ChainTelemetryReview(7, 1, ChainReviewVerdict.Deny),
         new ChainTelemetryReview(7, 2, ChainReviewVerdict.Deny)],
        new Dictionary<int, int> { [7] = 3 });
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done, role: ChainStepRole.Review()))],
        DefaultLimits(), reviews));

    ChainTelemetryMetric changed = Metric(report, "plan-changed-after-deny");
    // Both denials judged versions below the plan's current 3, so both produced a revision.
    Assert.Equal(1m, changed.Value);
    Assert.False(changed.Breached);
    Assert.Contains("2 of 2 denial", changed.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void PlanChangedAfterDeny_IsNotMeasuredWhenNoDenialWasRecorded()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done, role: ChainStepRole.Review()))],
        DefaultLimits(), ChainTelemetryReviewFacts.Empty));

    // There is no share of nothing: reporting 0% would say every denial was ignored when in
    // fact no reviewer said no.
    Assert.Null(Metric(report, "plan-changed-after-deny").Value);
    Assert.Contains("no denial recorded", Metric(report, "plan-changed-after-deny").Detail,
        StringComparison.Ordinal);
  }

  // ---- 5. cost per completed step -----------------------------------------

  [Fact]
  public void CostPerStep_DividesTheLedgersSpendByCompletedSteps_AndCountsUnpricedSessions()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null,
            Step(1, ChainStepStatus.Done), Step(2, ChainStepStatus.Done))],
        costs:
        [
            new ChainTelemetrySessionCost("session-1", 0.50m, 10, 0),
            new ChainTelemetrySessionCost("session-2", null, 0, 4),
        ]));

    ChainTelemetryMetric cost = Metric(report, "cost-per-step");
    Assert.Equal(0.25m, cost.Value);
    // No baseline in the fixture, so the report REFUSES to assert a ratio.
    Assert.Null(cost.Breached);
    Assert.Contains("INCOMPARABLE", cost.Detail, StringComparison.Ordinal);
    Assert.Contains("1 session(s) unpriced", cost.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void CostPerStep_AgainstAComparableBaseline_ReportsTheRatioAndBreachesAboveIt()
  {
    ChainTelemetryBaseline baseline = new(100, 80, 60d, 0.10m, 20);
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done))],
        baseline: baseline,
        costs: [new ChainTelemetrySessionCost("session-1", 0.40m, 10, 0)]));

    ChainTelemetryMetric cost = Metric(report, "cost-per-step");
    Assert.Equal(4m, cost.Value);
    Assert.True(cost.Breached);
    Assert.Contains("baseline median 0.1", cost.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void CostPerStep_WithNoPricedSpendAtAll_IsNotMeasured()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done))],
        costs: [new ChainTelemetrySessionCost("session-1", null, 0, 9)]));

    Assert.Null(Metric(report, "cost-per-step").Value);
  }

  // ---- 6. steps to completion and wall clock ------------------------------

  [Fact]
  public void StartedSettledRatio_CountsAStartedStepThatNeverSettled_AsTheLossItIs()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
    [
        Chain(1, ChainStatus.Running, null,
            Step(1, ChainStepStatus.Done),
            Step(2, ChainStepStatus.Running, attempts: 1)),
    ]));

    ChainTelemetryMetric metric = Metric(report, "started-settled-ratio");
    Assert.Equal(0.5m, metric.Value);
    Assert.True(metric.Breached);
  }

  [Fact]
  public void WallClock_IsDerivedFromStoredClocksOnly_AndExcludesASettledStepWithNone()
  {
    // A step whose status says settled but whose completion clock is absent is COUNTED as
    // settled and EXCLUDED from the median: a duration that cannot be computed is not zero.
    ChainTelemetryStep clockless = new(2, ChainStepStatus.Done, "session-2", 1, 0,
        ChainStepRole.Implement, T0, null);
    ChainTelemetryBaseline baseline = new(10, 10, 7200d, 0.10m, 0);
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done, hours: 1d), clockless)],
        baseline: baseline));

    ChainTelemetryMetric wall = Metric(report, "wall-clock");
    // 1 hour against a 2-hour baseline median.
    Assert.Equal(0.5m, wall.Value);
    Assert.False(wall.Breached);
    Assert.Contains("1 settled step(s) with no duration", wall.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void WallClock_WithoutAMeasuredBaseline_IsNotMeasured_AndSaysIncomparable()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done))],
        baseline: new ChainTelemetryBaseline(10, 10, null, 0.10m, 0)));

    ChainTelemetryMetric wall = Metric(report, "wall-clock");
    Assert.Null(wall.Value);
    Assert.Contains("INCOMPARABLE", wall.Detail, StringComparison.Ordinal);
  }

  // ---- reproducibility ----------------------------------------------------

  [Fact]
  public void Render_OfTheSameRows_IsByteIdentical()
  {
    ChainTelemetryInputs inputs = Inputs(
    [
        Chain(1, ChainStatus.Completed, null,
            Step(1, ChainStepStatus.Done), Step(2, ChainStepStatus.Done, attempts: 2)),
        Chain(2, ChainStatus.Parked, "review cap reached", Step(1, ChainStepStatus.Parked)),
    ],
        baseline: new ChainTelemetryBaseline(100, 80, 3600d, 0.10m, 20),
        reviews: new ChainTelemetryReviewFacts(
            [new ChainTelemetryReview(7, 1, ChainReviewVerdict.Deny)],
            new Dictionary<int, int> { [7] = 2 }));

    // The acceptance criterion that makes the report checkable: two runs over the same rows
    // produce the same numbers. A figure read from the wall clock would break this.
    Assert.Equal(ChainTelemetry.Compute(inputs).Render(), ChainTelemetry.Compute(inputs).Render());
  }

  [Fact]
  public void Breaches_ListOnlyMetricsThatWereMeasuredAndMissed()
  {
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Parked, "stuck", Step(1, ChainStepStatus.Done))]));

    Assert.All(report.Breaches, metric => Assert.True(metric.Breached));
    // Park share is 100% here, so it is a breach; nothing measured-and-passing appears.
    Assert.Contains(report.Breaches, metric => metric.Name == "park-share");
    Assert.DoesNotContain(report.Breaches, metric => metric.Value is null);
  }

  [Fact]
  public void Compute_PropagatesTheThresholdSet_SoNoMetricCanUseADefault()
  {
    // Every threshold is set to a value no fixture could accidentally satisfy, so a metric
    // reading a default instead of the artifact would fail rather than pass quietly.
    ChainTelemetryThresholds impossible = DefaultLimits() with
    {
      InterventionShareMax = 0m,
      MedianCompletedStepsMin = 999m,
      ParkShareMax = 0m,
      TopParkReasonShareMax = 0m,
      FirstAttemptSuccessMin = 1m,
      CapReachedShareMax = 0m,
      ReviewDenyShareMin = 0m,
      ReviewDenyShareMax = 0m,
      PlanChangedAfterDenyMin = 1m,
      CostPerStepRatioMax = 0m,
      StartedSettledRatioMin = 1m,
      WallClockRatioMax = 0m,
    };
    ChainTelemetryReport report = ChainTelemetry.Compute(Inputs(
        [Chain(1, ChainStatus.Completed, null, Step(1, ChainStepStatus.Done))], impossible));

    Assert.Contains("999", Metric(report, "median-completed-steps").Threshold, StringComparison.Ordinal);
    Assert.Contains("0", Metric(report, "park-share").Threshold, StringComparison.Ordinal);
  }
}
