using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The ORDERED MODEL LIST on the wire: <c>model</c> carries the head and <c>models</c>
///     carries the whole list in the order the user stated, so the provider can fall through to a
///     backup when the head cannot serve.
///     <para><c>models</c> IS OMITTED WHEN NOTHING FOLLOWS THE HEAD. A single-model session must
///     send the body it has always sent, and an array holding one id beside <c>model</c> is a form
///     whose behaviour the provider does not document.</para>
///     <para>THE LIST IS BUILT FROM THE HEAD PLUS THE FALLBACKS, never assembled independently:
///     two sources for 'which model comes first' is how the request would come to state one order
///     in <c>model</c> and another in <c>models</c>.</para></summary>
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class ModelListWireTests
{
  private const string Head = "alpha/model";
  private const string Second = "beta/other";
  private const string Third = "gamma/third";

  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  [Fact]
  public async Task Body_AConfigWithNoFallbacks_SendsNoModelsKeyAtAll()
  {
    string body = await CaptureBodyAsync(Model()).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("models", out _));
    Assert.Equal(Head, doc.RootElement.GetProperty("model").GetString());
  }

  [Fact]
  public async Task Body_TwoFallbacks_SendsTheHeadThenEachBackupInOrder()
  {
    string body = await CaptureBodyAsync(Model([Second, Third])).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal([Head, Second, Third], ModelsOf(doc.RootElement));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  public async Task Body_ModelStaysAString_WhateverFollowsIt(int backups)
  {
    IReadOnlyList<string> fallbacks = [.. new[] { Second, Third }.Take(backups)];
    string body = await CaptureBodyAsync(Model(fallbacks)).ConfigureAwait(true);

    // A request whose model member became an array would be a different request entirely: the
    // head is still the model the turn is budgeted against and reported as running.
    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement model = doc.RootElement.GetProperty("model");
    Assert.Equal(JsonValueKind.String, model.ValueKind);
    Assert.Equal(Head, model.GetString());
  }

  [Theory]
  [InlineData(ModelVariant.Standard, "", "")]
  [InlineData(ModelVariant.Nitro, ":nitro", ":nitro")]
  [InlineData(ModelVariant.Floor, ":floor", ":floor")]
  [InlineData(ModelVariant.CacheFloor, "", "")]
  public async Task Body_TheVariantReachesEveryListEntry_ExactlyAsItReachesTheModel(
      ModelVariant variant, string headSuffix, string backupSuffix)
  {
    string body = await CaptureBodyAsync(Model([Second, Third], variant)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(Head + headSuffix, doc.RootElement.GetProperty("model").GetString());
    Assert.Equal([Head + headSuffix, Second + backupSuffix, Third + backupSuffix],
        ModelsOf(doc.RootElement));
  }

  [Fact]
  public async Task Body_TheListAlreadyCarriesItsSuffix_SoItIsNotAppliedTwice()
  {
    // The overlay writes the suffix before the config reaches the ACL. A body builder that
    // re-applied it would send 'beta/other:nitro:nitro'.
    ModelConfig config = ModelConfig.Create(ModelVariants.ApplyTo(ModelVariant.Nitro, Head), 64, 0.7f,
        4096, variant: ModelVariant.Nitro, retention: RetentionPolicy.RetentionAndTraining,
        fallbackModelIds: [ModelVariants.ApplyTo(ModelVariant.Nitro, Second)]).Value!;

    string body = await CaptureBodyAsync(config).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal([Head + ":nitro", Second + ":nitro"], ModelsOf(doc.RootElement));
  }

  [Fact]
  public async Task Body_TwoAdjacentRequests_SendTheSameListInTheSameOrder()
  {
    ModelConfig config = Model([Second, Third]);

    using JsonDocument first = JsonDocument.Parse(await CaptureBodyAsync(config).ConfigureAwait(true));
    using JsonDocument second = JsonDocument.Parse(await CaptureBodyAsync(config).ConfigureAwait(true));

    Assert.Equal(ModelsOf(first.RootElement), ModelsOf(second.RootElement));
    Assert.Equal([Head, Second, Third], ModelsOf(second.RootElement));
  }

  [Fact]
  public async Task Send_AnEnforcedTierWithNoEligibleHeadEndpoint_FailsNamingTheTier()
  {
    // The restriction is hard, and it is stated about the HEAD: a backup is not an endpoint the
    // tier has admitted, so it cannot loosen a restriction the head cannot meet.
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new HeadUnservableCatalog());

    Result<ModelResponse> result = await provider.SendAsync(
        Model([Second], retention: RetentionPolicy.ZeroDataRetention), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RetentionUnavailable", result.Error.Code);
    Assert.Contains("ZDR", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(Head, result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Send_AResponseNamingABackup_ReportsThatBackupAsServed()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Json(System.Net.HttpStatusCode.OK, ServedBy(Second));
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model([Second, Third]), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.Equal(Head, doc.RootElement.GetProperty("model").GetString());
    Assert.Equal(Second, result.Value.ServedModelId);
  }

  [Fact]
  public async Task Send_AResponseNamingNoModel_ReportsNoServedModel()
  {
    // Unreported stays unreported: the requested head is the model the turn was assembled
    // around, and the parse path never promotes it into a claim about what answered.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Ok()));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model([Second]), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Null(result.Value.ServedModelId);
  }

  [Fact]
  public async Task Send_ARefusedList_SurfacesTheProvidersErrorVerbatim_AndSendsNothingElse()
  {
    // The provider refused EVERY entry. The turn fails with the provider's own words - the
    // harness has no model left that the user listed, and inventing one is the substitution the
    // resolver exists to refuse.
    const string Refusal = "no endpoints available for any listed model";
    int sends = 0;
    FakeHttpMessageHandler handler = new(_ =>
    {
      sends++;
      return Task.FromResult(Wire.Json(System.Net.HttpStatusCode.BadRequest,
          /*lang=json,strict*/ """{"error":{"message":"no endpoints available for any listed model"}}"""));
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model([Second, Third]), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains(Refusal, result.Error.Message, StringComparison.Ordinal);
    // ONE REQUEST, UNAMBIGUOUSLY: a 400 is not retried (the identical request would fail
    // identically), so nothing else reached the provider on this turn.
    Assert.Equal(1, sends);
  }

  [Fact]
  public async Task Send_ARefusedOverLongList_IsTheProvidersOwnError_AndTheConfigIsUnchanged()
  {
    // A list the provider will not accept is refused BY THE PROVIDER, at request time:
    // the harness does not trim, reorder, or shorten it to fit, because a shorter list is a
    // different selection from the one the user stated.
    const int Limit = 20;
    IReadOnlyList<string> fallbacks = [.. Enumerable.Range(0, Limit).Select(i => $"many/model{i}")];
    ModelConfig config = Model(fallbacks);
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Json(System.Net.HttpStatusCode.BadRequest,
          /*lang=json,strict*/ """{"error":{"message":"models accepts at most 20 entries"}}""");
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(config, Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("at most 20", result.Error.Message, StringComparison.Ordinal);
    // THE REQUEST NAMED EVERY ENTRY THE USER STATED: the refusal came from what was sent, not
    // from a request the harness had already reduced.
    using JsonDocument sent = JsonDocument.Parse(captured!);
    Assert.Equal(Limit + 1, ModelsOf(sent.RootElement).Count);
    Assert.Equal(fallbacks, config.FallbackModelIds);
  }

  private static string ServedBy(string modelId)
      => /*lang=json,strict*/ $$"""
          {"model":"{{modelId}}","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}
          """;

  private static IReadOnlyList<string> ModelsOf(JsonElement root)
      => [.. root.GetProperty("models").EnumerateArray().Select(e => e.GetString()!)];

  // NO RESTRICTION STATED: these tests are about the list, so the retention tier must not add a
  // provider object of its own to the body they inspect.
  private static ModelConfig Model(IReadOnlyList<string>? fallbacks = null,
      ModelVariant variant = ModelVariant.Standard,
      RetentionPolicy retention = RetentionPolicy.RetentionAndTraining)
      => ModelConfig.Create(ModelVariants.ApplyTo(variant, Head), 64, 0.7f, 4096,
          variant: variant, retention: retention,
          fallbackModelIds: fallbacks is { Count: > 0 }
              ? [.. fallbacks.Select(id => ModelVariants.ApplyTo(variant, id))]
              : null).Value!;

  private static ModelRequest Request()
      => new([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]);

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

    Result<ModelResponse> result = await provider.SendAsync(config, Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    return captured!;
  }

  /// <summary>A catalog that describes the head's endpoints and an eligible list admitting none of
  ///     them: the one fact that proves the stated tier cannot be met for the head.</summary>
  private sealed class HeadUnservableCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [new ModelProviderEntry(Head, "Provider", 0.0000001m, null, 0.0000005m, 128_000, 8_192,
                SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
                IntelligenceScore: null, CodingScore: null, AgenticScore: null,
                LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
            {
              Variant = EndpointVariant.Parse("alpha/model", null, null),
            }]));

    public Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
        => Task.FromResult<RetentionEligibleList?>(RetentionEligibleList.Of(
            [new RetentionEligibleEndpoint("other/model", "Provider", "other", false)]));
  }
}
#pragma warning restore CA2000
