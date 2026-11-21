using System.Net;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>The Cloudflare model catalog: one entry per chat model, every fact read from the
///     response, and a failed read that is a named failure rather than an empty catalog.
///     <para>EVERY ASSERTION HERE IS OBSERVABLE FROM THE CATALOG'S PUBLIC SURFACE with a fake
///     HTTP handler standing in for Cloudflare, so no test needs a credential or a network.
///     </para></summary>
public class CloudflareModelCatalogTests
{
  private const string AccountId = "acct-123";
  private const string ApiToken = "token-abc";

  private static CloudflareConfiguration Configuration() =>
      new(AccountId, ApiToken);

  private static CloudflareModelCatalog Catalog(FakeHttpMessageHandler handler) =>
      new(new HttpClient(handler), Configuration());

  private static string Response(params string[] models)
      => $$"""{"success":true,"errors":[],"messages":[],"result":[{{string.Join(",", models)}}]}""";

  private static string ChatModel(
      string name,
      string properties = "",
      string task = "Text Generation",
      string description = "a model")
      => $$"""{"name":"{{name}}","description":"{{description}}","task":{"id":"t","name":"{{task}}"},"properties":[{{properties}}]}""";

  private static string Property(string id, string value)
      => $$"""{"property_id":"{{id}}","value":{{value}}}""";

  // JSON002 fires only in the format/IDE host, which cannot see that these literals are wire
  // fixtures under test - the pragma pair is the repo's named decision for hand-written shapes.
#pragma warning disable JSON002 // Probable JSON string detected
  private const string ToolCapable = """{"property_id":"function_calling","value":"true"}""";
  private const string ContextWindow = """{"property_id":"context_window","value":"8192"}""";
  private const string PaidPlan = """{"property_id":"require_workers_paid","value":"true"}""";

  private const string Priced =
      """{"property_id":"price","value":[{"currency":"USD","price":1.5,"unit":"per M input tokens"},{"currency":"USD","price":4.5,"unit":"per M output tokens"}]}""";
#pragma warning restore JSON002 // Probable JSON string detected

  // ── what the catalog yields ──────────────────────────────────────────────

