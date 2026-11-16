namespace eThangAgent.ModelDomain.Tests;

// The cost model behind the Cheapest routing variant: what one endpoint costs per input token
// at a STATED usage shape, the hit rate at which two endpoints cost the same, and the
// shape-free dominance claim. Every figure here is the measured table from the plan, priced at
// an output/input ratio of 0.007284 and shown in USD per million input tokens.
public class CheapestCostTests
{
  private const decimal Ratio = 0.007284m;

  // deepseek/deepseek-v4.1-flash's two endpoints, at the magnitudes the catalog serves.
  private static readonly ModelProviderEntry Wafer = Endpoint(0.000000051m, 0.000000018m, 0.000000567m);
  private static readonly ModelProviderEntry River = Endpoint(0.000000096m, 0.00000000192m, 0.000000384m);

  private static ModelProviderEntry Endpoint(decimal? input, decimal? cacheRead, decimal? output)
      => new("test/model", "Provider", input, cacheRead, output, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null);

  private static decimal PerMillion(ModelProviderEntry entry, decimal hitRate)
      => Math.Round(CheapestCost.Unit(entry, new CostShape(hitRate, Ratio))!.Value * 1_000_000m, 6);

  [Fact]
  public void Unit_ReproducesTheMeasuredWaferAndRiverTable()
  {
    // E1: the two endpoints cross at 71.49%, so Wafer leads below it and River above it.
    (decimal HitRate, decimal Wafer, decimal River)[] rows =
    [
        (0m, 0.055130m, 0.098797m),
        (0.5m, 0.038630m, 0.051757m),
        (0.714m, 0.031568m, 0.031624m),
        (0.9013m, 0.025387m, 0.014003m),
        (1m, 0.022130m, 0.004717m),
    ];

    foreach ((decimal hitRate, decimal wafer, decimal river) in rows)
    {
      Assert.Equal(wafer, PerMillion(Wafer, hitRate));
      Assert.Equal(river, PerMillion(River, hitRate));
    }
  }

  [Fact]
  public void BreakEvenHitRate_SolvesTheCrossingOfTheMeasuredPair()
  {
    // E1: 0.7149, so the surface can say the choice holds only above a 71.5% hit rate.
    decimal? crossing = CheapestCost.BreakEvenHitRate(Wafer, River, Ratio);

    Assert.True(crossing.HasValue);
    Assert.Equal(0.7149m, Math.Round(crossing.Value, 4));
  }

  [Fact]
  public void BreakEvenHitRate_IsSymmetric()
  {
    Assert.Equal(CheapestCost.BreakEvenHitRate(Wafer, River, Ratio),
        CheapestCost.BreakEvenHitRate(River, Wafer, Ratio));
  }

  [Fact]
  public void BreakEvenHitRate_IsNullWhenTheCrossingIsOutOfRange()
  {
    // E5: this pair crosses at h* = 1.4, which no real hit rate reaches.
    ModelProviderEntry cheap = Endpoint(0.0000001m, 0.0000001m, 0.0000001m);
    ModelProviderEntry dearer = Endpoint(0.00000024m, 0.00000014m, 0.0000001m);

    Assert.Null(CheapestCost.BreakEvenHitRate(cheap, dearer, Ratio));
  }

  [Fact]
  public void BreakEvenHitRate_IsNullWhenTheEndpointsNeverCross()
  {
    // A zero denominator: the two costs move together in the hit rate and are parallel.
    ModelProviderEntry a = Endpoint(0.0000001m, 0.00000002m, 0.0000002m);
    ModelProviderEntry b = Endpoint(0.0000002m, 0.00000012m, 0.0000004m);

    Assert.Null(CheapestCost.BreakEvenHitRate(a, b, Ratio));
  }

  [Fact]
  public void Unit_ReproducesTheDominatedPairTable()
  {
    // E2: A is cheaper on all three rates, so it leads at every hit rate.
    ModelProviderEntry a = Endpoint(0.0000001m, 0.00000001m, 0.0000002m);
    ModelProviderEntry b = Endpoint(0.0000002m, 0.00000003m, 0.0000004m);

    Assert.Equal(0.101457m, PerMillion(a, 0m));
    Assert.Equal(0.202914m, PerMillion(b, 0m));
    Assert.Equal(0.056457m, PerMillion(a, 0.5m));
    Assert.Equal(0.117914m, PerMillion(b, 0.5m));
    Assert.Equal(0.011457m, PerMillion(a, 1m));
    Assert.Equal(0.032914m, PerMillion(b, 1m));
  }

