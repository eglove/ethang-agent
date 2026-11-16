namespace eThangAgent.ModelDomain.Tests;

// The evidence chain behind the Cheapest routing variant: which rung of our own usage ledger
// answers for a model that is being priced, and what it says. Every figure here is the
// measured value from the plan, and the Describe strings are the surface's verbatim text.
public class CheapestShapeSourceTests
{
  private const string Model = "deepseek/deepseek-v4.1-flash-1017";

  private const string Endpoint = "baidu/fp8";

  // E1's measured row: 9,054,053,580 input tokens, 8,082,000,000 of them cache hits.
  private const long MeasuredInput = 9_054_053_580L;

  private const long MeasuredCached = 8_082_000_000L;

  private static readonly CostShape WorkspaceShape = new(0.9013m, 0.007284m);

  private static ProviderPerformance Row(string? modelId, string? provider, long inputTokens,
      long cachedTokens, long outputTokens = 0, string? servedEndpoint = null)
      => new(modelId, provider, null, null, null, null, 0, 0, 0, 0, 1, inputTokens, cachedTokens,
          servedEndpoint)
      {
        OutputTokens = outputTokens,
      };

  private static ProviderPerformanceReport Report(params ProviderPerformance[] rows)
      => new(UsageScope.Global, null, rows.Sum(row => row.InputTokens),
          rows.Sum(row => row.CachedInputTokens), rows.Length, 0, rows);

  [Fact]
  public void Resolve_AnswersTheEndpointRungWhenTheEndpointClearedTheFloor()
  {
    // E1: the report has been through BindEndpoints, so this row names its endpoint and the
    // endpoint rung answers with its own measured rate.
    ProviderPerformanceReport report = Report(
        Row(Model, "Baidu", MeasuredInput, MeasuredCached, servedEndpoint: Endpoint));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, WorkspaceShape, Model, Endpoint);

