using System.Globalization;

namespace eThangAgent.ModelDomain;

/// <summary>Which rung of the usage-evidence chain answered a <see cref="CostShape"/>
///     (see <see cref="CheapestShapeSource.Resolve"/>).
///     <para>THE ORDER OF THE MEMBERS IS THE CHAIN, most specific first: this exact endpoint,
///     then this model across every provider, then the workspace's median model, then the
///     documented default. A caller that shows a projected cost shows this beside it, because
///     the same provider is the cheap choice at a 90% cache-hit rate and the expensive one at
///     0%.</para></summary>
public enum ShapeSource
{
  /// <summary>This exact endpoint's own measured shape.</summary>
  Endpoint,

  /// <summary>This model's measured shape, aggregated across every provider that served it.</summary>
  Model,

  /// <summary>The median shape across this ledger's qualifying models.</summary>
  Workspace,

  /// <summary>Nothing measured anything usable, so the documented defaults apply.</summary>
  Default,
}

/// <summary>The usage shape a cost is projected at, together with the rung that supplied it.
///     <para>THE SHAPE IS ALWAYS CARRIED WITH ITS SOURCE: a projection built on an unstated
///     assumption is a guess wearing a decimal point, so <see cref="Describe"/> states both
///     the figures and where they came from.</para></summary>
/// <param name="Shape">The hit rate and output/input ratio to price at.</param>
/// <param name="Source">The rung that answered.</param>
public sealed record CostShapeAssumption(CostShape Shape, ShapeSource Source)
{
  /// <summary>The documented defaults, used only when no rung of the chain answered.
  ///     Mid-range on the hit rate rather than zero, so a projection does not hide the
  ///     difference caching makes.</summary>
  public static CostShapeAssumption Fallback { get; } = new(
      new CostShape(CheapestShapeSource.DefaultHitRate, CheapestShapeSource.DefaultOutputPerInputRatio),
      ShapeSource.Default);

  /// <summary>True when the shape rests on something this harness measured rather than on the
  ///     documented default.</summary>
  public bool IsEvidence => Source != ShapeSource.Default;

  /// <summary>The one-line statement of the shape and its provenance, e.g.
  ///     <c>this model measured 90.1% cache hits, 0.007463 output per input token</c>.
  ///     <para>EVERY SOURCE GETS ITS OWN SENTENCE, so a measured shape, a workspace median,
  ///     and an invented default cannot be rendered identically.</para></summary>
  public string Describe() => Source switch
  {
    ShapeSource.Endpoint => $"this endpoint measured {PercentText} cache hits, {RatioText} output per input token",
    ShapeSource.Model => $"this model measured {PercentText} cache hits, {RatioText} output per input token",
    ShapeSource.Workspace => $"workspace median {PercentText} cache hits, {RatioText} output per input token",
    ShapeSource.Default => $"default {PercentText} cache hits, {RatioText} output per input token (nothing measured yet)",
    _ => $"{PercentText} cache hits, {RatioText} output per input token",
  };

