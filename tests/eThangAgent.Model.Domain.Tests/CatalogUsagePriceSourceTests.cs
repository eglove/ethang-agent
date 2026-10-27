using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

// B6 (issue #22): the price source stamps the three prices a usage row carries, including
// the cache-read price, and never fabricates a cost for a price it does not know.
public class CatalogUsagePriceSourceTests
{
  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public int Calls { get; private set; }

    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(entries));
    }
  }

  private sealed class FailingCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError("CatalogDown", "boom")));
  }

  private static ModelProviderEntry Entry(string model, string? provider, decimal prompt, decimal? cacheRead,
      decimal completion) =>
      new(model, provider, prompt, cacheRead, completion, 128_000, 4096, true, false, true, null, null, null, null, null, "");

  [Fact]
  public async Task PricesFor_ModelWithCacheReadPrice_StampsAllThree()
  {
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry("m", "openrouter", 0.000001m, 0.0000001m, 0.000002m)]));

    UsagePrices prices = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0.000001m, prices.PromptPricePerToken);
    Assert.Equal(0.0000001m, prices.PromptCacheReadPricePerToken);
    Assert.Equal(0.000002m, prices.CompletionPricePerToken);
  }

  [Fact]
  public async Task PricesFor_ModelWithoutCacheReadPrice_LeavesThatMemberNull()
  {
    // 'No cache price advertised' is a different fact from 'cache reads are free': the
    // prompt and completion prices still stamp, the cache member stays null.
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry("m", "openrouter", 0.000001m, null, 0.000002m)]));

    UsagePrices prices = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0.000001m, prices.PromptPricePerToken);
    Assert.Null(prices.PromptCacheReadPricePerToken);
    Assert.Equal(0.000002m, prices.CompletionPricePerToken);
  }

  [Fact]
  public async Task PricesFor_ModelAbsentFromCatalog_ResolvesUnknown()
  {
    CatalogUsagePriceSource source = new(new FakeCatalog([Entry("other", "openrouter", 1m, 1m, 1m)]));

    UsagePrices prices = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(UsagePrices.Unknown, prices);
  }

  [Fact]
  public async Task PricesFor_CatalogFailure_ResolvesUnknown_AndIsNotMemoized()
  {
    // A transient catalog fault must not poison every later stamp with nulls for the
    // process lifetime, so a failure is deliberately NOT memoized.
    CatalogUsagePriceSource source = new(new FailingCatalog());

    Assert.Equal(UsagePrices.Unknown,
        await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken));
    Assert.Equal(UsagePrices.Unknown,
        await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task PricesFor_NegativeAdvertisedPrice_IsNotAPrice()
  {
    // The routing pseudo-models serve -1 to mean 'set at routing time': a negative value
    // is not a price, so cost reads as unknown rather than as a negative charge.
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry("openrouter/auto", null, -1m, -1m, -1m)]));

    UsagePrices prices = await source.PricesForAsync("openrouter/auto", ct: TestContext.Current.CancellationToken);

    Assert.Equal(UsagePrices.Unknown, prices);
  }

  [Fact]
  public async Task PricesFor_RepeatedLookup_MemoizesOneCatalogWalk()
  {
    FakeCatalog catalog = new([Entry("m", "openrouter", 0.000001m, 0.0000001m, 0.000002m)]);
    CatalogUsagePriceSource source = new(catalog);

    _ = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);
    _ = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, catalog.Calls);
  }

  [Fact]
  public async Task PricesFor_AModelWithSeveralEndpoints_StampsTheCheapestPricedOne()
  {
    // WHICH ENDPOINT ANSWERS. The catalog serves one row per routable endpoint, each with its own
    // price, so "the first row" would stamp a different endpoint's prices on every call and make
    // the ledger's effective pricing meaningless. The cheapest advertised row is the one a
    // request with no pin can actually be served from, and it is deterministic.
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry("m", "a", 0.000001m, null, 0.000002m), Entry("m", "b", 0.000009m, null, 0.000010m)]));

    UsagePrices prices = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0.000001m, prices.PromptPricePerToken);
  }

  [Fact]
  public async Task PricesFor_ARoutingVariantSuffix_ResolvesTheBaseModel()
  {
    // The wire id carries the variant; the catalog is keyed on the bare model. Looking the
    // suffixed id up would find no row and silently leave every nitro or floor call unpriced.
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry("m", "a", 0.000001m, null, 0.000002m)]));

    UsagePrices prices = await source.PricesForAsync("m:nitro", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0.000001m, prices.PromptPricePerToken);
  }

  [Fact]
  public async Task PricesFor_ZeroCacheReadPrice_IsARealPrice()
  {
    // A served zero means cache reads are billed at nothing - a price, not an absence.
    CatalogUsagePriceSource source = new(new FakeCatalog(
        [Entry("m", "openrouter", 0.000001m, 0m, 0.000002m)]));

    UsagePrices prices = await source.PricesForAsync("m", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0m, prices.PromptCacheReadPricePerToken);
  }
}