  [Fact]
  public void Dominates_IsTrueForTheDominatedPairAndFalseReversed()
  {
    ModelProviderEntry cheap = Endpoint(0.0000001m, 0.00000001m, 0.0000002m);
    ModelProviderEntry dearer = Endpoint(0.0000002m, 0.00000003m, 0.0000004m);

    Assert.True(CheapestCost.Dominates(cheap, dearer));
    Assert.False(CheapestCost.Dominates(dearer, cheap));
    Assert.Null(CheapestCost.BreakEvenHitRate(cheap, dearer, Ratio));
  }

  [Fact]
  public void Dominates_IsFalseWhenOneRateIsWorse()
  {
    // Neither endpoint dominates the measured pair: Wafer is cheaper on input and output,
    // River on cache reads.
    Assert.False(CheapestCost.Dominates(Wafer, River));
    Assert.False(CheapestCost.Dominates(River, Wafer));
  }

  [Fact]
  public void Dominates_IsIrreflexive()
  {
    Assert.False(CheapestCost.Dominates(Wafer, Wafer));
    Assert.False(CheapestCost.Dominates(Wafer, Endpoint(0.000000051m, 0.000000018m, 0.000000567m)));
  }

  [Fact]
  public void Unit_IsConstantAcrossTheHitRateWithoutACacheReadPrice()
  {
    // E3: no advertised cache-read price means no discount, so a high hit rate gains nothing.
    ModelProviderEntry noCache = Endpoint(0.0000003m, null, 0.0000012m);

    Assert.Equal(0.308741m, PerMillion(noCache, 0m));
    Assert.Equal(0.308741m, PerMillion(noCache, 0.5m));
    Assert.Equal(0.308741m, PerMillion(noCache, 1m));
  }

  [Fact]
  public void Unit_FallsAsTheHitRateRisesWhenTheCacheReadRateIsLower()
  {
    Assert.True(PerMillion(Wafer, 1m) < PerMillion(Wafer, 0m));
    Assert.True(PerMillion(River, 1m) < PerMillion(River, 0m));
  }

  [Fact]
  public void Unit_TreatsANegativeCacheReadPriceAsNoDiscount()
  {
    // The routing pseudo-models advertise -1, "set at routing time" - not a price.
    ModelProviderEntry routing = Endpoint(0.0000003m, -1m, 0.0000012m);

    Assert.Equal(PerMillion(Endpoint(0.0000003m, null, 0.0000012m), 0.9m), PerMillion(routing, 0.9m));
  }

  [Fact]
  public void Unit_IsNullWhenTheInputPriceIsUnknown()
  {
    // E4: unknown never ranks as cheapest.
    Assert.Null(CheapestCost.Unit(Endpoint(null, 0.00000001m, 0.0000012m), new CostShape(0.5m, Ratio)));
    Assert.Null(CheapestCost.Unit(Endpoint(-1m, 0.00000001m, 0.0000012m), new CostShape(0.5m, Ratio)));
  }

  [Fact]
  public void Unit_IsNullWhenTheOutputPriceIsUnknown()
      => Assert.Null(CheapestCost.Unit(Endpoint(0.0000003m, 0.00000001m, null), new CostShape(0.5m, Ratio)));

  [Fact]
  public void Unit_PricesAServedZeroInputRateAsZero()
  {
    // E4: a free model is a real price of zero, not an unknown one.
    ModelProviderEntry free = Endpoint(0m, 0m, 0.0000012m);

    decimal? unit = CheapestCost.Unit(free, new CostShape(0.5m, Ratio));

    Assert.True(unit.HasValue);
    Assert.Equal(0.0000000087408m, Math.Round(unit.Value, 13));
  }

  [Fact]
  public void EveryFunction_IsTotalOverNullInputs()
  {
    CostShape shape = new(0.5m, Ratio);

    Assert.Null(CheapestCost.Unit(null, shape));
    Assert.Null(CheapestCost.BreakEvenHitRate(null, Wafer, Ratio));
    Assert.Null(CheapestCost.BreakEvenHitRate(Wafer, null, Ratio));
    Assert.Null(CheapestCost.BreakEvenHitRate(null, null, Ratio));
    Assert.False(CheapestCost.Dominates(null, Wafer));
    Assert.False(CheapestCost.Dominates(Wafer, null));
    Assert.False(CheapestCost.Dominates(null, null));
  }

  [Fact]
  public void CostShape_CarriesTheAssumptionItWasBuiltWith()
  {
    CostShape shape = new(0.9013m, Ratio);

    Assert.Equal(0.9013m, shape.HitRate);
    Assert.Equal(Ratio, shape.OutputPerInputRatio);
  }
}