  /// <summary>The hit rate as a percentage with at most one decimal place, invariant culture.
  ///     </summary>
  private string PercentText => (Shape.HitRate * 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%";

  /// <summary>The output/input ratio at up to six decimal places, invariant culture.</summary>
  private string RatioText => Shape.OutputPerInputRatio.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>Resolves the usage shape a provider comparison prices at, by walking the
///     evidence chain and stopping at the first rung that answers.
///     <para>THE CHAIN IS FIXED AND WALKED IN THIS ORDER: this endpoint, then this model
///     across every provider, then the workspace median, then the documented default. A rung
///     answers only when the input tokens behind it reach <see cref="EvidenceFloorTokens"/>,
///     so a figure resting on a handful of calls cannot outrank a wider measurement.</para>
///     <para>THE FLOOR APPLIES TO THE RUNG'S AGGREGATE, NEVER TO EACH ROW: a model whose two
///     providers were billed 600,000 and 500,000 input tokens has 1,100,000 tokens of
///     evidence between them and answers on the model rung.</para>
///     <para>THE REPORT MUST ALREADY HAVE BEEN THROUGH
///     <see cref="ProviderPerformanceReport.BindEndpoints"/>: the storage ACL records the
///     provider stamp the rows carry and leaves <see cref="ProviderPerformance.ServedEndpoint"/>
///     null, while binding a stamp to an endpoint needs the catalog. An unbound report
///     therefore answers no endpoint rung and falls through to the model rung - which is the
///     documented behaviour of <see cref="ProviderPerformanceReport.LookupByEndpoint"/>, not a
///     failure.</para>
///     <para>NOTHING HERE THROWS: a null report, an empty report, a report whose rows all fall
///     below the floor, and a null workspace shape each fall through to the next rung, and the
///     returned <see cref="ShapeSource"/> names the rung that answered.</para></summary>
public static class CheapestShapeSource
{
  /// <summary>The input tokens a rung needs before it answers, and the only threshold in this
  ///     file.</summary>
  public const long EvidenceFloorTokens = 1_000_000L;

  /// <summary>The documented hit rate used when no rung answers.</summary>
  public const decimal DefaultHitRate = 0.5m;

  /// <summary>The documented output/input ratio used when no rung answers.</summary>
  public const decimal DefaultOutputPerInputRatio = 0.007284m;

  /// <summary>The shape to price <paramref name="modelId"/> at, from the most specific
  ///     evidence available.
  ///     <para>The workspace rung carries no token count of its own, so its floor is applied
  ///     where the median is computed: a caller supplies a shape that already rests on
  ///     qualifying models only.</para></summary>
  /// <param name="report">The usage report, already through
  ///     <see cref="ProviderPerformanceReport.BindEndpoints"/>, or null when no report could be
  ///     read.</param>
  /// <param name="workspaceShape">The ledger's median shape, or null when it has none.</param>
  /// <param name="modelId">The model being priced.</param>
  /// <param name="endpointSlug">The endpoint being priced, or null when the caller prices no
  ///     particular endpoint.</param>
  public static CostShapeAssumption Resolve(ProviderPerformanceReport? report,
      CostShape? workspaceShape, string modelId, string? endpointSlug)
    => EndpointRung(report, modelId, endpointSlug)
        ?? ModelRung(report, modelId)
        ?? WorkspaceRung(workspaceShape)
        ?? CostShapeAssumption.Fallback;

  /// <summary>This endpoint's own measurement, or null when it never ran or ran below the
  ///     floor.</summary>
  private static CostShapeAssumption? EndpointRung(ProviderPerformanceReport? report,
      string modelId, string? endpointSlug)
    => Measured(report?.LookupByEndpoint(modelId, endpointSlug), ShapeSource.Endpoint);

  /// <summary>Every row for this model, whatever provider served it, summed into one shape - or
  ///     null when the model's combined input tokens fall below the floor.</summary>
  private static CostShapeAssumption? ModelRung(ProviderPerformanceReport? report, string modelId)
  {
    if (report is null || string.IsNullOrEmpty(modelId))
    {
      return null;
    }

    long inputTokens = 0;
    long cachedTokens = 0;
    long outputTokens = 0;
    foreach (ProviderPerformance row in report.Providers.Where(row =>
        string.Equals(row.ModelId, modelId, StringComparison.Ordinal)))
    {
      inputTokens += row.InputTokens;
      cachedTokens += row.CachedInputTokens;
      outputTokens += row.OutputTokens;
    }

    return inputTokens >= EvidenceFloorTokens
        ? new CostShapeAssumption(
            new CostShape(UsageStatistics.HitRate(cachedTokens, inputTokens) ?? 0m,
                Ratio(outputTokens, inputTokens)),
            ShapeSource.Model)
        : null;
  }

  /// <summary>The workspace median the caller supplied, or null when it supplied none.</summary>
  private static CostShapeAssumption? WorkspaceRung(CostShape? workspaceShape)
    => workspaceShape is { } shape ? new CostShapeAssumption(shape, ShapeSource.Workspace) : null;

  /// <summary>One measured row as an assumption, or null when the row is absent or rests on
  ///     fewer input tokens than the floor.</summary>
  private static CostShapeAssumption? Measured(ProviderPerformance? row, ShapeSource source)
    => row is { } measured
        && measured.InputTokens >= EvidenceFloorTokens
        && measured.HitRate is { } hitRate
            ? new CostShapeAssumption(
                new CostShape(hitRate, Ratio(measured.OutputTokens, measured.InputTokens)), source)
            : null;

  /// <summary>Output tokens per input token, or zero when no input token was billed - the
  ///     floor keeps that case out of the chain, so it is a total function rather than a
  ///     reachable answer.</summary>
  private static decimal Ratio(long outputTokens, long inputTokens)
    => inputTokens <= 0 ? 0m : (decimal)outputTokens / inputTokens;
}