    Assert.Equal(ShapeSource.Endpoint, resolved.Source);
    // 8,082,000,000 / 9,054,053,580 = 0.89263885..., so four places is 0.8926.
    Assert.Equal(0.8926m, Math.Round(resolved.Shape.HitRate, 4));
  }

  [Fact]
  public void Resolve_FallsThroughToTheModelRungWhenTheReportWasNeverBound()
  {
    // E2: the storage ACL leaves ServedEndpoint null, so an unbound report answers no
    // endpoint rung. That is the ordinary path, not an error.
    ProviderPerformanceReport report = Report(Row(Model, "Baidu", MeasuredInput, MeasuredCached));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, WorkspaceShape, Model, Endpoint);

    Assert.Equal(ShapeSource.Model, resolved.Source);
    Assert.Equal(0.8926m, Math.Round(resolved.Shape.HitRate, 4));
  }

  [Fact]
  public void Resolve_FallsThroughToTheWorkspaceRungBelowTheFloor()
  {
    // E3: 704,752 input tokens is real evidence, but not enough of it to outrank the
    // workspace median.
    ProviderPerformanceReport report = Report(Row("openai/gpt-6-luna", "OpenAI", 704_752L, 466_009L));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, WorkspaceShape,
        "openai/gpt-6-luna", null);

    Assert.Equal(ShapeSource.Workspace, resolved.Source);
    Assert.Equal(0.9013m, resolved.Shape.HitRate);
    Assert.Equal(0.007284m, resolved.Shape.OutputPerInputRatio);
  }

  [Fact]
  public void Resolve_FallsThroughToTheDocumentedDefaultsWhenNothingMeasured()
  {
    // E4: nothing measured this model and the ledger has no qualifying model either.
    CostShapeAssumption resolved = CheapestShapeSource.Resolve(null, null, Model, Endpoint);

    Assert.Equal(ShapeSource.Default, resolved.Source);
    Assert.Equal(0.5m, resolved.Shape.HitRate);
    Assert.Equal(0.007284m, resolved.Shape.OutputPerInputRatio);
    Assert.Equal(CostShapeAssumption.Fallback, resolved);
    Assert.False(resolved.IsEvidence);
  }

  [Fact]
  public void Resolve_ReportsAGenuineZeroHitRateAsEvidence()
  {
    // E5: this model was billed 954,994,119 input tokens and scored no cache hits at all.
    // A real zero is a measurement, and reporting the default instead would hide it.
    ProviderPerformanceReport report = Report(
        Row("stealth/crouton-alpha", "Stealth", 954_994_119L, 0L));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, WorkspaceShape,
        "stealth/crouton-alpha", null);

    Assert.Equal(ShapeSource.Model, resolved.Source);
    Assert.Equal(0m, resolved.Shape.HitRate);
    Assert.True(resolved.IsEvidence);
  }

  [Fact]
  public void Resolve_AppliesTheFloorToTheModelAggregateNotToEachRow()
  {
    // E7: neither provider cleared the floor alone; together they hold 1,100,000 tokens.
    ProviderPerformanceReport report = Report(
        Row(Model, "Wafer", 600_000L, 540_000L),
        Row(Model, "River", 500_000L, 450_000L));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, WorkspaceShape, Model, null);

    Assert.Equal(ShapeSource.Model, resolved.Source);
    Assert.Equal(990_000m / 1_100_000m, resolved.Shape.HitRate);
  }

  [Fact]
  public void Resolve_AnswersAtExactlyTheFloorAndNotOneTokenBelowIt()
  {
    ProviderPerformanceReport atFloor = Report(Row(Model, "Wafer", 1_000_000L, 900_000L));
    ProviderPerformanceReport belowFloor = Report(Row(Model, "Wafer", 999_999L, 900_000L));

    Assert.Equal(ShapeSource.Model,
        CheapestShapeSource.Resolve(atFloor, null, Model, null).Source);
    Assert.Equal(ShapeSource.Default,
        CheapestShapeSource.Resolve(belowFloor, null, Model, null).Source);
    Assert.Equal(1_000_000L, CheapestShapeSource.EvidenceFloorTokens);
  }

  [Fact]
  public void Resolve_NeverLendsAnEndpointASiblingsFigure()
  {
    // The sibling cleared the floor; the endpoint asked about did not. The endpoint rung must
    // stay silent and the model rung answers with the combined figure - never the sibling's.
    ProviderPerformanceReport report = Report(
        Row(Model, "Wafer", 9_000_000L, 8_100_000L, servedEndpoint: "wafer/fp8"),
        Row(Model, "River", 500_000L, 0L, servedEndpoint: "river/fp8"));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, null, Model, "river/fp8");

    Assert.Equal(ShapeSource.Model, resolved.Source);
    Assert.Equal(8_100_000m / 9_500_000m, resolved.Shape.HitRate);
  }

  [Fact]
  public void Resolve_DerivesTheRatioFromTheRungsOwnOutputTokens()
  {
    ProviderPerformanceReport report = Report(
        Row(Model, "Wafer", 2_000_000L, 1_800_000L, outputTokens: 20_000L),
        Row(Model, "River", 1_000_000L, 900_000L, outputTokens: 10_000L));

    CostShapeAssumption resolved = CheapestShapeSource.Resolve(report, null, Model, null);

    Assert.Equal(0.01m, resolved.Shape.OutputPerInputRatio);
  }

  [Fact]
  public void Resolve_IsTotalOverEveryCombinationOfNullAndEmptyInputs()
  {
    ProviderPerformanceReport empty = Report();
    ProviderPerformanceReport belowFloor = Report(Row(Model, "Wafer", 1L, 0L));

    Assert.Equal(ShapeSource.Default,
        CheapestShapeSource.Resolve(empty, null, Model, null).Source);
    Assert.Equal(ShapeSource.Default,
        CheapestShapeSource.Resolve(belowFloor, null, Model, Endpoint).Source);
    Assert.Equal(ShapeSource.Default,
        CheapestShapeSource.Resolve(null, null, string.Empty, string.Empty).Source);
    Assert.Equal(ShapeSource.Workspace,
        CheapestShapeSource.Resolve(null, WorkspaceShape, Model, null).Source);
  }

  [Fact]
  public void Describe_StatesEachRungAndItsFigures()
  {
    // B7: the four sentences, byte for byte. The rung is always named, because a projection
    // built on an unstated assumption is the failure this feature exists to remove.
    CostShape measured = new(0.9013m, 0.007463m);

    Assert.Equal("this endpoint measured 90.1% cache hits, 0.007463 output per input token",
        new CostShapeAssumption(measured, ShapeSource.Endpoint).Describe());
    Assert.Equal("this model measured 90.1% cache hits, 0.007463 output per input token",
        new CostShapeAssumption(measured, ShapeSource.Model).Describe());
    Assert.Equal("workspace median 90.1% cache hits, 0.007284 output per input token",
        new CostShapeAssumption(new CostShape(0.9013m, 0.007284m), ShapeSource.Workspace).Describe());
    Assert.Equal("default 50% cache hits, 0.007284 output per input token (nothing measured yet)",
        CostShapeAssumption.Fallback.Describe());
  }

  [Fact]
  public void Describe_RendersPercentagesWithOneDecimalInInvariantCulture()
  {
    CostShapeAssumption resolved = CheapestShapeSource.Resolve(
        Report(Row(Model, "Wafer", 1_000_000L, 1_000_000L, outputTokens: 7_463L)), null, Model, null);

    Assert.Equal(ShapeSource.Model, resolved.Source);
    Assert.Equal("this model measured 100% cache hits, 0.007463 output per input token",
        resolved.Describe());
  }

  [Fact]
  public void Defaults_AreTheDocumentedConstants()
  {
    Assert.Equal(0.5m, CheapestShapeSource.DefaultHitRate);
    Assert.Equal(0.007284m, CheapestShapeSource.DefaultOutputPerInputRatio);
    Assert.Equal(new CostShape(CheapestShapeSource.DefaultHitRate,
        CheapestShapeSource.DefaultOutputPerInputRatio), CostShapeAssumption.Fallback.Shape);
    Assert.Equal(ShapeSource.Default, CostShapeAssumption.Fallback.Source);
  }
}
