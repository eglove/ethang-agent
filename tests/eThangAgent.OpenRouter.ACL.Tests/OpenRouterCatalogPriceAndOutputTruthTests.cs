using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>Two catalog facts the live-model selector (issue #289) depends on, both of
///     which the parser used to destroy before any consumer could see them:
///     <list type="number">
///     <item>PRICE ABSENCE READ AS FREE. <c>ParsePricing</c> returned 0 for a missing
///     pricing block and <c>ParseDecimal</c> returned 0 for anything unparseable, so an
///     UNPRICED model was indistinguishable from a genuinely free one - and
///     <see cref="ModelProviderEntry"/> held non-nullable prices, so the distinction
///     was gone before a consumer could make it. Measured on the live catalog: 20 rows
///     serve zero prices and 3 of them are not <c>:free</c> (two music generators and
///     the free router).</item>
///     <item>OUTPUT MODALITY WAS NEVER PARSED. <c>output_modalities</c> appeared zero
///     times in the codebase; only input modalities were read. "Text output" was not
///     expressible, so a caller could not tell a chat model from a music generator.</item>
///     </list>
///     <para>The rule every test here pins: an ABSENT or unparseable price stays NULL
///     (unknown is not free), while a SERVED zero stays a real price of zero.</para></summary>
public class OpenRouterCatalogPriceAndOutputTruthTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  // One fixture carrying every branch: an explicitly free model, a model with NO
  // pricing block at all, a model whose prices are unparseable text, and a paid model.
  private const string ModelsJson =
      /*lang=json,strict*/
      """
      {"data":[
      {"id":"free/model","context_length":262144,"pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":262144,"max_completion_tokens":8192}},
      {"id":"unpriced/model","context_length":200000,"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":200000,"max_completion_tokens":8192}},
      {"id":"garbled/model","context_length":200000,"pricing":{"prompt":"not-a-price","completion":""},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":200000,"max_completion_tokens":8192}},
      {"id":"paid/model","context_length":131072,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":131072,"max_completion_tokens":4096}},
      {"id":"music/model","context_length":1048576,"pricing":{"prompt":"0","completion":"0"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text","audio"]},"supported_parameters":["max_tokens"],"top_provider":{"context_length":1048576,"max_completion_tokens":65536}},
      {"id":"router/pseudo","context_length":200000,"pricing":{"prompt":"0","completion":"0"},"architecture":{"tokenizer":"Router","input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":200000}},
      {"id":"noarch/modalityless","context_length":8192,"pricing":{"prompt":"0","completion":"0"},"supported_parameters":["tools"],"top_provider":{"context_length":8192,"max_completion_tokens":2048}}
      ]}
      """;

  private const string EndpointsJson =
      /*lang=json,strict*/
      """
      [{"provider_name":"Vendor","context_length":262144,"max_completion_tokens":8192,"pricing":{"prompt":"0","completion":"0"}}]
      """;

  private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
      new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Handler(HttpRequestMessage req) =>
      req.RequestUri!.AbsolutePath switch
      {
        "/api/v1/models" => JsonResponse(HttpStatusCode.OK, ModelsJson),
        _ => req.RequestUri.AbsolutePath.EndsWith("/endpoints", StringComparison.Ordinal)
            ? JsonResponse(HttpStatusCode.OK, EndpointsJson)
            : new HttpResponseMessage(HttpStatusCode.NotFound)
      };

  private static async Task<IReadOnlyList<ModelProviderEntry>> FetchAsync()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);
    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    return result.Value;
  }

  private static ModelProviderEntry One(IReadOnlyList<ModelProviderEntry> entries, string modelId)
      => Assert.Single(entries, e => e.ModelId == modelId);

  [Fact]
  public async Task GetAsync_ServedZeroPrices_StayRealPricesOfZero()
  {
    ModelProviderEntry free = One(await FetchAsync(), "free/model");

    Assert.Equal(0m, free.PromptPricePerToken);
    Assert.Equal(0m, free.CompletionPricePerToken);
  }

  [Fact]
  public async Task GetAsync_NoPricingBlock_ResolvesNullPricesNeverZero()
  {
    // THE DEFECT: an unpriced model used to resolve (0, null, 0) - identical to a
    // genuinely free model. Absence is unknown, and unknown must not read as free.
    ModelProviderEntry unpriced = One(await FetchAsync(), "unpriced/model");

    Assert.Null(unpriced.PromptPricePerToken);
    Assert.Null(unpriced.CompletionPricePerToken);
  }

  [Fact]
  public async Task GetAsync_UnparseablePriceText_ResolvesNullPricesNeverZero()
  {
    // An unparseable price is unparseable, not free: ParseDecimal used to return 0 for
    // anything it could not read, which is the same lie in a second place.
    ModelProviderEntry garbled = One(await FetchAsync(), "garbled/model");

    Assert.Null(garbled.PromptPricePerToken);
    Assert.Null(garbled.CompletionPricePerToken);
  }

  [Fact]
  public async Task GetAsync_PaidModel_KeepsItsPrices()
  {
    ModelProviderEntry paid = One(await FetchAsync(), "paid/model");

    Assert.Equal(0.000001m, paid.PromptPricePerToken);
    Assert.Equal(0.000002m, paid.CompletionPricePerToken);
  }

  [Fact]
  public async Task GetAsync_TextInOutputModalities_ResolvesSupportsTextOutputTrue()
  {
    ModelProviderEntry paid = One(await FetchAsync(), "paid/model");

    Assert.True(paid.SupportsTextOutput);
  }

  [Fact]
  public async Task GetAsync_AudioAlongsideText_StillProducesText()
  {
    // A music generator advertises text+audio output. It DOES produce text, so the
    // modality flag is honestly true; what rejects it for the live suite is the missing
    // tool capability (see BestFreeModelSelector tests), never a misread modality.
    ModelProviderEntry music = One(await FetchAsync(), "music/model");

    Assert.True(music.SupportsTextOutput);
    Assert.False(music.SupportsToolUse);
  }

  [Fact]
  public async Task GetAsync_RouterTokenizer_MarksTheRowAsARouter()
  {
    // openrouter/free and openrouter/auto pick an upstream PER REQUEST. Measured on the
    // live catalog they are identifiable by the architecture's tokenizer reading "Router",
    // which also covers the ~-prefixed "latest" aliases that route without the
    // openrouter/ prefix - an id-prefix test cannot express that.
    ModelProviderEntry router = One(await FetchAsync(), "router/pseudo");

    Assert.True(router.IsRouter);
    Assert.True(router.SupportsTextOutput);
  }

  [Fact]
  public async Task GetAsync_OrdinaryTokenizer_IsNotARouter()
  {
    ModelProviderEntry ordinary = One(await FetchAsync(), "paid/model");

    Assert.False(ordinary.IsRouter);
  }

  [Fact]
  public async Task GetAsync_NoArchitectureBlock_ResolvesSupportsTextOutputFalse()
  {
    // Absence is an answer: a model that never declared its output modality has not
    // declared text, and the selector must not assume.
    ModelProviderEntry noarch = One(await FetchAsync(), "noarch/modalityless");

    Assert.False(noarch.SupportsTextOutput);
  }
}
