using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderFactoryTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  [Fact]
  public async Task Create_PerSpawnModel_ReachesWireWithBaseCredentials()
  {
    HttpRequestMessage? captured = null;
    string? body = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req;
      Assert.NotNull(req.Content);
      body = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProviderFactory factory = new(
        new OpenRouterConfiguration("test-key", BaseUrl), http);
    IModelProvider provider = factory.Create(ModelConfig.Create("mock/sub-model", 128, 0.7f, 4096).Value!);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("mock/sub-model", 128, 0.7f, 4096).Value!,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Contains("\"model\":\"mock/sub-model\"", body, StringComparison.Ordinal);
    Assert.Equal("Bearer test-key", captured!.Headers.Authorization!.ToString());
  }

  [Fact]
  public async Task Create_CacheFloor_UsesCatalogForChildProvider()
  {
    string? body = null;
    FakeHttpMessageHandler handler = new(async request =>
    {
      Assert.NotNull(request.Content);
      body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    ModelProviderEntry endpoint = new("mock/sub-model", "Provider", 0.1m, 0.01m, 0.2m,
        128_000, 8192, true, false, true, null, null, null, null, null, null)
    { Variant = new EndpointVariant("provider/flex") };
    OpenRouterModelProviderFactory factory = new(new OpenRouterConfiguration("test-key", BaseUrl),
        http, new Catalog([endpoint]));
    ModelConfig config = ModelConfig.Create("mock/sub-model", 128, 0.7f, 4096,
        variant: ModelVariant.CacheFloor).Value!;

    Result<ModelResponse> result = await factory.Create(config).SendAsync(config,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body!);
    Assert.Equal("provider/flex", doc.RootElement.GetProperty("provider")
        .GetProperty("order")[0].GetString());
  }

  private sealed class Catalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(entries));
  }

  [Fact]
  public void Create_NullConfig_Throws()
  {
    using HttpClient http = new();
    OpenRouterModelProviderFactory factory = new(
        new OpenRouterConfiguration("k", BaseUrl), http);

    _ = Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
  }
}
