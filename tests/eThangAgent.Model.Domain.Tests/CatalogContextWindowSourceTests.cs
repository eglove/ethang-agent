using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The catalog is the only window source, and it is keyed on the BASE MODEL.
///     <para>WHY THERE IS NO PROVIDER DIMENSION (it replaced the provider pin): the catalog
///     carries one row per routable endpoint, and every endpoint of a model serves the same
///     window. The old parameter therefore could not change the answer — it only let a caller
///     narrow the lookup to the endpoints that share one provider NAME, which is not a fact a
///     turn can act on once routing is expressed as a variant suffix.</para></summary>
public class CatalogContextWindowSourceTests
{
  private static ModelProviderEntry Entry(string model, string provider, int window) =>
      new(model, provider, 0m, null, 0m, window, 4096, true, false, true, null, null, null, null, null, "");

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(entries));
  }

  private sealed class FailingCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError("CatalogDown", "boom")));
  }

  [Fact]
  public async Task WindowFor_TakesTheWidestEndpointWindowOfTheModel()
  {
    // The endpoints of one model can report different windows, and the budget must fit the one
    // that actually serves - which is not knowable before the request. The widest is the honest
    // reading for a router, and never under-budgets a turn whose context would have fit.
    CatalogContextWindowSource source = new(new FakeCatalog([Entry("m", "a", 1000), Entry("m", "b", 2000)]));

    Assert.Equal(2000, await source.WindowForAsync("m", TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_ARoutingVariantSuffix_ResolvesTheBaseModel()
  {
    // THE SUFFIX IS NOT PART OF THE KEY. A session's committed variant rides the wire id, and a
    // lookup on the suffixed id would find nothing - failing every turn of a nitro or floor
    // session with 'no catalog context window' for a model the catalog clearly lists.
    CatalogContextWindowSource source = new(new FakeCatalog([Entry("m", "a", 1000)]));

    Assert.Equal(1000, await source.WindowForAsync("m:nitro", TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(1000, await source.WindowForAsync("m:floor", TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_UnknownModel_ReturnsNull()
  {
    CatalogContextWindowSource source = new(new FakeCatalog([]));

    Assert.Null(await source.WindowForAsync("nope", TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_CatalogFailure_ReturnsNull()
  {
    CatalogContextWindowSource source = new(new FailingCatalog());

    Assert.Null(await source.WindowForAsync("m", TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_RoutingPseudoModel_ResolvesFromCatalogRow()
  {
    // The catalog is the ONLY window source: no curated constants. The live API carries
    // openrouter/auto with context_length 2000000; it resolves like any row.
    CatalogContextWindowSource source = new(new FakeCatalog([Entry("openrouter/auto", "OpenRouter", 2_000_000)]));

    Assert.Equal(2_000_000, await source.WindowForAsync("openrouter/auto", TestContext.Current.CancellationToken).ConfigureAwait(true));
  }
}