  [Fact]
  public async Task AChatModelThatIsToolCapableAndPriced_YieldsOneEntryWithBothPrices()
  {
    // The image model is listed beside the chat model and must not contribute an entry.
    string body = Response(
        ChatModel("@cf/meta/llama-3.1-8b-instruct",
            $"{ToolCapable},{ContextWindow},{Priced}"),
        ChatModel("@cf/black-forest-labs/flux-1-schnell", task: "Text-to-Image"));

    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(body)).GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    ModelProviderEntry entry = Assert.Single(read.Value);
    Assert.Equal("@cf/meta/llama-3.1-8b-instruct", entry.ModelId);
    Assert.Equal(ModelVendors.Cloudflare, entry.ProviderName);
    Assert.True(entry.SupportsToolUse);
    Assert.True(entry.SupportsTextOutput);
    Assert.Equal(8192, entry.ContextLength);
    Assert.Equal(0.0000015m, entry.PromptPricePerToken);
    Assert.Equal(0.0000045m, entry.CompletionPricePerToken);
  }

  [Theory]
  [InlineData("Text-to-Image")]
  [InlineData("Text Embeddings")]
  [InlineData("Automatic Speech Recognition")]
  [InlineData("Text-to-Speech")]
  [InlineData("Translation")]
  [InlineData("Reranking")]
  [InlineData("Image Classification")]
  public async Task ANonChatTask_ContributesNoEntry(string task)
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b", task: task))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Empty(read.Value);
  }

  [Fact]
  public async Task AResponseSayingNothingAboutToolCalling_YieldsSupportsToolUseFalse()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b"))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.False(Assert.Single(read.Value).SupportsToolUse);
  }

  [Fact]
  public async Task AResponseSayingNothingAboutPricing_YieldsNullPricesAndIsNotAdvertisedFree()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b"))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    ModelProviderEntry entry = Assert.Single(read.Value);
    Assert.Null(entry.PromptPricePerToken);
    Assert.Null(entry.CompletionPricePerToken);
    Assert.False(entry.IsAdvertisedFree);
  }

  [Fact]
  public async Task AResponseSayingNothingAboutAContextWindow_YieldsZero()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b"))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Equal(0, Assert.Single(read.Value).ContextLength);
  }

  [Fact]
  public async Task AModelMarkedAsNeedingAPaidPlan_IsReturnedAndMarked()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b", PaidPlan))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.True(Assert.Single(read.Value).RequiresPaidPlan);
  }

  [Fact]
  public async Task AModelSayingNothingAboutAPlan_CarriesNoMarker()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b"))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Null(Assert.Single(read.Value).RequiresPaidPlan);
  }

  [Fact]
  public async Task AModelMarkedAsNotNeedingAPaidPlan_ReadsFalse()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(
            Response(ChatModel("@cf/a/b", Property("require_workers_paid", "false")))))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.False(Assert.Single(read.Value).RequiresPaidPlan);
  }

  [Fact]
  public async Task ASuccessfulReadReturningNoModels_IsSuccessWithAnEmptyCatalog()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(Response())).GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Empty(read.Value);
  }

  // ── failures ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task ARefusedCredential_IsANamedFailureCarryingCloudflaresOwnText()
  {
    const string body =
        /*lang=json,strict*/
        """{"success":false,"errors":[{"code":10000,"message":"Authentication error: invalid token"}],"result":null}""";

    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(body, HttpStatusCode.Forbidden))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.False(read.IsSuccess);
    Assert.Equal("ProviderUnauthorized", read.Error.Code);
    Assert.Contains("invalid token", read.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnUnauthorizedStatus_IsTheSameNamedFailure()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json("", HttpStatusCode.Unauthorized))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.False(read.IsSuccess);
    Assert.Equal("ProviderUnauthorized", read.Error.Code);
  }

  [Fact]
  public async Task ARefusedCredential_IsDistinguishableFromANetworkFailure()
  {
    FakeHttpMessageHandler network = new(_ => throw new HttpRequestException("no route to host"));

    Result<IReadOnlyList<ModelProviderEntry>> refused =
        await Catalog(FakeHttpMessageHandler.Json("", HttpStatusCode.Forbidden))
            .GetAsync(TestContext.Current.CancellationToken);
    Result<IReadOnlyList<ModelProviderEntry>> unreachable =
        await Catalog(network).GetAsync(TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.False(unreachable.IsSuccess);
    Assert.NotEqual(refused.Error.Code, unreachable.Error.Code);
    Assert.Contains("could not be reached", unreachable.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AServerFault_IsANamedFailureAndNotAnEmptyCatalog()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.False(read.IsSuccess);
    Assert.Equal("CatalogUnavailable", read.Error.Code);
  }

  [Fact]
  public async Task AResponseThatIsNotTheDocumentedEnvelope_IsANamedFailure()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(/*lang=json,strict*/ """{"nope":true}"""))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.False(read.IsSuccess);
    Assert.Equal("CatalogParseError", read.Error.Code);
  }

  [Fact]
  public async Task AResponseThatIsNotJson_IsANamedFailure()
  {
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json("not json at all"))
            .GetAsync(TestContext.Current.CancellationToken);

    Assert.False(read.IsSuccess);
    Assert.Equal("CatalogParseError", read.Error.Code);
  }

  // ── the request and the cache ────────────────────────────────────────────

  [Fact]
  public async Task TheRead_GoesToTheAccountScopedSearchPathOnTheApiHost()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(Response());

    _ = await Catalog(handler).GetAsync(TestContext.Current.CancellationToken);

    HttpRequestMessage request = Assert.Single(handler.Requests);
    Assert.Equal(
        $"https://api.cloudflare.com/accounts/{AccountId}/ai/models/search",
        request.RequestUri!.AbsoluteUri);
    Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
    Assert.Equal(ApiToken, request.Headers.Authorization.Parameter);
  }

  [Fact]
  public async Task TwoReadsInsideTheFreshnessWindow_MakeOneHttpCall()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(Response(ChatModel("@cf/a/b")));
    CloudflareModelCatalog catalog = Catalog(handler);

    _ = await catalog.GetAsync(TestContext.Current.CancellationToken);
    _ = await catalog.GetAsync(TestContext.Current.CancellationToken);

    Assert.Equal(1, handler.CallCount);
  }

  [Fact]
  public async Task EveryEntryNamesTheCloudflareVendor()
  {
    string body = Response(ChatModel("@cf/a/b"), ChatModel("@cf/c/d"));

    Result<IReadOnlyList<ModelProviderEntry>> read =
        await Catalog(FakeHttpMessageHandler.Json(body)).GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.All(read.Value, entry => Assert.Equal(ModelVendors.Cloudflare, entry.ProviderName));
  }
}
