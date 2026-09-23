using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL.Tests;

/// <summary>The static z.ai catalog pins per-model vision capability: glm-5.3
///     accepts image input, glm-5.3-flash does not. The values travel the
///     ModelProviderEntry capability surface the model picker reads.</summary>
public class ZaiModelCatalogVisionFlagTests
{
  [Fact]
  public async Task Glm5_3_AcceptsImageInput()
  {
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync(TestContext.Current.CancellationToken);

    ModelProviderEntry flagship = Assert.Single(result.Value!, e => e.ModelId == "glm-5.3");
    Assert.True(flagship.SupportsVision);
  }

  [Fact]
  public async Task Glm5_3Flash_DoesNotAcceptImageInput()
  {
    ZaiModelCatalog catalog = new();

    Result<IReadOnlyList<ModelProviderEntry>> result = await catalog.GetAsync(TestContext.Current.CancellationToken);

    ModelProviderEntry flash = Assert.Single(result.Value!, e => e.ModelId == "glm-5.3-flash");
    Assert.False(flash.SupportsVision);
  }
}
