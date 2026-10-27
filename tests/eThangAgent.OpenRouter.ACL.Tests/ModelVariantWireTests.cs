using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The routing VARIANT on the wire, and the DELETION of the provider pin.
///     <para>OpenRouter expresses the two tradeoffs the picker offers as SUFFIXES ON THE MODEL
///     ID (<c>:nitro</c> sorts the endpoint pool by throughput and admits priority-tier
///     endpoints; <c>:floor</c> sorts by price and admits flex-tier endpoints). The suffix in
///     <c>model</c> is therefore the WHOLE routing decision, and the request carries no
///     <c>provider</c> object at all: the retired pin (<c>only</c> + <c>allow_fallbacks:false</c>)
///     narrowed the pool to a single endpoint, which is precisely the decision the user no
///     longer makes.</para>
///     <para>WHY NO PROVIDER KEY IS THE ASSERTION THAT MATTERS: a leftover pin would silently
///     override the variant's sorting - a <c>:floor</c> request pinned to one expensive endpoint
///     would pay that endpoint's price while the user asked for the cheapest - and the harness
///     would report that choice as the user's.</para></summary>
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class ModelVariantWireTests
{
  private const string BaseModel = "openai/gpt-5";

  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  [Fact]
  public async Task Body_TheStandardVariant_SendsTheBareModelId()
    => Assert.Equal(BaseModel, ModelOf(await CaptureBodyAsync(ModelFor(ModelVariant.Standard)).ConfigureAwait(true)));

  [Fact]
  public async Task Body_TheNitroVariant_RidesTheModelId_AsTheDocumentedSuffix()
    => Assert.Equal("openai/gpt-5:nitro", ModelOf(await CaptureBodyAsync(ModelFor(ModelVariant.Nitro)).ConfigureAwait(true)));

  [Fact]
  public async Task Body_TheFloorVariant_RidesTheModelId_AsTheDocumentedSuffix()
    => Assert.Equal("openai/gpt-5:floor",
        ModelOf(await CaptureBodyAsync(ModelFor(ModelVariant.Floor)).ConfigureAwait(true)));

  [Theory]
  [InlineData(ModelVariant.Standard)]
  [InlineData(ModelVariant.Nitro)]
  [InlineData(ModelVariant.Floor)]
  public async Task Body_NoVariant_SendsNoProviderObjectAtAll(ModelVariant variant)
  {
    string body = await CaptureBodyAsync(ModelFor(variant)).ConfigureAwait(true);

    // The whole routing decision is the model id. A "provider" object here would be a
    // resurrected pin competing with the variant's own sorting.
    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
    Assert.DoesNotContain("allow_fallbacks", body, StringComparison.Ordinal);
    Assert.DoesNotContain("\"only\"", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Body_APersistedDeadRoutingPayload_CannotResurrectAProviderObject()
  {
    // Persisted ProviderSettings written before the pin was removed still carry a "routing"
    // object. The ACL skips unknown members on read, so such a payload must produce NO provider
    // key - neither the dead routing nor a pin rebuilt from it.
    OpenRouterRequestSettings settings = OpenRouterRequestSettings.Parse(
        /*lang=json,strict*/ """{"routing":{"only":["someone-else"],"allow_fallbacks":true}}""")!;
    ModelConfig config = ModelConfig.Create(BaseModel + ":nitro", 64, 0.7f, 4096,
        providerSettings: OpenRouterRequestSettings.Serialize(settings)).Value!;

    string body = await CaptureBodyAsync(config).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
    Assert.Equal("openai/gpt-5:nitro", ModelOf(body));
  }

  [Fact]
  public async Task Body_TheVariantSuffix_ReachesTheWireVerbatim()
  {
    // Regression guard for the removal itself: a future refactor that normalized the model id
    // before sending would quietly drop the user's routing intent, and nothing else in the body
    // would record what they asked for.
    string body = await CaptureBodyAsync(ModelFor(ModelVariant.Nitro)).ConfigureAwait(true);
    Assert.Contains("\"model\":\"openai/gpt-5:nitro\"", body, StringComparison.Ordinal);
  }

  private static string ModelOf(string body)
  {
    using JsonDocument doc = JsonDocument.Parse(body);
    return doc.RootElement.GetProperty("model").GetString()!;
  }

  private static ModelConfig ModelFor(ModelVariant variant)
      => ModelConfig.Create(ModelVariants.ApplyTo(variant, BaseModel), 64, 0.7f, 4096).Value!;

  private static async Task<string> CaptureBodyAsync(ModelConfig config)
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        config, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    return captured!;
  }
}
#pragma warning restore CA2000
