using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL.Tests;

public class ZaiModelCatalogTests
{
  [Fact]
  public async Task GetAsync_ReturnsPopulatedCatalog()
  {
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync();

    Assert.True(result.IsSuccess);
    Assert.NotEmpty(result.Value);
    Assert.Contains(result.Value, e => e.ModelId == "glm-5.3");
  }

  [Fact]
  public async Task Catalog_IsTheSelectableLineup_ForTheModelPicker()
  {
    // z.ai sessions run no automatic selection: the user picks one of these in the
    // host's model picker, and glm-5.3-flash is the session default.
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync();

    Assert.Equal(["glm-5.3", "glm-5.3-flash"], result.Value!.Select(e => e.ModelId).ToList());
  }

  [Fact]
  public async Task Entries_HaveUniqueModelIds_AndUniqueExclusionKeys()
  {
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync();

    IReadOnlyList<ModelProviderEntry> entries = result.Value!;
    Assert.Equal(entries.Count, entries.Select(e => e.ModelId).ToHashSet().Count);
    Assert.Equal(entries.Count, entries.Select(e => e.Key).ToHashSet().Count);
  }

  [Fact]
  public async Task Entries_CarryZaiProviderName_AndToolUse_AndDescriptions()
  {
    // The provider name doubles as the exclusion-key discriminator, so it must be
    // stamped on every entry; descriptions carry the capability signal because z.ai
    // publishes no numeric scores.
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync();

    Assert.All(result.Value!, e =>
    {
      Assert.Equal("z.ai", e.ProviderName);
      Assert.True(e.SupportsToolUse);
      Assert.False(e.SupportsVision);
      Assert.False(string.IsNullOrWhiteSpace(e.Description));
    });
  }

  [Fact]
  public async Task Entries_CarrySaneLimitsAndPrices()
  {
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync();

    Assert.All(result.Value!, e =>
    {
      Assert.True(e.ContextLength > 0);
      Assert.True(e.MaxCompletionTokens > 0);
      Assert.True(e.PromptPricePerToken >= 0);
      Assert.True(e.CompletionPricePerToken >= 0);
    });
  }
}
