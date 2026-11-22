using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The window source asks the catalog of the vendor that serves the model, and is
///     keyed on the BASE MODEL.
///     <para>WHY THE VENDOR IS PART OF THE QUESTION: a session is not tied to a provider, so its
///     ordered model list names a different vendor per entry and each vendor's catalog is a
///     separate read. A lookup that named only the model asked whichever catalog the session was
///     opened under, and reported every other vendor's models as unknown - which is what refused
///     a Cloudflare model's turn in an OpenRouter session.</para>
///     <para>A NULL VENDOR ASKS EVERY CONFIGURED VENDOR: a persisted pin records a model and not
///     a vendor, and an entry written before vendors existed names none either, so neither may
///     be refused for a fact it never stated.</para></summary>
public class CatalogContextWindowSourceTests
{
  private const string OpenRouter = ModelVendors.OpenRouter;
  private const string Cloudflare = ModelVendors.Cloudflare;

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

  /// <summary>A resolver over exactly the vendors a test names; every other vendor has no
  ///     catalog, which is the credential-not-configured shape.</summary>
  private sealed class Catalogs(params (string Vendor, IModelCatalog Catalog)[] pairs)
      : IModelProviderResolver
  {
    public Result<IModelCatalog> Catalog(string vendorId) =>
        pairs.FirstOrDefault(pair => pair.Vendor == vendorId).Catalog is { } found
            ? Result.Success(found)
            : Result.Failure<IModelCatalog>(new DomainError("ProviderNotConfigured", "none"));

    public Result<IModelProvider> Resolve(string vendorId, ModelConfig config) =>
        Result.Failure<IModelProvider>(new DomainError("ProviderNotConfigured", "none"));
  }

  [Fact]
  public async Task WindowFor_TakesTheWidestEndpointWindowOfTheModel()
  {
    // The endpoints of one model can report different windows, and the budget must fit the one
    // that actually serves - which is not knowable before the request. The widest is the honest
    // reading for a router, and never under-budgets a turn whose context would have fit.
    CatalogContextWindowSource source = new(new Catalogs((OpenRouter,
        new FakeCatalog([Entry("m", "a", 1000), Entry("m", "b", 2000)]))));

    Assert.Equal(2000, await source.WindowForAsync("m", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_ARoutingVariantSuffix_ResolvesTheBaseModel()
  {
    // THE SUFFIX IS NOT PART OF THE KEY. A session's committed variant rides the wire id, and a
    // lookup on the suffixed id would find nothing - failing every turn of a nitro or floor
    // session with 'no catalog context window' for a model the catalog clearly lists.
    CatalogContextWindowSource source = new(new Catalogs((OpenRouter,
        new FakeCatalog([Entry("m", "a", 1000)]))));

    Assert.Equal(1000, await source.WindowForAsync("m:nitro", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(1000, await source.WindowForAsync("m:floor", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_UnknownModel_ReturnsNull()
  {
    CatalogContextWindowSource source = new(new Catalogs((OpenRouter, new FakeCatalog([]))));

    Assert.Null(await source.WindowForAsync("nope", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_CatalogFailure_ReturnsNull()
  {
    CatalogContextWindowSource source = new(new Catalogs((OpenRouter, new FailingCatalog())));

    Assert.Null(await source.WindowForAsync("m", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_RoutingPseudoModel_ResolvesFromCatalogRow()
  {
    // The catalog is the ONLY window source: no curated constants. The live API carries
    // openrouter/auto with context_length 2000000; it resolves like any row.
    CatalogContextWindowSource source = new(new Catalogs((OpenRouter,
        new FakeCatalog([Entry("openrouter/auto", "OpenRouter", 2_000_000)]))));

    Assert.Equal(2_000_000, await source.WindowForAsync("openrouter/auto", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_ModelServedByAnotherVendor_ResolvesFromThatVendorsCatalog()
  {
    // THE REFUSAL THIS REPLACES: a Cloudflare model named in a session opened under OpenRouter
    // found no window, because the lookup asked OpenRouter's catalog and Cloudflare's was never
    // consulted. A session is not tied to a provider, so the entry's vendor decides which
    // catalog answers.
    CatalogContextWindowSource source = new(new Catalogs(
        (OpenRouter, new FakeCatalog([Entry("m", "a", 1000)])),
        (Cloudflare, new FakeCatalog([Entry("@cf/deepseek-ai/deepseek-v4.1-flash-1017", "cloudflare", 1_048_576)]))));

    Assert.Equal(1_048_576, await source.WindowForAsync("@cf/deepseek-ai/deepseek-v4.1-flash-1017",
        Cloudflare, TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_NoVendorNamed_AsksEveryConfiguredVendor()
  {
    // A PIN RECORDS A MODEL, NOT A VENDOR. Asking only the session's own vendor would refuse a
    // resumed session whose pin names a model another vendor serves.
    CatalogContextWindowSource source = new(new Catalogs(
        (OpenRouter, new FakeCatalog([Entry("m", "a", 1000)])),
        (Cloudflare, new FakeCatalog([Entry("@cf/x/y", "cloudflare", 4096)]))));

    Assert.Equal(4096, await source.WindowForAsync("@cf/x/y", vendorId: null,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_VendorWithoutACatalog_IsSkippedRatherThanFatal()
  {
    // A credential that was never configured leaves its vendor absent. A model the configured
    // vendor lists still resolves.
    CatalogContextWindowSource source = new(new Catalogs(
        (Cloudflare, new FakeCatalog([Entry("@cf/x/y", "cloudflare", 4096)]))));

    Assert.Equal(4096, await source.WindowForAsync("@cf/x/y", vendorId: null,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Null(await source.WindowForAsync("m", OpenRouter,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }
}
