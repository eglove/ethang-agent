using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

// B3 (issue #293): a call is billed from the price-ladder rung its PROMPT SIZE falls in.
// The live shape is a prompt-size tier, not a clock - measured 2026-10-27, every override
// entry carries min_prompt_tokens and none carries a time bound - so the rule is pure over
// the prompt size and knows nothing about when the call happened.
public class PriceTierTests
{
  private static PriceOverride Tier(int minTokens, decimal prompt, decimal cacheRead, decimal completion)
      => new(minTokens, prompt, cacheRead, null, completion);

  private static ModelProviderEntry Entry(string? provider = "p", IReadOnlyList<PriceOverride>? tiers = null)
      => new("m", provider, 0.000003m, 0.0000003m, 0.000015m, 128_000, 4096, true, false, true,
          null, null, null, null, null, "", PriceOverrides: tiers);

  [Fact]
  public void Applicable_PromptBelowEveryRung_ReturnsNull_SoBasePricesApply()
  {
    // A 1,000-token call on a 200,000-token ladder is priced from the base rung. Returning
    // the ladder's first entry here would overcharge every short call.
    IReadOnlyList<PriceOverride> tiers = [Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m)];

    Assert.Null(PriceTiers.Applicable(tiers, 199_999));
  }

  [Fact]
  public void Applicable_PromptExactlyAtTheRung_AppliesIt()
  {
    // The boundary is inclusive: 'at or above' is what the field name says.
    IReadOnlyList<PriceOverride> tiers = [Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m)];

    Assert.Equal(200_000, PriceTiers.Applicable(tiers, 200_000)!.MinPromptTokens);
  }

  [Fact]
  public void Applicable_SeveralRungs_ReturnsTheHighestApplicable_NotTheFirst()
  {
    // THE ORDERING RULE: the API serves the ladder ascending today, but a consumer that
    // trusted array order would mis-price the day a provider emitted it differently.
    IReadOnlyList<PriceOverride> tiers =
    [
        Tier(500_000, 0.000009m, 0.0000009m, 0.00003m),
        Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m),
        Tier(100_000, 0.000004m, 0.0000004m, 0.00002m),
    ];

    Assert.Equal(500_000, PriceTiers.Applicable(tiers, 600_000)!.MinPromptTokens);
    Assert.Equal(200_000, PriceTiers.Applicable(tiers, 250_000)!.MinPromptTokens);
    Assert.Equal(100_000, PriceTiers.Applicable(tiers, 120_000)!.MinPromptTokens);
  }

  [Fact]
  public void Applicable_NoLadder_ReturnsNull()
  {
    Assert.Null(PriceTiers.Applicable(null, 1_000_000));
    Assert.Null(PriceTiers.Applicable([], 1_000_000));
  }

  [Fact]
  public void PricesFor_ShortCall_StampsTheBasePrices()
  {
    ModelProviderEntry entry = Entry(tiers: [Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m)]);

    UsagePrices prices = entry.PricesFor(1_000);

    Assert.Equal(0.000003m, prices.PromptPricePerToken);
    Assert.Equal(0.0000003m, prices.PromptCacheReadPricePerToken);
    Assert.Equal(0.000015m, prices.CompletionPricePerToken);
  }

  [Fact]
  public void PricesFor_LongCall_StampsTheUpperRung()
  {
    // THE DEFECT THIS CLOSES: a 300,000-token call on Claude Sonnet 4.5 is billed at $6/M
    // input while the base price is $3/M, so every long-context call was stamped at HALF
    // what it cost - in the ledger, whose whole purpose is to state what a call cost.
    ModelProviderEntry entry = Entry(tiers: [Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m)]);

    UsagePrices prices = entry.PricesFor(300_000);

    Assert.Equal(0.000006m, prices.PromptPricePerToken);
    Assert.Equal(0.0000006m, prices.PromptCacheReadPricePerToken);
    Assert.Equal(0.0000225m, prices.CompletionPricePerToken);
  }

  [Fact]
  public void PricesFor_RungOmittingAPrice_LeavesItNull_NeverInheritsTheBase()
  {
    // A rung states DIFFERENT numbers; a price it omits is unknown, not 'same as base'.
    ModelProviderEntry entry = Entry(tiers: [new PriceOverride(200_000, 0.000006m, null, null, null)]);

    UsagePrices prices = entry.PricesFor(300_000);

    Assert.Equal(0.000006m, prices.PromptPricePerToken);
    Assert.Null(prices.PromptCacheReadPricePerToken);
    Assert.Null(prices.CompletionPricePerToken);
  }

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public int Calls { get; private set; }

    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(entries));
    }
  }

  [Fact]
  public async Task PricesForAsync_TwoCallsOfDifferentSizes_EachGetItsOwnTier()
  {
    // WHAT IS MEMOIZED IS THE ENTRY, NOT THE PRICE: caching the resolved price set would
    // serve the first call's tier to every later call, so a long-context call would be
    // billed at the short-context rate silently. One catalog walk, two correct answers.
    FakeCatalog catalog = new([Entry(tiers: [Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m)])]);
    CatalogUsagePriceSource source = new(catalog);

    UsagePrices shortCall = await source.PricesForAsync("m", 1_000,
        TestContext.Current.CancellationToken);
    UsagePrices longCall = await source.PricesForAsync("m", 300_000,
        TestContext.Current.CancellationToken);

    Assert.Equal(0.000003m, shortCall.PromptPricePerToken);
    Assert.Equal(0.000006m, longCall.PromptPricePerToken);
    Assert.Equal(1, catalog.Calls);
  }

  [Fact]
  public async Task PricesForAsync_NoSizeSupplied_StampsTheBasePrices()
  {
    // A caller that does not know the size gets the base rung - never the most expensive one.
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry(tiers: [Tier(200_000, 0.000006m, 0.0000006m, 0.0000225m)])]));

    UsagePrices prices = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0.000003m, prices.PromptPricePerToken);
  }
}
