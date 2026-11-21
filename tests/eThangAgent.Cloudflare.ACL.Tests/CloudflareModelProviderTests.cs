using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it
// JSON002 fires only in the format/IDE host, which cannot see that these literals are wire
// fixtures under test - the pragma pair is the repo's named decision for hand-written shapes.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>The Cloudflare chat provider: one model per request, Cloudflare's own dialect, and
///     every failure a named error carrying Cloudflare's text.
///     <para>EVERY ASSERTION HERE IS OBSERVABLE AGAINST A FAKE HTTP HANDLER with no network and
///     no credential.</para></summary>
public class CloudflareModelProviderTests
{
  private const string AccountId = "acct-123";
  private const string ApiToken = "token-abc";
  private const string ModelId = "@cf/meta/llama-3.1-8b-instruct";

  private static CloudflareConfiguration Configuration() => new(AccountId, ApiToken);

  private static CloudflareModelProvider Provider(FakeHttpMessageHandler handler)
      => new(new HttpClient(handler), Configuration());

  private static ModelConfig Model(bool acceptsImageInput = false,
      IReadOnlyList<string>? fallbackModelIds = null,
      ModelVariant variant = ModelVariant.Standard,
      RetentionPolicy retention = RetentionPolicy.ZeroDataRetention)
      => ModelConfig.Create(ModelId, 256, 0.7f, 4096,
          acceptsImageInput: acceptsImageInput,
          variant: variant,
          retention: retention,
          fallbackModelIds: fallbackModelIds).Value!;

  private static string Result(string payload)
      => $$"""{"success":true,"errors":[],"messages":[],"result":{{payload}}}""";

  // JSON002 fires only in the format/IDE host, which cannot see that these literals are wire
  // fixtures under test - the pragma pair is the repo's named decision for hand-written shapes.
#pragma warning disable JSON002 // Probable JSON string detected
  private static readonly string PlainResult = Result("{\"response\":\"hello\"}");
#pragma warning restore JSON002 // Probable JSON string detected

  private static JsonElement Body(FakeHttpMessageHandler handler)
      => JsonElement.Parse(handler.Body);

  // ── one model, Cloudflare's dialect ──────────────────────────────────────

