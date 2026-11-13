using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.OpenRouter.ACL.Tests;

// The retention restriction on the wire: provider.zdr, on by default, and HARD when the catalog
// proves nothing eligible serves the model.
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class ZeroDataRetentionWireTests
{
  private const string BaseModel = "alpha/model";
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private static ModelProviderEntry Endpoint(string slug, decimal? cacheRead = null)
      => new(BaseModel, "Provider", 0.0000001m, cacheRead, 0.0000005m, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
      {
        Variant = EndpointVariant.Parse(slug, null, null),
      };

  /// <summary>An eligible list naming one endpoint, as the provider publishes it.</summary>
  private static RetentionEligibleList Eligible(string modelId, string providerName, string slug,
      bool safetyRetention = false)
      => RetentionEligibleList.Of(
          [new RetentionEligibleEndpoint(modelId, providerName, slug, safetyRetention)]);

  private static ModelConfig Model(RetentionPolicy retention = RetentionPolicy.ZeroDataRetention,
      ModelVariant variant = ModelVariant.Standard)
      => ModelConfig.Create(BaseModel, 64, 0.7f, 4096, variant: variant,
          retention: retention).Value!;

  [Fact]
  public async Task Body_TheDefaultConfig_RestrictsRoutingToZeroRetentionEndpoints()
  {
    // The restriction is the DEFAULT, so a config nobody configured carries it. This is the
    // assertion that makes 'silence means on' true on the wire and not only in the dialog.
    string body = await CaptureBodyAsync(Model(), [Endpoint("alpha/zdr")]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    // Nothing else is invented: the restriction alone names no order and no fallback policy.
    Assert.False(provider.TryGetProperty("order", out _));
    Assert.False(provider.TryGetProperty("allow_fallbacks", out _));
  }

  [Fact]
  public async Task Body_AnExplicitlyLoosenedSession_SendsNoProviderObjectAtAll()
  {
    // Only an explicit false removes the restriction, and removing it must leave the body
    // exactly as it was before this feature existed - no empty provider object.
    string body = await CaptureBodyAsync(Model(RetentionPolicy.RetentionAndTraining), [Endpoint("alpha")])
        .ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Fact]
  public async Task Body_TheRestrictionAndTheCacheFloorOrder_ShareOneProviderObject()
  {
    // The provider block is one member of the body, so two independent routing facts must be
    // assembled into it rather than each writing the key and overwriting the other.
    string body = await CaptureBodyAsync(Model(variant: ModelVariant.CacheFloor),
        [Endpoint("alpha/zdr", cacheRead: 0.00000002m)]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.Equal(["alpha/zdr"],
        provider.GetProperty("order").EnumerateArray().Select(e => e.GetString()));
    Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
  }
  [Fact]
  public async Task Send_TheCatalogProvesNothingEligible_FailsNamingTheRestriction()
  {
    // THE RESTRICTION IS HARD. Every other routing preference in this harness degrades quietly;
    // this one is a statement about where the prompts may go, so a silent fallback to a
    // retaining provider would break it without ever saying so.
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new FakeCatalog(
        [Endpoint("alpha")], Eligible("other/model", "Provider", "other")));

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ZeroDataRetentionUnavailable", result.Error.Code);
    Assert.Contains("zero-data-retention", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(BaseModel, result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Send_AnEligibleEndpointWithNoZdrSlug_IsServed()
  {
    // THE DEFECT THIS FIXES: eligibility was read from the endpoint's tag, so a model whose
    // eligible endpoints carry no 'zdr' segment was refused. Measured live 2026-11-01,
    // deepseek/deepseek-v4.1-flash-1017 has 42 eligible endpoints and not one tag says 'zdr'.
    // The provider's own list is the authority, and it names this endpoint.
    ModelProviderEntry plain = Endpoint("alpha");
    string body = await CaptureBodyAsync(Model(), [plain],
        Eligible(BaseModel, "Provider", "alpha")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.True(doc.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
  }

  [Fact]
  public async Task Send_AConsultedListThatOmitsTheModel_FailsNamingTheRestriction()
  {
    // The list was consulted and does not name this model, which is the one fact that proves
    // nothing eligible serves it. An endpoint row with a tag is what makes the catalog's silence
    // about the model meaningful rather than an absence of evidence.
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new FakeCatalog(
        [Endpoint("alpha")], Eligible("other/model", "Provider", "other")));

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ZeroDataRetentionUnavailable", result.Error.Code);
  }

  [Fact]
  public async Task Send_AnUnconsultedList_IsNotProofOfIneligibility()
  {
    // A list that was never fetched says nothing about the model's endpoints. Refusing here
    // would fail turns over an absence of evidence - the same rule a failed catalog read follows.
    string body = await CaptureBodyAsync(Model(), [Endpoint("alpha")]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.True(doc.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
  }

  [Fact]
  public async Task Send_AnEligibleEndpointExists_IsServed()
  {
    string body = await CaptureBodyAsync(Model(), [Endpoint("alpha/zdr")]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.True(doc.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
  }

  [Fact]
  public async Task Send_ALoosenedSession_IsServedWithNoEligibleEndpoint()
  {
    // The same catalog that fails a restricted session must serve a loosened one: the check
    // exists to enforce the promise, not to gate the model.
    string body = await CaptureBodyAsync(Model(RetentionPolicy.RetentionAndTraining), [Endpoint("alpha")])
        .ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(BaseModel, doc.RootElement.GetProperty("model").GetString());
  }

  [Fact]
  public async Task Send_AnUnavailableCatalog_IsNotProofOfIneligibility()
  {
    // A catalog read fault says nothing about the model's endpoints. Refusing here would fail
    // turns over a local fault, so the request is sent with the restriction on and the provider
    // decides.
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new FailingCatalog());

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.True(doc.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
  }


  [Fact]
  public async Task Send_ACatalogWithNoRowsForTheModel_IsNotProofOfIneligibility()
  {
    // A catalog that has not loaded this model has said nothing about its endpoints. Refusing
    // here would fail every turn for a model the catalog simply has not reached yet.
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog([Endpoint("alpha/zdr") with { ModelId = "other/model" }]));

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.True(doc.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
  }


  [Fact]
  public async Task Send_ACatalogThatNeverDescribedTheEndpoints_IsNotProofOfIneligibility()
  {
    // A row parsed from the model catalog rather than the endpoints payload says what the model
    // is, never how an endpoint retains. Reading that silence as 'retains' would fail turns for
    // every model whose endpoints were never crawled.
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog([Endpoint("alpha") with { Variant = EndpointVariant.Unspecified }]));

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.True(doc.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
  }

  private static async Task<string> CaptureBodyAsync(ModelConfig config,
      IReadOnlyList<ModelProviderEntry> endpoints, RetentionEligibleList? eligible = null)
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog(endpoints, eligible));

    _ = await provider.SendAsync(
        config, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    return captured!;
  }

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries,
      RetentionEligibleList? eligible = null) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(entries));

    public Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
        => Task.FromResult(eligible);
  }

  private sealed class FailingCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogUnavailable", "down")));

    public Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
        => Task.FromResult<RetentionEligibleList?>(null);
  }
}
#pragma warning restore CA2000
