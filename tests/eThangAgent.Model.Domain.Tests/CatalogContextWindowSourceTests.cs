using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

public class CatalogContextWindowSourceTests
{
  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(entries));
  }

  private static ModelProviderEntry Entry(string model, string provider, int window) =>
      new(model, provider, 0m, 0m, window, 4096, true, false, null, null, null, null, null, "");

  [Fact]
  public async Task WindowFor_MatchesModelAndProvider()
  {
    CatalogContextWindowSource source = new(new FakeCatalog([Entry("m", "a", 1000), Entry("m", "b", 2000)]));
    Assert.Equal(2000, await source.WindowForAsync("m", "b", ct: TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_NullProvider_TakesFirstModelMatch()
  {
    CatalogContextWindowSource source = new(new FakeCatalog([Entry("m", "a", 1000)]));
    Assert.Equal(1000, await source.WindowForAsync("m", null, ct: TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task WindowFor_UnknownModel_ReturnsNull()
  {
    CatalogContextWindowSource source = new(new FakeCatalog([]));
    Assert.Null(await source.WindowForAsync("nope", null, ct: TestContext.Current.CancellationToken).ConfigureAwait(true));
  }
}