  [Fact]
  public async Task ARequest_NamesExactlyOneModel_InThePath()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);

    Result<ModelResponse> sent = await Provider(handler)
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    HttpRequestMessage request = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Post, request.Method);
    Assert.Equal(
        $"https://api.cloudflare.com/accounts/{AccountId}/ai/run/{ModelId}",
        request.RequestUri!.AbsoluteUri);
    Assert.Equal("Bearer " + ApiToken, request.Headers.Authorization!.ToString());
  }

  [Fact]
  public async Task AConfigWithAFallbackList_SendsOnlyTheModelItNames()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(
        Model(fallbackModelIds: ["@cf/meta/llama-3.3-70b-instruct-fp8-fast"]),
        new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    string body = handler.Body;
    Assert.DoesNotContain("models", body, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("llama-3.3-70b", body, StringComparison.Ordinal);
    Assert.DoesNotContain(ModelId, body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ANonStandardVariant_SendsNoVariantSuffixAndNoProviderBlock()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(
        Model(variant: ModelVariant.Nitro, retention: RetentionPolicy.ZeroDataRetention),
        new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    Assert.DoesNotContain(":nitro", handler.Requests[0].RequestUri!.AbsoluteUri,
        StringComparison.Ordinal);
    string body = handler.Body;
    Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("zdr", body, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task TheSystemPrompt_RidesAsASystemMessage()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(Model(),
        new ModelRequest([User("hi")], SystemPrompt: "be brief"),
        TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    JsonElement messages = Body(handler).GetProperty("messages");
    Assert.Equal(2, messages.GetArrayLength());
    Assert.Equal("system", messages[0].GetProperty("role").GetString());
    Assert.Equal("be brief", messages[0].GetProperty("content").GetString());
    Assert.Equal("user", messages[1].GetProperty("role").GetString());
    Assert.Equal("hi", messages[1].GetProperty("content").GetString());
  }

  [Fact]
  public async Task TheToolsArray_CarriesFlatFunctionEntries()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);
    ToolDefinition tool = new("read", "Read a file.",
        [new ToolParameter("path", ToolParameterType.Text, "The path.")]);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(Model(),
        new ModelRequest([User("hi")], Tools: [tool]),
        TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    JsonElement entry = Assert.Single(Body(handler).GetProperty("tools").EnumerateArray());
    Assert.Equal("function", entry.GetProperty("type").GetString());
    Assert.Equal("read", entry.GetProperty("name").GetString());
    JsonElement parameters = entry.GetProperty("parameters");
    Assert.Equal("object", parameters.GetProperty("type").GetString());
    Assert.Equal("string",
        parameters.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
    Assert.Equal("path", Assert.Single(parameters.GetProperty("required").EnumerateArray()).GetString());
  }

  // ── what a response yields ──────────────────────────────────────────────

  [Fact]
  public async Task AResponse_ReturnsItsText()
  {
    Result<ModelResponse> sent = await Provider(FakeHttpMessageHandler.Json(PlainResult))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    Assert.Equal("hello", sent.Value.Content);
    Assert.Empty(sent.Value.ToolCalls);
    Assert.Equal(FinishReason.Stop, sent.Value.FinishReason);
  }

  [Fact]
  public async Task AToolCall_ComesBackWithItsArgumentsIntact()
  {
    string body = Result(
        "{\"response\":\"\",\"tool_calls\":[{\"name\":\"read\","
        + "\"arguments\":{\"path\":\"a.txt\"}}]}");

    Result<ModelResponse> sent = await Provider(FakeHttpMessageHandler.Json(body))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    ToolCallRequest call = Assert.Single(sent.Value.ToolCalls);
    Assert.Equal("read", call.Name);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"a.txt\"}", call.Arguments);
    Assert.Equal(FinishReason.ToolCalls, sent.Value.FinishReason);
  }

  [Fact]
  public async Task AToolCallWithAnEmptyObject_ComesBackAsAnEmptyObject()
  {
    string body = Result(
        /*lang=json,strict*/ "{\"response\":\"\",\"tool_calls\":[{\"name\":\"status\",\"arguments\":{}}]}");

    Result<ModelResponse> sent = await Provider(FakeHttpMessageHandler.Json(body))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    ToolCallRequest call = Assert.Single(sent.Value.ToolCalls);
    Assert.Equal("status", call.Name);
    Assert.Equal("{}", call.Arguments);
  }

  [Fact]
  public async Task AResponseReportingBothTokenCounts_ReturnsBothOnTheUsage()
  {
    string body = Result(
        /*lang=json,strict*/ "{\"response\":\"hi\",\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7}}");

    Result<ModelResponse> sent = await Provider(FakeHttpMessageHandler.Json(body))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    TokenUsage usage = Assert.IsType<TokenUsage>(sent.Value.Usage);
    Assert.Equal(11, usage.InputTokens);
    Assert.Equal(7, usage.OutputTokens);
    Assert.Null(usage.ReportedCost);
  }

  [Fact]
  public async Task AResponseReportingNoUsage_LeavesUsageAbsent()
  {
    Result<ModelResponse> sent = await Provider(FakeHttpMessageHandler.Json(PlainResult))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    Assert.Null(sent.Value.Usage);
  }

  [Fact]
  public async Task AResponseBodyThatCannotBeParsed_IsANamedFailure()
  {
    Result<ModelResponse> sent = await Provider(FakeHttpMessageHandler.Json("not json at all"))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderError", sent.Error.Code);
  }

  [Fact]
  public async Task AResponseCarryingNoResult_IsANamedFailure()
  {
    Result<ModelResponse> sent = await Provider(
        FakeHttpMessageHandler.Json(/*lang=json,strict*/ "{\"success\":true}"))
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderError", sent.Error.Code);
  }

  // ── images ──────────────────────────────────────────────────────────────

  [Fact]
  public async Task AnImageBearingRequest_WithAcceptsImageInput_SendsTheImage()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(
        Model(acceptsImageInput: true), new ModelRequest([Image("describe this")]),
        TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    JsonElement content = Body(handler).GetProperty("messages")[0].GetProperty("content");
    JsonElement image = Assert.Single(content.EnumerateArray(),
        part => part.GetProperty("type").GetString() == "image_url");
    Assert.StartsWith("data:image/png;base64,",
        image.GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnImageBearingRequest_WithoutAcceptsImageInput_IsANamedFailureAndMakesNoCall()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(
        Model(), new ModelRequest([Image("describe this")]),
        TestContext.Current.CancellationToken);

    Assert.False(sent.IsSuccess);
    Assert.Equal("ImageInputUnsupported", sent.Error.Code);
    Assert.Contains(ModelId, sent.Error.Message, StringComparison.Ordinal);
    Assert.Equal(0, handler.CallCount);
  }

  [Fact]
  public async Task AnImageFromAnEarlierTurn_IsNotCarried_SoNoImageSupportIsRequired()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);
    Message picture = Image("look");
    ModelRequest request = new([picture, User("and now?")]);

    Result<ModelResponse> sent = await Provider(handler).SendAsync(Model(), request,
        TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    Assert.DoesNotContain("image_url", handler.Body, StringComparison.Ordinal);
  }

  // ── the factory ─────────────────────────────────────────────────────────

  [Fact]
  public async Task TheFactory_CreatesAProviderThatCallsTheConfiguredAccount()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(PlainResult);
    CloudflareModelProviderFactory factory = new(Configuration(), new HttpClient(handler));

    Result<ModelResponse> sent = await factory.Create(Model())
        .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

    Assert.True(sent.IsSuccess, sent.Error?.Message);
    Assert.Equal(1, handler.CallCount);
  }

  // ── fixtures ────────────────────────────────────────────────────────────

  private static Message User(string text)
      => new(Role.User, text, DateTimeOffset.UnixEpoch);

  private static Message Image(string text)
      => new(Role.User, text, DateTimeOffset.UnixEpoch, Parts:
          [new MessagePart.ImagePart("image/png", OnePixelPngBase64)]);

#pragma warning restore JSON002 // Probable JSON string detected

  /// <summary>A one-pixel PNG: the smallest payload the image part admits.</summary>
  private const string OnePixelPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
}
