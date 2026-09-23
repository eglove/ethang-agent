using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>The compaction summarizer config stamps the chosen catalog entry's vision
///     capability: the resolver holds the entry in hand and passes its flag through.</summary>
public class CompactionModelVisionFlagTests
{
  private sealed class FakePreferences : Storage.ACL.IAppPreferenceStore
  {
    public Dictionary<string, string> Store { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Store[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(Store.Remove(key));
  }

  private sealed class FakeCatalog(params ModelProviderEntry[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));
  }

  [Fact]
  public async Task PreferredEntry_VisionCapable_ConfigCarriesFlagTrue()
  {
    FakePreferences preferences = new();
    preferences.Store[CompactionModelResolver.PreferenceKey("openrouter", @"C:\ws")] = "vision/model";
    CompactionModelResolver resolver = new(preferences,
        new FakeCatalog(new ModelProviderEntry("vision/model", "openrouter", 0.000001m, 0.000002m,
            200_000, 8_192, true, true, null, null, null, null, null, "")), "openrouter", @"C:\ws");

    ModelConfig? resolved = await resolver.ResolveAsync(32 * 1024, 0.7f, TestContext.Current.CancellationToken);

    Assert.NotNull(resolved);
    Assert.True(resolved.AcceptsImageInput);
  }

  [Fact]
  public async Task PreferredEntry_TextOnly_ConfigCarriesFlagFalse()
  {
    FakePreferences preferences = new();
    preferences.Store[CompactionModelResolver.PreferenceKey("openrouter", @"C:\ws")] = "text/model";
    CompactionModelResolver resolver = new(preferences,
        new FakeCatalog(new ModelProviderEntry("text/model", "openrouter", 0.000001m, 0.000002m,
            200_000, 8_192, true, false, null, null, null, null, null, "")), "openrouter", @"C:\ws");

    ModelConfig? resolved = await resolver.ResolveAsync(32 * 1024, 0.7f, TestContext.Current.CancellationToken);

    Assert.NotNull(resolved);
    Assert.False(resolved.AcceptsImageInput);
  }
}
