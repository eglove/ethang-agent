using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.OpenRouter.ACL.Tests;

// The three retention tiers on the wire: which of them writes provider.zdr, which pins the
// admitted endpoint slugs in provider.only, and which fails the turn instead of routing to a
// provider that keeps the prompts.
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class RetentionWireTests
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

  /// <summary>The endpoints the provider publishes for the base model, each with the tier it is
  ///     eligible under.</summary>
  private static RetentionEligibleList Eligible(params (string Slug, bool SafetyRetention)[] rows)
      => RetentionEligibleList.Of(rows.Select(row =>
          new RetentionEligibleEndpoint(BaseModel, "Provider", row.Slug, row.SafetyRetention)));

  /// <summary>An eligible list naming one endpoint of ANOTHER model, which is what makes the
  ///     catalog's silence about the base model a fact rather than an absence of evidence.</summary>
  private static RetentionEligibleList EligibleFor(string modelId, string slug)
      => RetentionEligibleList.Of([new RetentionEligibleEndpoint(modelId, "Provider", slug, false)]);

  private static ModelConfig Model(RetentionPolicy retention = RetentionPolicy.ZeroDataRetention,
      ModelVariant variant = ModelVariant.Standard)
      => ModelConfig.Create(BaseModel, 64, 0.7f, 4096, variant: variant,
          retention: retention).Value!;

  private static ModelRequest Request()
      => new([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]);

  [Fact]
  public async Task Body_TheDefaultTier_WritesZdrAndPinsTheAdmittedSlugs()
  {
    // The strict tier is the DEFAULT, so a config nobody configured carries it: zdr on, and the
    // admitted slugs named so the provider cannot admit a safety-retention endpoint on its own
    // account setting.
    (string body, _) = await SendAsync(Model(), [Endpoint("alpha/zdr")],
        Eligible(("alpha/zdr", false))).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.Equal(["alpha/zdr"],
        provider.GetProperty("only").EnumerateArray().Select(e => e.GetString()));
    // Nothing else is invented: the restriction alone names no order and no fallback policy.
    Assert.False(provider.TryGetProperty("order", out _));
    Assert.False(provider.TryGetProperty("allow_fallbacks", out _));
  }

  [Fact]
  public async Task Body_RetentionAndTraining_WritesNoProviderObjectAndConsultsNoList()
  {
    // The loosened tier states no restriction, so it writes nothing and asks nothing: an
    // eligible-list read it does not act on would be work done to reach no decision.
    (string body, FakeCatalog catalog) = await SendAsync(
        Model(RetentionPolicy.RetentionAndTraining), [Endpoint("alpha")],
        Eligible(("alpha", false))).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
    Assert.Equal(0, catalog.EligibleReads);
  }

  [Fact]
  public async Task Body_SafetyRetention_WritesZdrAndNoOnlyList()
  {
    // Safety Retention accepts every endpoint the eligible list names, so it consults the list
    // and pins nothing: an only list here would exclude endpoints the tier admits.
    (string body, FakeCatalog catalog) = await SendAsync(
        Model(RetentionPolicy.SafetyRetention), [Endpoint("alpha/zdr")],
        Eligible(("alpha/zdr", false))).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.False(provider.TryGetProperty("only", out _));
    Assert.Equal(1, catalog.EligibleReads);
  }

  [Fact]
  public async Task Body_ZeroDataRetention_NeverPinsASafetyRetentionEndpoint()
  {
    // THE ACCEPTANCE CRITERION: a strict request for a model with a mixed endpoint set carries
    // no safety-retention slug. Only is the one per-endpoint exclusion available - provider.ignore
    // is provider-level, so ignoring a provider to drop its safety endpoint would drop that
    // provider's strict endpoint for the same model too.
    (string body, _) = await SendAsync(Model(),
        [Endpoint("alpha/zdr"), Endpoint("alpha/safety")],
        Eligible(("alpha/zdr", false), ("alpha/safety", true))).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.Equal(["alpha/zdr"],
        provider.GetProperty("only").EnumerateArray().Select(e => e.GetString()));
  }

  [Fact]
  public async Task Body_ZeroDataRetentionWithAnUntaggedAdmittedEndpoint_SendsNoOnlyList()
  {
    // An endpoint the provider gave no tag for is not addressable, and a slug list naming an
    // invented one fails the whole request. The tier is still served, so the restriction rides
    // the request alone and the provider routes among the endpoints it considers eligible.
    (string body, _) = await SendAsync(Model(), [Endpoint("alpha")],
        RetentionEligibleList.Of(
            [new RetentionEligibleEndpoint(BaseModel, "Provider", null, false)])).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.False(provider.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Body_ZeroDataRetentionWithTheCacheFloorVariant_SharesOneProviderObject()
  {
    // The provider block is one member of the body, so the retention restriction and the Cache
    // Floor order are assembled into it rather than each writing the key and overwriting the
    // other. The two lists answer different questions: only is the admitted endpoint set, order
    // is the cache-price ranking of every priced endpoint.
    (string body, _) = await SendAsync(Model(variant: ModelVariant.CacheFloor),
        [Endpoint("alpha/zdr", cacheRead: 0.00000002m),
         Endpoint("alpha/safety", cacheRead: 0.0000005m)],
        Eligible(("alpha/zdr", false), ("alpha/safety", true))).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.Equal(["alpha/zdr"],
        provider.GetProperty("only").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal(["alpha/zdr", "alpha/safety"],
        provider.GetProperty("order").EnumerateArray().Select(e => e.GetString()));
    Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
  }

  [Fact]
  public async Task Send_SafetyRetention_IsServedByASafetyRetentionEndpoint()
  {
    // The middle tier exists to admit exactly this endpoint: a strict request refuses it, and a
    // loosened one would not have needed the list at all.
    (string body, _) = await SendAsync(Model(RetentionPolicy.SafetyRetention),
        [Endpoint("alpha/safety")], Eligible(("alpha/safety", true))).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.False(provider.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Send_ZeroDataRetentionWithOnlySafetyRetentionEndpoints_FailsNamingTheTier()
  {
    // THE RESTRICTION IS HARD. Every other routing preference in this harness degrades quietly;
    // this one is a statement about where the prompts may go, so a silent fallback to a
    // retaining endpoint would break it without ever saying so.
    DomainError error = await SendExpectingFailureAsync(Model(),
        [Endpoint("alpha/safety")], Eligible(("alpha/safety", true))).ConfigureAwait(true);

    Assert.Equal("RetentionUnavailable", error.Code);
    Assert.Contains("ZDR", error.Message, StringComparison.Ordinal);
    Assert.Contains(BaseModel, error.Message, StringComparison.Ordinal);
    Assert.Contains("Model Settings", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Send_SafetyRetentionWithNoEndpointForTheModel_FailsNamingTheTier()
  {
    // The list was consulted and names nothing for this model, which is the one fact that
    // proves no endpoint the tier admits serves it. An endpoint row with a tag is what makes the
    // list's silence about the model meaningful rather than an absence of evidence.
    DomainError error = await SendExpectingFailureAsync(Model(RetentionPolicy.SafetyRetention),
        [Endpoint("alpha/zdr")], EligibleFor("other/model", "other")).ConfigureAwait(true);

    Assert.Equal("RetentionUnavailable", error.Code);
    Assert.Contains("Safety Retention", error.Message, StringComparison.Ordinal);
    Assert.Contains(BaseModel, error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Send_ZeroDataRetentionWithNoCatalog_SendsZdrAndNoOnlyList()
  {
    // A catalog that was never wired has said nothing about the model's endpoints, so the
    // request is sent with the restriction on and the provider decides. Refusing here would fail
    // turns over an absence of evidence.
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model(), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    JsonElement block = doc.RootElement.GetProperty("provider");
    Assert.True(block.GetProperty("zdr").GetBoolean());
    Assert.False(block.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Send_AnUnconsultedList_SendsZdrAndNoOnlyList()
  {
    // Null is the catalog's word for 'not established', and it is not proof of ineligibility:
    // the turn is sent with the restriction on and no invented slug list.
    (string body, _) = await SendAsync(Model(), [Endpoint("alpha")]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement block = doc.RootElement.GetProperty("provider");
    Assert.True(block.GetProperty("zdr").GetBoolean());
    Assert.False(block.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Send_AnUnavailableCatalog_IsNotProofOfIneligibility()
  {
    // A catalog read fault says nothing about the model's endpoints, so the request is sent with
    // the restriction on and the provider decides.
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

    Result<ModelResponse> result = await provider.SendAsync(Model(), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    JsonElement block = doc.RootElement.GetProperty("provider");
    Assert.True(block.GetProperty("zdr").GetBoolean());
    Assert.False(block.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Send_ACatalogWithNoRowsForTheModel_IsNotProofOfIneligibility()
  {
    // A catalog that has not loaded this model has said nothing about its endpoints, so refusing
    // here would fail every turn for a model the catalog simply has not reached yet.
    (string body, _) = await SendAsync(Model(),
        [Endpoint("alpha/zdr") with { ModelId = "other/model" }]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement block = doc.RootElement.GetProperty("provider");
    Assert.True(block.GetProperty("zdr").GetBoolean());
    Assert.False(block.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Send_ACatalogThatNeverDescribedTheEndpoints_IsNotProofOfIneligibility()
  {
    // A row parsed from the model catalog rather than the endpoints payload says what the model
    // is, never how an endpoint retains. Reading that silence as 'retains' would fail turns for
    // every model whose endpoints were never crawled.
    (string body, _) = await SendAsync(Model(),
        [Endpoint("alpha") with { Variant = EndpointVariant.Unspecified }]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement block = doc.RootElement.GetProperty("provider");
    Assert.True(block.GetProperty("zdr").GetBoolean());
    Assert.False(block.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Send_RetentionAndTraining_IsServedWithNoEligibleEndpoint()
  {
    // The same catalog that fails an enforced tier must serve a loosened one: the check exists
    // to enforce the promise, not to gate the model.
    (string body, _) = await SendAsync(Model(RetentionPolicy.RetentionAndTraining),
        [Endpoint("alpha")], EligibleFor("other/model", "other")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(BaseModel, doc.RootElement.GetProperty("model").GetString());
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  private static async Task<(string Body, FakeCatalog Catalog)> SendAsync(ModelConfig config,
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
    FakeCatalog catalog = new(endpoints, eligible);
    OpenRouterModelProvider provider = new(http, Config, catalog: catalog);

    Result<ModelResponse> result = await provider.SendAsync(config, Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    return (captured!, catalog);
  }

  private static async Task<DomainError> SendExpectingFailureAsync(ModelConfig config,
      IReadOnlyList<ModelProviderEntry> endpoints, RetentionEligibleList? eligible)
  {
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog(endpoints, eligible));

    Result<ModelResponse> result = await provider.SendAsync(config, Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    return result.Error;
  }

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries,
      RetentionEligibleList? eligible = null) : IModelCatalog
  {
    /// <summary>How many times the eligible list was asked for, so a tier that states no
    ///     restriction can be shown to consult nothing.</summary>
    public int EligibleReads { get; private set; }

    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(entries));

    public Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
    {
      EligibleReads++;
      return Task.FromResult(eligible);
    }
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
