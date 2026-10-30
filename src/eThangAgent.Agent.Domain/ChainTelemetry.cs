using System.Globalization;
using eThangAgent.ModelDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Everything a telemetry report is computed from (issue #309), gathered by the
///     caller so the arithmetic stays PURE.
///     <para>WHY AN INPUTS RECORD AND NOT A STORE: the computation is the thing the issue is
///     about, and a pure function over facts is unit-testable against fixtures with no
///     database, no clock and no container — the <c>ChainAdvancePolicy</c> and
///     <c>UsageStatistics</c> precedent. The ADAPTER that reads the rows is a separate
///     concern, and separating them is what makes 'the same rows produce the same numbers'
///     checkable rather than asserted.</para>
///     <para>NO CLOCK IS CARRIED. Every time figure is derived from a stored timestamp, which
///     is what makes two runs over the same rows byte-identical — a report that read the wall
///     clock would differ on every run and its reproducibility criterion would be
///     unfalsifiable.</para></summary>
/// <param name="Chains">The chains in scope, with their steps.</param>
/// <param name="SessionCosts">The ledger's totals for the sessions those chains started.
///     </param>
/// <param name="Reviews">The plan review record, plus each reviewed plan's current version.
///     </param>
/// <param name="Baseline">The subagent-era baseline, or null when nothing was measured.</param>
/// <param name="Thresholds">The thresholds fixed before the first report (D4/D5). Required:
///     a report without them would be the unfalsifiable claim this issue opens with.</param>
public sealed record ChainTelemetryInputs(
    IReadOnlyList<ChainTelemetryChain> Chains,
    IReadOnlyList<ChainTelemetrySessionCost> SessionCosts,
    ChainTelemetryReviewFacts Reviews,
    ChainTelemetryBaseline? Baseline,
    ChainTelemetryThresholds Thresholds);

/// <summary>What the persisted rows say about how chains ran (issue #309), as a PURE function
///     over <see cref="ChainTelemetryInputs"/>.
///     <para>THE METRICS ARE THE ISSUE'S SIX, each stated as a threshold in the artifact the
///     implementing session wrote BEFORE any of these numbers existed. This type computes; it
///     never chooses a bound.</para>
///     <para>NOTHING IS SELF-REPORTED. Every input is a stored row: the chain record's own
///     status/attempts/clocks (D2), the usage ledger's per-call rows summed by session (D1), and
///     the plan review record. A session's prose about how it went is not an input anywhere.
///     </para>
///     <para>NULL MEANS NOT MEASURED AND NEVER MEANS ZERO, throughout: a rate over an empty
///     denominator is absent, not 0%, and its <c>Breached</c> is absent rather than false. That
///     is the one rule that keeps a green report from implying a judgment nobody made.</para>
///     </summary>
public static class ChainTelemetry
{
  /// <summary>Computes the report. Total: any input, including an empty workspace, produces a
  ///     report rather than a failure — 'nothing has run yet' is an answer, and a failure would
  ///     make a caller distinguish it from a broken read for no reason.</summary>
  /// <param name="inputs">The gathered facts and the fixed thresholds.</param>
  public static ChainTelemetryReport Compute(ChainTelemetryInputs inputs)
  {
    ArgumentNullException.ThrowIfNull(inputs);
    ChainTelemetryThresholds limits = inputs.Thresholds;
    List<ChainTelemetryChain> chains = [.. inputs.Chains];
    List<ChainTelemetryStep> steps = [.. chains.SelectMany(chain => chain.Steps)];
    List<ChainTelemetryMetric> metrics =
    [
        .. StepsPerChain(chains, limits),
        ParkRate(chains, limits),
        .. RetryRate(steps, limits),
        .. ReviewRate(steps, inputs.Reviews, limits),
        CostPerStep(steps, inputs, limits),
        .. Completion(steps, inputs.Baseline, limits),
    ];
    return new ChainTelemetryReport(chains.Count, metrics, ParkReasons(chains),
        inputs.Baseline, Notes(chains, steps, inputs));
  }

  // ---- 1. steps completed per chain ---------------------------------------

  /// <summary>The intervention share and the median completed steps of a completed chain.
  ///     <para>INTERVENTION IS MEASURED FROM THE RECORD'S OWN EVIDENCE, and the evidence is
  ///     narrower than it looks: <c>Chain.Resume</c> CLEARS the park reason, so a chain that
  ///     parked and was resumed leaves no trace on the chain row. A step settled Parked DOES
  ///     survive, so both are counted and the report says which limitation applies — a rate
  ///     computed from partial evidence must not be presented as complete.</para></summary>
  private static IReadOnlyList<ChainTelemetryMetric> StepsPerChain(
      List<ChainTelemetryChain> chains, ChainTelemetryThresholds limits)
  {
    int interventions = chains.Count(HasParkEvidence);
    List<decimal> completedSteps =
    [
        .. chains.Where(chain => chain.Status == ChainStatus.Completed)
            .Select(chain => (decimal)chain.Steps.Count(step => step.IsCompleted)),
    ];
    decimal? share = Share(interventions, chains.Count);
    decimal? median = Median(completedSteps);
    return
    [
        Metric("intervention-share", share, "<= " + Number(limits.InterventionShareMax),
            share is { } s && s <= limits.InterventionShareMax,
            $"{interventions} of {chains.Count} chain(s) show a park"),
        Metric("median-completed-steps", median, ">= " + Number(limits.MedianCompletedStepsMin),
            median is { } m && m >= limits.MedianCompletedStepsMin,
            $"{completedSteps.Count} completed chain(s) in scope"),
    ];
  }

  /// <summary>True when a chain's record shows it needed a human: it is parked now, or one of
  ///     its steps settled Parked. The two are separate facts and either is sufficient.</summary>
  private static bool HasParkEvidence(ChainTelemetryChain chain) =>
      chain.Status == ChainStatus.Parked || chain.Steps.Any(step => step.Status == ChainStepStatus.Parked);

  // ---- 2. park rate and reason distribution -------------------------------

  /// <summary>The park share, and the largest single reason's share of all parks.
  ///     <para>THE TOP-REASON SHARE IS A SEPARATE METRIC because one dominant reason is a
  ///     specific defect rather than general flakiness, and averaging it into a rate would hide
  ///     the finding. An unattributed park is its OWN entry and is counted, never folded into a
  ///     reason bucket: it is a reporting defect and must read as one.</para></summary>
  private static ChainTelemetryMetric ParkRate(List<ChainTelemetryChain> chains,
      ChainTelemetryThresholds limits)
  {
    int parks = chains.Count(chain => chain.Status == ChainStatus.Parked);
    decimal? share = Share(parks, chains.Count);
    return Metric("park-share", share, "<= " + Number(limits.ParkShareMax),
        share is { } s && s <= limits.ParkShareMax,
        $"{parks} of {chains.Count} chain(s) parked");
  }

  /// <summary>Park counts per reason, most frequent first.
  ///     <para>GROUPED ON THE RECORD'S OWN REASON, verbatim: bucketing into categories would
  ///     invent the vocabulary the reader is meant to see, and the threshold is about one reason
  ///     dominating rather than about a taxonomy.</para></summary>
  private static IReadOnlyList<ChainTelemetryParkReason> ParkReasons(
      List<ChainTelemetryChain> chains) =>
  [
      .. chains.Where(chain => chain.Status == ChainStatus.Parked)
          .GroupBy(chain => string.IsNullOrWhiteSpace(chain.ParkReason)
              ? ChainTelemetryReport.UnattributedParkReason
              : chain.ParkReason.Trim(), StringComparer.Ordinal)
          .Select(group => new ChainTelemetryParkReason(group.Key, group.Count()))
          .OrderByDescending(reason => reason.Count)
          .ThenBy(reason => reason.Reason, StringComparer.Ordinal),
  ];

  // ---- 3. retry rate per step ---------------------------------------------

  /// <summary>First-attempt success and cap-reached share, over SETTLED ATTEMPTED steps.
  ///     <para>THE DENOMINATOR IS SETTLED STEPS THAT WERE ATTEMPTED, and each exclusion is
  ///     deliberate: a Pending step was never tried, and a Running step has not answered yet, so
  ///     counting either would move the rate on work that has not happened. A settled step with
  ///     zero attempts is a skip the harness never ran, and counting it as a first-attempt
  ///     success would inflate the number with work nobody did.</para></summary>
  private static IReadOnlyList<ChainTelemetryMetric> RetryRate(
      IReadOnlyList<ChainTelemetryStep> steps, ChainTelemetryThresholds limits)
  {
    List<ChainTelemetryStep> attempted =
    [
        .. steps.Where(step => step.IsSettled && step.Attempts > 0),
    ];
    int firstTry = attempted.Count(step => step.Attempts == 1 && step.IsCompleted);
    int atCap = attempted.Count(step => step.ReachedRetryCap);
    decimal? success = Share(firstTry, attempted.Count);
    decimal? capped = Share(atCap, attempted.Count);
    return
    [
        Metric("first-attempt-success", success, ">= " + Number(limits.FirstAttemptSuccessMin),
            success is { } s && s >= limits.FirstAttemptSuccessMin,
            $"{firstTry} of {attempted.Count} settled step(s) succeeded on attempt 1"),
        Metric("cap-reached-share", capped, "<= " + Number(limits.CapReachedShareMax),
            capped is { } c && c <= limits.CapReachedShareMax,
            $"{atCap} of {attempted.Count} settled step(s) reached the "
            + $"{ChainRetryPolicy.MaxAttempts}-attempt cap"),
    ];
  }

  // ---- 4. review deny rate and plan change after deny ---------------------

  /// <summary>The deny rate over recorded verdicts, and how often a denial produced a revision.
  ///     <para>BOTH ARE ABSENT WHEN THERE IS NOTHING TO MEASURE, and the report distinguishes the
  ///     two absences: no review STEP in scope means reviews are not part of how these chains
  ///     run, while review steps with no recorded verdict is the silent-discard shape #320
  ///     closed — a reviewer that did the work and recorded nothing. The second gets a NOTE,
  ///     because it is a defect rather than an absence.</para></summary>
  private static IReadOnlyList<ChainTelemetryMetric> ReviewRate(
      IReadOnlyList<ChainTelemetryStep> steps, ChainTelemetryReviewFacts reviews,
      ChainTelemetryThresholds limits)
  {
    int reviewSteps = steps.Count(step => step.Role.IsReview);
    List<ChainTelemetryReview> recorded = [.. reviews.Reviews];
    bool measurable = reviewSteps > 0 && recorded.Count > 0;
    int denials = recorded.Count(review => review.IsDenial);
    decimal? denyRate = measurable ? Share(denials, recorded.Count) : null;
    return
    [
        Metric("review-deny-share", denyRate,
            "in [" + Number(limits.ReviewDenyShareMin) + ", " + Number(limits.ReviewDenyShareMax) + "]",
            denyRate is { } rate && rate >= limits.ReviewDenyShareMin && rate <= limits.ReviewDenyShareMax,
            ReviewDetail(reviewSteps, recorded.Count, denials)),
        ChangedAfterDeny(denials, reviews, limits),
    ];
  }

  /// <summary>The deny rate's detail line, which says WHY it is absent when it is.</summary>
  private static string ReviewDetail(int reviewSteps, int recorded, int denials) =>
      reviewSteps == 0
          ? "no review step in scope, so nothing was judged"
          : VerdictDetail(reviewSteps, recorded, denials);

  /// <summary>The detail for a scope that DOES hold review steps: a missing verdict is called
  ///     out as a defect rather than reported as an absence, because a reviewer that recorded
  ///     nothing is the silent-discard shape #320 closed.</summary>
  private static string VerdictDetail(int reviewSteps, int recorded, int denials) =>
      recorded == 0
          ? $"{reviewSteps} review step(s) in scope but NO verdict was recorded"
          : $"{denials} denial(s) of {recorded} recorded verdict(s)";

  /// <summary>How many denials produced a plan revision.
  ///     <para>ABSENT WHEN NO DENIAL WAS RECORDED — there is no share of nothing, and reporting
  ///     0% would say every denial was ignored when in fact no reviewer said no.</para>
  ///     <para>THE COMPARISON IS VERSION-BASED, which is what the record can actually answer:
  ///     the review is keyed on the version it JUDGED, so a plan whose current version is higher
  ///     was revised after that verdict. Whether the revision addressed the finding is not
  ///     knowable from these rows and is not claimed.</para></summary>
  private static ChainTelemetryMetric ChangedAfterDeny(int denials,
      ChainTelemetryReviewFacts reviews, ChainTelemetryThresholds limits)
  {
    List<ChainTelemetryReview> denied = [.. reviews.Reviews.Where(review => review.IsDenial)];
    int changed = denied.Count(reviews.ChangedAfter);
    decimal? share = denials == 0 ? null : Share(changed, denials);
    return Metric("plan-changed-after-deny", share, ">= " + Number(limits.PlanChangedAfterDenyMin),
        share is { } s && s >= limits.PlanChangedAfterDenyMin,
        denials == 0
            ? "no denial recorded, so there is no share to compute"
            : $"{changed} of {denials} denial(s) produced a plan revision");
  }

  // ---- 5. cost per completed step -----------------------------------------

  /// <summary>The chain's own cost per completed step, and its ratio against the baseline.
  ///     <para>THE COST IS THE LEDGER'S, summed over the sessions the chain started (D1). A
  ///     session that could not be priced contributes nothing and is COUNTED, so a partial total
  ///     is never presented as the whole bill.</para>
  ///     <para>THE RATIO IS THE THRESHOLD, not an absolute figure: the baseline is the thing
  ///     worth beating, and an absolute currency ceiling would be a number chosen against no
  ///     comparison at all. When the baseline cannot express one, the metric says INCOMPARABLE
  ///     and reports the raw figure alone rather than asserting a comparison it could not
  ///     compute.</para></summary>
  private static ChainTelemetryMetric CostPerStep(IReadOnlyList<ChainTelemetryStep> steps,
      ChainTelemetryInputs inputs, ChainTelemetryThresholds limits)
  {
    int completed = steps.Count(step => step.IsCompleted);
    decimal priced = inputs.SessionCosts.Where(cost => cost.IsPriced)
        .Sum(cost => cost.Cost ?? 0m);
    int unpriced = inputs.SessionCosts.Count(cost => !cost.IsPriced);
    decimal? perStep = completed == 0 || priced <= 0m ? null : priced / completed;
    return inputs.Baseline is { IsComparable: true } baseline
        ? CostRatio(perStep, baseline, unpriced, completed, limits)
        : Metric("cost-per-step", perStep, "<= " + Number(limits.CostPerStepRatioMax) + "x baseline",
            null, CostDetail(perStep, unpriced, completed)
            + "; INCOMPARABLE - the baseline has no priced completed run");
  }

  /// <summary>The cost ratio against a baseline that can express one.</summary>
  private static ChainTelemetryMetric CostRatio(decimal? perStep, ChainTelemetryBaseline baseline,
      int unpriced, int completed, ChainTelemetryThresholds limits)
  {
    decimal baselineCost = baseline.MedianCostPerCompletedRun!.Value;
    decimal? ratio = perStep is { } cost ? cost / baselineCost : null;
    return Metric("cost-per-step", ratio, "<= " + Number(limits.CostPerStepRatioMax) + "x baseline",
        ratio is { } r && r <= limits.CostPerStepRatioMax,
        CostDetail(perStep, unpriced, completed) + "; baseline median " + Number(baselineCost));
  }

  /// <summary>The cost metric's detail line, naming the unpriced share when there is one.</summary>
  private static string CostDetail(decimal? perStep, int unpriced, int completed)
  {
    string priced = perStep is { } value ? Number(value) + " per completed step" : "no priced spend";
    return unpriced == 0
        ? $"{priced} over {completed} completed step(s)"
        : $"{priced} over {completed} completed step(s), {unpriced} session(s) unpriced";
  }

  // ---- 6. steps to completion and wall clock ------------------------------

  /// <summary>The started-settled ratio and the wall-clock ratio against the baseline.
  ///     <para>STARTED MEANS ATTEMPTED: a step with attempts is one a session was started for,
  ///     and a step that was started and never settled is the silent loss this ratio exists to
  ///     expose.</para>
  ///     <para>THE WALL CLOCK IS DERIVED FROM STORED CLOCKS ONLY, so the report is reproducible;
  ///     a step with a claim but no completion clock is EXCLUDED from the median and counted as
  ///     unmeasured, because a duration that cannot be computed is not a duration of zero.
  ///     </para></summary>
  private static IReadOnlyList<ChainTelemetryMetric> Completion(IReadOnlyList<ChainTelemetryStep> steps,
      ChainTelemetryBaseline? baseline, ChainTelemetryThresholds limits)
  {
    List<ChainTelemetryStep> started = [.. steps.Where(step => step.Attempts > 0)];
    int settled = started.Count(step => step.IsSettled);
    decimal? ratio = Share(settled, started.Count);
    return
    [
        Metric("started-settled-ratio", ratio, ">= " + Number(limits.StartedSettledRatioMin),
            ratio is { } r && r >= limits.StartedSettledRatioMin,
            $"{settled} of {started.Count} started step(s) settled"),
        WallClock(started, baseline, limits),
    ];
  }

  /// <summary>The median step duration and its ratio against the baseline's median run.
  ///     <para>A CHAIN STEP AND A BASELINE RUN ARE NOT THE SAME UNIT, and the report says so
  ///     rather than letting the ratio imply equivalence: the comparison is between 'one step
  ///     finished' and 'one child run finished', which is the closest pair the rows offer.
  ///     </para></summary>
  private static ChainTelemetryMetric WallClock(IReadOnlyList<ChainTelemetryStep> started,
      ChainTelemetryBaseline? baseline, ChainTelemetryThresholds limits)
  {
    List<double> seconds =
    [
        .. started.Where(step => step is { ClaimedAt: not null, CompletedAt: not null })
            .Select(step => (step.CompletedAt!.Value - step.ClaimedAt!.Value).TotalSeconds)
            .Where(value => value >= 0d),
    ];
    double? median = UsageStatistics.Median(seconds);
    int unmeasured = started.Count(step => step.CompletedAt is null && step.IsSettled);
    return baseline is { HasDuration: true } measured && median is { } value
        ? WallClockRatio(value, measured, unmeasured, limits)
        : Metric("wall-clock", null, "<= " + Number(limits.WallClockRatioMax) + "x baseline",
            null, WallDetail(median, unmeasured, seconds.Count)
            + "; INCOMPARABLE - the baseline has no measured run duration");
  }

  /// <summary>The wall-clock ratio against a baseline that has a measured duration.</summary>
  private static ChainTelemetryMetric WallClockRatio(double median, ChainTelemetryBaseline baseline,
      int unmeasured, ChainTelemetryThresholds limits)
  {
    double baselineSeconds = baseline.MedianDurationSeconds!.Value;
    decimal ratio = (decimal)median / (decimal)baselineSeconds;
    return Metric("wall-clock", ratio, "<= " + Number(limits.WallClockRatioMax) + "x baseline",
        ratio <= limits.WallClockRatioMax,
        WallDetail(median, unmeasured, -1) + "; baseline median "
        + Number((decimal)baselineSeconds) + "s");
  }

  /// <summary>The wall-clock metric's detail line. A negative <paramref name="measuredCount"/>
  ///     means the caller already stated the baseline and only the step side is wanted.</summary>
  private static string WallDetail(double? median, int unmeasured, int measuredCount)
  {
    string figure = median is { } value
        ? Number((decimal)value) + "s median per settled step"
        : "no settled step carries both clocks";
    string excluded = unmeasured == 0
        ? string.Empty
        : $", {unmeasured} settled step(s) with no duration";
    string counted = measuredCount < 0 ? string.Empty : $" over {measuredCount} step(s)";
    return figure + counted + excluded;
  }

  // ---- notes --------------------------------------------------------------

  /// <summary>The facts a reader needs to read the numbers correctly.
  ///     <para>EVERY NOTE NAMES A LIMIT OF THE RECORD, never a caveat about effort: a resumed
  ///     park that left no trace, an empty scope, a baseline answered from a wider scope. A
  ///     report that presented these as clean measurements would be the confident lie the
  ///     nullable-price rule exists to prevent.</para></summary>
  private static List<string> Notes(IReadOnlyList<ChainTelemetryChain> chains,
      IReadOnlyList<ChainTelemetryStep> steps, ChainTelemetryInputs inputs)
  {
    List<string> notes = [];
    AddScopeNote(notes, chains);
    AddParkNote(notes, chains);
    AddReviewNote(notes, steps, inputs.Reviews);
    AddBaselineNote(notes, inputs.Baseline);
    return notes;
  }

  private static void AddScopeNote(List<string> notes, IReadOnlyList<ChainTelemetryChain> chains)
  {
    if (chains.Count == 0)
    {
      notes.Add("no chain exists in this workspace yet, so every metric is NOT MEASURED. "
          + "This is an empty scope, not a passing report.");
    }
  }

  private static void AddParkNote(List<string> notes, IReadOnlyList<ChainTelemetryChain> chains)
  {
    if (!chains.Any(HasParkEvidence))
    {
      return;
    }

    notes.Add("intervention and park rates read the record's surviving evidence (a chain parked "
        + "now, or a step settled Parked). Chain.Resume CLEARS a chain's park reason, so a park "
        + "that was resumed is invisible on the chain row and these two rates UNDERCOUNT it.");
  }

  private static void AddReviewNote(List<string> notes, IReadOnlyList<ChainTelemetryStep> steps,
      ChainTelemetryReviewFacts reviews)
  {
    int reviewSteps = steps.Count(step => step.Role.IsReview);
    if (reviewSteps > 0 && reviews.Reviews.Count == 0)
    {
      notes.Add($"{reviewSteps} review step(s) exist but NO verdict was recorded: the deny rate "
          + "cannot be computed, and a reviewer that recorded nothing is the silent-discard "
          + "shape #320 closed.");
    }
  }

  private static void AddBaselineNote(List<string> notes, ChainTelemetryBaseline? baseline)
  {
    if (baseline is null)
    {
      notes.Add("no subagent-era baseline could be read, so no cost or wall-clock ratio is "
          + "asserted. The raw figures are reported alone.");
    }
  }

  // ---- shared helpers -----------------------------------------------------

  /// <summary>A metric whose value is null when <paramref name="denominator"/> is zero.
  ///     <para>THIS IS THE ONE PLACE 'no denominator means not measured' is enforced, so no
  ///     caller can accidentally report a 0% that reads as a measurement.</para></summary>
  private static decimal? Share(int numerator, int denominator) =>
      denominator == 0 ? null : (decimal)numerator / denominator;

  /// <summary>The median of a set, or null when it is empty. Delegated to the Model Domain's
  ///     one implementation so this report and the performance read model cannot disagree.
  ///     </summary>
  private static decimal? Median(List<decimal> values)
  {
    if (values.Count == 0)
    {
      return null;
    }

    // The Model Domain's median cannot answer null here (the list is non-empty), and the
    // cast back to decimal is exact for the figures this report measures: counts and
    // ratios that were themselves decimals.
    double median = UsageStatistics.Median([.. values.Select(value => (double)value)])
        ?? throw new InvalidOperationException("a non-empty measurement set has no median.");
    return (decimal)median;
  }

  /// <summary>Builds one metric, with the breach rule stated once: a metric that was NOT
  ///     COMPARED is neither a breach nor a pass.
  ///     <para>THE COMPARISON IS WHAT DECIDES, not the value's presence. A cost per step with no
  ///     comparable baseline HAS a measured value and nothing to judge it against, so reporting
  ///     it as a breach would flag a chain for missing a comparison it was never given — and
  ///     reporting it as clean would be the green gate implying a judgment nobody made. Both
  ///     absences collapse to 'not tested'.</para></summary>
  private static ChainTelemetryMetric Metric(string name, decimal? value, string threshold,
      bool? cleared, string detail) =>
      new(name, value, threshold, Breached(value, cleared), detail);

  /// <summary>True when the metric was measured, was compared, and missed. Null otherwise —
  ///     'not measured' and 'not compared' are both 'not tested'.</summary>
  private static bool? Breached(decimal? value, bool? cleared) =>
      value is null || cleared is null ? null : !cleared.Value;

  /// <summary>A number as the report renders it: invariant, and without a trailing run of
  ///     zeros that makes a ratio harder to read than it needs to be.</summary>
  private static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
