using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Every chain in a workspace, with its steps, for the telemetry report (issue #309).
///     <para>WHY A PORT OF ITS OWN RATHER THAN <see cref="IChainStore"/>: the report needs EVERY
///     chain regardless of status (the store's listing is filtered and returns a catalog of
///     materialized AGGREGATES), and it needs them without the CAS machinery a write port
///     carries. A report that could write would be a report that could change the record it
///     measures.</para>
///     <para>IT IS READ-ONLY BY CONSTRUCTION: there is no write member, so no caller can mutate
///     a chain through it even by accident — the <c>IExperimentLedgerAccess</c> precedent.
///     </para>
///     <para>A row that cannot be materialized is SKIPPED and REPORTED rather than failing the
///     read (the #279 containment): one corrupt chain must not blank a workspace's telemetry.
///     </para></summary>
public interface IChainTelemetrySource
{
  /// <summary>Every chain in the bound workspace, oldest id first, with its steps.
  ///     <para>An empty workspace is an EMPTY LIST, not a failure: 'nothing has run yet' is a
  ///     real answer, and a failure would make a caller distinguish it from a broken read for no
  ///     reason.</para></summary>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainTelemetryCatalog>> ListAsync(CancellationToken ct = default);
}

/// <summary>The chains a telemetry read could materialize, plus the rows it could not
///     (issue #309).
///     <para>THE CORRUPT LIST IS NOT DECORATION: a report computed over a silently partial set
///     of chains would present itself as complete, and the one number a reader most needs is
///     'how much of this did you actually see'.</para></summary>
/// <param name="Chains">The readable chains, with their steps.</param>
/// <param name="Corrupt">The chain ids that could not be read, with the reason.</param>
public sealed record ChainTelemetryCatalog(IReadOnlyList<ChainTelemetryChain> Chains,
    IReadOnlyList<CorruptChainRow> Corrupt)
{
  /// <summary>An empty catalog: nothing run, nothing corrupt.</summary>
  public static ChainTelemetryCatalog Empty { get; } = new([], []);
}

/// <summary>The usage ledger's totals for the sessions a chain started (issue #309 D1).
///     <para>WHY ITS OWN PORT: the Model Domain's <c>IUsageQueries</c>
///     answers 'what did ONE session and its descendants spend', and the Agent Domain
///     references it — but the telemetry question is 'what did THESE sessions spend', and asking
///     it once per session would re-walk a recursive CTE per step. This is the batch shape.
///     </para>
///     <para>THE COST IS THE LEDGER'S OWN ANSWER, never a recomputation: a second cost formula
///     would eventually disagree with the ledger, and the ledger is the one that is reconciled
///     (D1).</para></summary>
public interface IChainSessionCostSource
{
  /// <summary>The ledger's total for each of <paramref name="sessionIds"/>, keyed by session.
  ///     <para>A session with no rows is ABSENT from the result rather than present with a zero
  ///     cost: 'never ran a billable call' and 'ran free' are different facts, and a zero would
  ///     silently assert the second.</para></summary>
  /// <param name="sessionIds">The sessions to price.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<IReadOnlyList<ChainTelemetrySessionCost>>> CostsForAsync(
      IReadOnlyList<string> sessionIds, CancellationToken ct = default);
}

/// <summary>The plan review record plus each reviewed plan's current version (issue #309).
///     <para>WHY A PORT AND NOT THE REVIEW STORE DIRECTLY: the review record lives in the Plan
///     Domain and the Agent Domain does not reference it — the boundary <see cref="IChainReviewSource"/>
///     and <see cref="IChainPlanSource"/> already hold. The composition root adapts the real
///     store, so the report reads THE SAME ROWS THE CURSOR DOES (the #216 D7 rule) without the
///     Agent Domain learning what a review row is.</para>
///     <para>READ-ONLY, like every port on this surface.</para></summary>
public interface IChainReviewTelemetrySource
{
  /// <summary>Every recorded review in the bound workspace, plus each reviewed plan's current
  ///     version, so 'did the deny change anything' is answerable.</summary>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainTelemetryReviewFacts>> ListAsync(CancellationToken ct = default);
}

/// <summary>The subagent era's own rows, as the comparison baseline (issue #309 D3).
///     <para>WHY THIS READS THE DATABASE DIRECTLY: #305 retired the in-process subagent runtime
///     and #316 retired the messaging surface, so there is no live subagent path left to
///     measure. What survives is the RECORD — thousands of child agent rows with timestamps and
///     a usage ledger — and that is a real baseline rather than a reconstruction.</para>
///     <para>NULL MEANS THE ROWS DO NOT SAY, never zero: a baseline with no timed run has no
///     median duration, and a report that filled one in would compare chains against a number
///     nobody measured.</para>
///     <para>READ-ONLY, and deliberately CROSS-WORKSPACE (see
///     <see cref="ChainTelemetryBaselineScope"/>): the subagent rows predate workspace stamping,
///     so a workspace-scoped read would be empty everywhere and the report would claim there was
///     nothing to compare against.</para></summary>
public interface IChainBaselineSource
{
  /// <summary>The baseline measured from the persisted subagent-era rows, or null when there are
  ///     none to read.</summary>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainTelemetryBaseline?>> ReadAsync(CancellationToken ct = default);
}

/// <summary>Where the fixed thresholds live (issue #309 D5).
///     <para>A PORT RATHER THAN A DIRECT STATE READ: the State Domain is a separate bounded
///     context, and the Agent Domain reaching into its service would be the boundary violation
///     <c>IChainBriefSource</c> exists to avoid — the brief already reads the workspace's gate
///     commands through a port for exactly this reason.</para>
///     <para>READ-ONLY: nothing on this surface can write a threshold, because a report that
///     could set its own bounds would be the unfalsifiable claim the issue opens with.</para>
///     </summary>
public interface IChainThresholdSource
{
  /// <summary>The parsed thresholds at <paramref name="key"/>, or a named refusal when the key
  ///     is absent, empty, or does not parse.</summary>
  /// <param name="key">The state key the artifact lives at.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainTelemetryThresholds>> ReadAsync(string key, CancellationToken ct = default);
}
