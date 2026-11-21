using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>EVERY REQUEST NAMES EXACTLY ONE MODEL. The provider has no multi-model surface left:
///     <c>model</c> carries one id as a string and no <c>models</c> array is ever sent.
///     <para>FALL-THROUGH BELONGS TO THE HARNESS. A turn walks the session's list one model at a
///     time, so a request that named several would hide a refusal the walk has to see in order to
///     drop that model and continue.</para>
///     <para>THE ROUTING VARIANT STILL REACHES THE ID, because it is part of the id's wire form.
///     </para></summary>
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class SingleModelRequestTests
{
  private const string Model = "alpha/model";
  private const string Other = "beta/other";

  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  [Fact]
  public async Task Body_NamesExactlyOneModel_AndCarriesNoModelsKey()
  {
    string body = await CaptureBodyAsync(ConfigFor(Model)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(Model, doc.RootElement.GetProperty("model").GetString());
    Assert.False(doc.RootElement.TryGetProperty("models", out _));
  }

  [Theory]
  [InlineData(ModelVariant.Standard, "")]
  [InlineData(ModelVariant.Nitro, ":nitro")]
  [InlineData(ModelVariant.Floor, ":floor")]
  [InlineData(ModelVariant.CacheFloor, "")]
  public async Task Body_TheVariantReachesTheModelId_AndNothingElseNamesAModel(
      ModelVariant variant, string suffix)
  {
    string body = await CaptureBodyAsync(ConfigFor(ModelVariants.ApplyTo(variant, Model), variant))
        .ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(Model + suffix, doc.RootElement.GetProperty("model").GetString());
    Assert.False(doc.RootElement.TryGetProperty("models", out _));
  }

  [Fact]
  public async Task Body_TheModelStaysAString()
  {
    // A request whose model member became an array would be a different request entirely: the
    // head is the model the turn is budgeted against and reports as running.
    string body = await CaptureBodyAsync(ConfigFor(Model)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("model").ValueKind);
  }

  [Fact]
  public async Task Body_TwoAdjacentRequests_NameTheSameSingleModel()
  {
    ModelConfig config = ConfigFor(Model);

    using JsonDocument first = JsonDocument.Parse(await CaptureBodyAsync(config).ConfigureAwait(true));
    using JsonDocument second = JsonDocument.Parse(await CaptureBodyAsync(config).ConfigureAwait(true));

    Assert.Equal(first.RootElement.GetProperty("model").GetString(),
        second.RootElement.GetProperty("model").GetString());
    Assert.False(second.RootElement.TryGetProperty("models", out _));
  }

  [Fact]
  public async Task Body_ASelectionOfThree_SendsTheHeadAndNoList()
  {
    // The list a session holds is the walk's, not the request's: the turn attempts the head and
    // the remaining entries are attempted by later requests if the head cannot serve.
    ModelSelection selection = ModelSelection.Create([Model, Other, "gamma/third"]).Value!;

    string body = await CaptureBodyAsync(ConfigFor(selection.Head)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(Model, doc.RootElement.GetProperty("model").GetString());
    Assert.False(doc.RootElement.TryGetProperty("models", out _));
    Assert.DoesNotContain(Other, body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Send_AnEnforcedTierWithNoEligibleHeadEndpoint_FailsNamingTheTier()
  {
    // The restriction is hard, and it is stated about the ONE model the request names.
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new HeadUnservableCatalog());

    Result<ModelResponse> result = await provider.SendAsync(
        ConfigFor(Model, retention: RetentionPolicy.ZeroDataRetention), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RetentionUnavailable", result.Error.Code);
    Assert.Contains("ZDR", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(Model, result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Send_AResponseNamingAModel_ReportsThatModelAsServed()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is { } content
          ? await content.ReadAsStringAsync().ConfigureAwait(false)
          : null;
      return Wire.Json(System.Net.HttpStatusCode.OK, ServedBy(Other));
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(ConfigFor(Model), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.Equal(Model, doc.RootElement.GetProperty("model").GetString());
    Assert.Equal(Other, result.Value.ServedModelId);
  }

  [Fact]
  public async Task Send_AResponseNamingNoModel_ReportsNoServedModel()
  {
    // Unreported stays unreported: the requested model is the one the turn was assembled around,
    // and the parse path never promotes it into a claim about what answered.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Ok()));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(ConfigFor(Model), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Null(result.Value.ServedModelId);
  }

  [Fact]
  public async Task Send_ARefusedModel_SurfacesTheProvidersErrorVerbatim_AndSendsNothingElse()
  {
    // The provider refused the model. The turn fails with the provider's own words - the harness
    // has no model left that the user listed, and inventing one is the substitution the resolver
    // exists to refuse.
    const string Refusal = "no endpoints available for this model";
    int sends = 0;
    FakeHttpMessageHandler handler = new(_ =>
    {
      sends++;
      return Task.FromResult(Wire.Json(System.Net.HttpStatusCode.BadRequest,
          /*lang=json,strict*/ """{"error":{"message":"no endpoints available for this model"}}"""));
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(ConfigFor(Model), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ModelRejected", result.Error.Code);
    Assert.Contains(Refusal, result.Error.Message, StringComparison.Ordinal);
    // ONE REQUEST, UNAMBIGUOUSLY: a 400 is not retried (the identical request would fail
    // identically), so nothing else reached the provider on this turn.
    Assert.Equal(1, sends);
  }

  private static string ServedBy(string modelId)
      => /*lang=json,strict*/ $$"""
          {"model":"{{modelId}}","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}
          """;

  // NO RESTRICTION STATED: these tests are about the model, so the retention tier must not add a
  // provider object of its own to the body they inspect.
  private static ModelConfig ConfigFor(string modelId,
      ModelVariant variant = ModelVariant.Standard,
      RetentionPolicy retention = RetentionPolicy.RetentionAndTraining)
      => ModelConfig.Create(modelId, 64, 0.7f, 4096, variant: variant, retention: retention).Value!;

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

  /// <summary>A catalog that describes the model's endpoints and an eligible list admitting none of
  ///     them: the one fact that proves the stated tier cannot be met for the model.</summary>
  private sealed class HeadUnservableCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [new ModelProviderEntry(Model, "Provider", 0.0000001m, null, 0.0000005m, 128_000, 8_192,
                SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
                IntelligenceScore: null, CodingScore: null, AgenticScore: null,
                LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
            {
              Variant = EndpointVariant.Parse(Model, null, null),
            }]));

    public Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
        => Task.FromResult<RetentionEligibleList?>(RetentionEligibleList.Of(
            [new RetentionEligibleEndpoint("other/model", "Provider", "other", false)]));
  }
}
#pragma warning restore CA2000
