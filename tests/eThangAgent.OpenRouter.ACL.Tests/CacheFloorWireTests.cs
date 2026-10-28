using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.OpenRouter.ACL.Tests;

// The Cache Floor variant on the wire: provider.order carrying the model's endpoints sorted by
// advertised cache-read price, with fallbacks allowed so an unavailable endpoint cannot fail
// the turn. Every other variant sends NO provider object.
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class CacheFloorWireTests
{
  private const string BaseModel = "alpha/model";
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private static ModelProviderEntry Endpoint(string slug, decimal? cacheRead)
      => new(BaseModel, "Provider", 0.0000001m, cacheRead, 0.0000005m, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
      {
        Variant = new EndpointVariant(slug),
      };

  [Fact]
  public async Task Body_CacheFloor_SendsTheOrderCheapestCacheReadFirst()
  {
    string body = await CaptureBodyAsync(ModelVariant.CacheFloor,
        [Endpoint("b/fp8", 0.00000030m), Endpoint("a/fp4", 0.00000002m)]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.Equal(["a/fp4", "b/fp8"],
        provider.GetProperty("order").EnumerateArray().Select(e => e.GetString()));
    Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
  }

  [Fact]
  public async Task Body_CacheFloor_LeavesTheModelIdBare()
  {
    string body = await CaptureBodyAsync(ModelVariant.CacheFloor,
        [Endpoint("a/fp4", 0.00000002m)]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(BaseModel, doc.RootElement.GetProperty("model").GetString());
  }

  [Fact]
  public async Task Body_CacheFloor_SendsNoOnlyKey_SoAStaleSlugCannotFailTheTurn()
  {
    string body = await CaptureBodyAsync(ModelVariant.CacheFloor,
        [Endpoint("a/fp4", 0.00000002m)]).ConfigureAwait(true);

    Assert.DoesNotContain("\"only\"", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Body_CacheFloor_WithNoAdvertisedCacheReadPrice_SendsNoProviderObject()
  {
    // Degrade to the provider's own routing rather than sending an empty order.
    string body = await CaptureBodyAsync(ModelVariant.CacheFloor,
        [Endpoint("a/fp4", null)]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Theory]
  [InlineData(ModelVariant.Standard)]
  [InlineData(ModelVariant.Nitro)]
  [InlineData(ModelVariant.Floor)]
  public async Task Body_EveryOtherVariant_SendsNoProviderObject(ModelVariant variant)
  {
    string body = await CaptureBodyAsync(variant, [Endpoint("a/fp4", 0.00000002m)]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  private static async Task<string> CaptureBodyAsync(ModelVariant variant,
      IReadOnlyList<ModelProviderEntry> endpoints)
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new FakeCatalog(endpoints));

    ModelConfig config = ModelConfig.Create(BaseModel, 64, 0.7f, 4096, variant: variant).Value!;
    _ = await provider.SendAsync(
        config, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    return captured!;
  }

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(entries));
  }
}
#pragma warning restore CA2000
