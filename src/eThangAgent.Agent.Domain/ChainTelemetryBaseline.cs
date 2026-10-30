using System.Globalization;

namespace eThangAgent.AgentDomain;

/// <summary>The subagent era's own measured record (issue #309 D3): what the harness did BEFORE
///     chains, read from the rows those runs left behind.
///     <para>WHY THE BASELINE IS READ FROM ROWS RATHER THAN RE-MEASURED: #305 retired the
///     in-process subagent runtime and #316 retired the messaging surface, so there is no live
///     subagent path left to run. The database holds thousands of child agent rows with
///     timestamps and a usage ledger, which is a REAL baseline rather than a reconstruction —
///     and re-running anything to produce one would measure the new harness, not the old one.
///     </para>
///     <para>EVERY MEMBER IS NULLABLE AND NULL MEANS 'THE ROWS DO NOT SAY'. A baseline with no
///     duration rows cannot produce a wall-clock median, and a report that filled one in anyway
///     would compare the chain against a number nobody measured. <see cref="IsComparable"/> is
///     the one gate a ratio may pass through.</para></summary>
/// <param name="Runs">How many baseline runs the rows describe.</param>
/// <param name="CompletedRuns">How many of them completed.</param>
/// <param name="MedianDurationSeconds">The median run duration, or null when nothing timed one.
///     </param>
/// <param name="MedianCostPerCompletedRun">The median cost of a completed run, or null when no
///     completed run could be priced. A run that cannot be priced is EXCLUDED and counted, never
///     priced at zero.</param>
/// <param name="UnpricedRuns">How many runs contributed no cost figure.</param>
public sealed record ChainTelemetryBaseline(int Runs, int CompletedRuns,
    double? MedianDurationSeconds, decimal? MedianCostPerCompletedRun, int UnpricedRuns)
{
  /// <summary>True when a ratio against this baseline can be computed at all.
  ///     <para>BOTH SIDES MUST EXIST: a baseline with no completed run has no cost per completed
  ///     run, and a report that divided by it would be asserting a comparison the rows cannot
  ///     support. The threshold for that metric says INCOMPARABLE in exactly this case.</para>
  ///     </summary>
  public bool IsComparable => CompletedRuns > 0 && MedianCostPerCompletedRun is > 0m;

  /// <summary>True when the baseline can express a wall-clock comparison.</summary>
  public bool HasDuration => MedianDurationSeconds is > 0d;

  /// <summary>The baseline as report lines. Every absent figure says NOT MEASURED in words,
  ///     because a blank where a number belongs reads as a rendering bug (the
  ///     <c>ProviderPerformanceText</c> rule).</summary>
  public IReadOnlyList<string> Render() =>
  [
      string.Create(CultureInfo.InvariantCulture,
          $"runs: {Runs} ({CompletedRuns} completed, {UnpricedRuns} unpriced)"),
      "median duration: " + DurationText(),
      "median cost per completed run: " + CostText(),
  ];

  private string DurationText() => MedianDurationSeconds is { } seconds
      ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#}s")
      : "NOT MEASURED (no baseline run carries both timestamps)";

  private string CostText() => MedianCostPerCompletedRun is { } cost
      ? cost.ToString("0.######", CultureInfo.InvariantCulture)
      : "NOT MEASURED (no completed baseline run could be priced)";
}

/// <summary>Where a baseline's numbers came from, so the report can say what it compared
///     against instead of leaving a reader to guess (issue #309 D3).
///     <para>A SCOPE IS CARRIED RATHER THAN ASSUMED: the subagent rows predate workspace
///     stamping (measured 2026-10-30: all 2,156 child rows carry a null workspace id, because
///     #305's stamping fix landed long after those runs), so a workspace-scoped baseline would
///     be empty in every workspace and the report would claim there was nothing to compare
///     against. The scope says which rows answered, and the report renders it.</para></summary>
public enum ChainTelemetryBaselineScope
{
  /// <summary>The rows are this workspace's own.</summary>
  Workspace,

  /// <summary>The rows span every workspace, because the per-workspace rows were empty or
  ///     unstamped. The report says so, since a wider baseline is a weaker comparison.</summary>
  Global,
}
