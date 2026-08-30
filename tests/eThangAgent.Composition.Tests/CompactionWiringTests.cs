using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

public class CompactionWiringTests
{
  private static ModelProviderEntry Entry(string model, string provider, decimal promptPrice, int window, bool tools = true) =>
      new(model, provider, promptPrice, 0m, window, 4096, tools, false, null, null, null, null, null, "");

  [Fact]
  public void Providers_ExposeRoutingWindowConstant()
  {
    Assert.Equal("openrouter/auto", Providers.RoutingModelId);
    Assert.True(Providers.RoutingContextWindow > 0);
  }

  [Fact]
  public async Task CompactionModelResolver_UnsetPreference_PrefersCheapestCapableEntry()
  {
    FakePreferences preferences = new();
    FakeCatalog catalog = new([
        Entry("expensive", "openrouter", 0.00001m, 100_000),
        Entry("cheap", "openrouter", 0.000001m, 200_000),
        Entry("notool", "openrouter", 0.0000001m, 300_000, tools: false), // cheapest but not tool-capable
    ]);
    CompactionModelResolver resolver = new(preferences, catalog, "openrouter", @"C:\ws");

    ModelConfig? resolved = await resolver.ResolveAsync(32 * 1024, 0.7f, TestContext.Current.CancellationToken);

    Assert.NotNull(resolved);
    Assert.Equal("cheap", resolved.ModelId);
    Assert.Equal(200_000, resolved.ContextWindow);
  }

  [Fact]
  public async Task CompactionModelResolver_SetPreference_Wins()
  {
    FakePreferences preferences = new();
    preferences.Store[CompactionModelResolver.PreferenceKey("openrouter", @"C:\ws")] = "expensive";
    FakeCatalog catalog = new([
        Entry("expensive", "openrouter", 0.00001m, 100_000),
        Entry("cheap", "openrouter", 0.000001m, 200_000),
    ]);
    CompactionModelResolver resolver = new(preferences, catalog, "openrouter", @"C:\ws");

    ModelConfig? resolved = await resolver.ResolveAsync(4096, 0.5f, TestContext.Current.CancellationToken);

    Assert.NotNull(resolved);
    Assert.Equal("expensive", resolved.ModelId);
  }

  private sealed class FakePreferences : eThangAgent.Storage.ACL.IAppPreferenceStore
  {
    public Dictionary<string, string> Store { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Store[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Store.Remove(key));
  }

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(entries));
  }
}
