using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Computes the chain telemetry report (issue #309) from persisted rows, and refuses
///     to compute it at all until the thresholds are fixed in a durable artifact.
///     <para>THE ORDERING IS STRUCTURAL, NOT ADVISORY (D5). The service reads the thresholds
///     key FIRST and fails with a named refusal when it is absent or unparseable, so there is no
///     code path that produces a report before a threshold set exists. That is what makes 'the
///     thresholds were chosen before the measurement' checkable from artifacts: the artifact's
///     timestamp precedes the first report because a report could not have been produced without
///     it.</para>
///     <para>IT REFUSES RATHER THAN INVENTING THRESHOLDS. Defaulting a missing key to a
///     plausible number would restore exactly the failure the decision exists to prevent — a
///     bound nobody chose, compared against a measurement that then looks judged.</para>
///     <para>THE ARITHMETIC IS <see cref="ChainTelemetry"/>, a pure function; this class only
///     gathers. The split is what makes 'the same rows produce the same numbers' testable: the
///     pure half has no store, no clock and no container.</para>
///     <para>COST IS NEVER RECORDED HERE (D1) and step outcomes are never duplicated (D2): the
///     inputs are the usage ledger's own rows and the chain record's own columns.</para></summary>
/// <param name="chains">The chain record, read for every chain in the workspace.</param>
/// <param name="costs">The usage ledger, read in one batch over the sessions a chain started.
///     </param>
/// <param name="reviews">The plan review record, read through the composition's adapter so the
///     report and the chain cursor see the same rows.</param>
/// <param name="baseline">The subagent-era rows, or a source that answers null.</param>
/// <param name="thresholds">Where the fixed thresholds live: a state-key read, so the artifact
///     is the same one a human can inspect with <c>state.get</c>.</param>
public sealed class ChainTelemetryService(IChainTelemetrySource chains, IChainSessionCostSource costs,
    IChainReviewTelemetrySource reviews, IChainBaselineSource baseline, IChainThresholdSource thresholds)
{
  private readonly IChainTelemetrySource _chains = chains
      ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainSessionCostSource _costs = costs
      ?? throw new ArgumentNullException(nameof(costs));
  private readonly IChainReviewTelemetrySource _reviews = reviews
      ?? throw new ArgumentNullException(nameof(reviews));
  private readonly IChainBaselineSource _baseline = baseline
      ?? throw new ArgumentNullException(nameof(baseline));
  private readonly IChainThresholdSource _thresholds = thresholds
      ?? throw new ArgumentNullException(nameof(thresholds));

  /// <summary>The report for <paramref name="chainTitle"/>'s chain, or a typed refusal when the
  ///     thresholds artifact is missing or invalid.
  ///     <para>THE TITLE SELECTS THE ARTIFACT because the thresholds key is derived from the
  ///     ledger slug (<see cref="ChainTelemetryThresholds.KeyFor"/>), which is what keeps a
  ///     chain's thresholds beside its ledger. The report itself covers every chain in the
  ///     workspace: the metrics are about how chains RUN, and one chain's run is not a
  ///     distribution.</para></summary>
  /// <param name="chainTitle">The chain whose thresholds artifact governs this report.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainTelemetryReport>> BuildAsync(string? chainTitle,
      CancellationToken ct = default)
  {
    Result<ChainTelemetryThresholds> limits = await _thresholds
        .ReadAsync(ChainTelemetryThresholds.KeyFor(chainTitle), ct).ConfigureAwait(false);
    if (!limits.IsSuccess)
    {
      return Result.Failure<ChainTelemetryReport>(limits.Error);
    }

    Result<ChainTelemetryInputs> inputs = await GatherAsync(limits.Value, ct).ConfigureAwait(false);
    return inputs.IsSuccess
        ? Result.Success(ChainTelemetry.Compute(inputs.Value))
        : Result.Failure<ChainTelemetryReport>(inputs.Error);
  }

  /// <summary>Gathers every input the computation needs. Each read's failure is PROPAGATED
  ///     rather than substituted: a report computed over a silently missing half would present
  ///     itself as complete, which is worse than an error naming what could not be read.
  ///     </summary>
  private async Task<Result<ChainTelemetryInputs>> GatherAsync(ChainTelemetryThresholds limits,
      CancellationToken ct)
  {
    Result<ChainTelemetryCatalog> catalog = await _chains.ListAsync(ct).ConfigureAwait(false);
    if (!catalog.IsSuccess)
    {
      return Result.Failure<ChainTelemetryInputs>(catalog.Error);
    }

    Result<IReadOnlyList<ChainTelemetrySessionCost>> spent = await _costs
        .CostsForAsync(SessionIdsOf(catalog.Value.Chains), ct).ConfigureAwait(false);
    if (!spent.IsSuccess)
    {
      return Result.Failure<ChainTelemetryInputs>(spent.Error);
    }

    Result<ChainTelemetryReviewFacts> verdicts = await _reviews.ListAsync(ct).ConfigureAwait(false);
    if (!verdicts.IsSuccess)
    {
      return Result.Failure<ChainTelemetryInputs>(verdicts.Error);
    }

    // The baseline is the ONE read whose absence is an ordinary answer rather than a failure:
    // an empty ledger is 'nothing to compare against', and the report says INCOMPARABLE rather
    // than asserting a ratio it could not compute.
    Result<ChainTelemetryBaseline?> era = await _baseline.ReadAsync(ct).ConfigureAwait(false);
    return era.IsSuccess
        ? Result.Success(new ChainTelemetryInputs(catalog.Value.Chains, spent.Value, verdicts.Value,
            era.Value, limits))
        : Result.Failure<ChainTelemetryInputs>(era.Error);
  }

  /// <summary>Every session any chain step recorded, deduplicated — the set the ledger is asked
  ///     to price. A step whose session was never assigned contributes nothing, and asking the
  ///     ledger about a null id would be asking about no session at all.</summary>
  private static IReadOnlyList<string> SessionIdsOf(IReadOnlyList<ChainTelemetryChain> chains) =>
  [
      .. chains.SelectMany(chain => chain.Steps)
          .Select(step => step.SessionId)
          .Where(id => !string.IsNullOrWhiteSpace(id))
          .Select(id => id!)
          .Distinct(StringComparer.Ordinal)
  ];
}
