using System.Net;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>A context-window overflow arrives as an ordinary HTTP 400 whose body names
///     the fault (OpenRouter forwards the upstream provider's error JSON). The ACL maps
///     those bodies to the distinct ContextWindowExceeded code so the agent loop — not
///     the wire layer — decides what an overflow means. Retryable stays false: the
///     identical request would fail identically, so the wire layer must not retry.</summary>
public class ContextWindowOverflowClassificationTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  private static OpenRouterModelProvider Provider(Func<HttpResponseMessage> respond)
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(respond()));
    return new OpenRouterModelProvider(new HttpClient(handler), new OpenRouterConfiguration("test-key", BaseUrl));
  }

  private static HttpResponseMessage BadRequest(string body) => Wire.Json(HttpStatusCode.BadRequest, body);

  /// <summary>Real overflow bodies observed across providers OpenRouter forwards:
  ///     OpenAI ("maximum context length"), Anthropic ("prompt is too long"), Gemini
  ///     ("input token count ... exceeds the maximum"), and LiteLLM's canonical overflow
  ///     list ("context_length_exceeded", "reduce the length of the messages",
  ///     "input length and max_tokens exceed context limit").</summary>
  public static TheoryData<string> OverflowBodies
  {
    get
    {
      TheoryData<string> bodies = [];
      bodies.Add(/*lang=json,strict*/ """{"error":{"message":"This model's maximum context length is 131072 tokens. However, you requested 150000 tokens (140000 in the messages, 10000 in the completion). Please reduce the length of the messages or completion."}}""");
      bodies.Add(/*lang=json,strict*/ """{"error":{"code":"context_length_exceeded","message":"This model's maximum context length is 131072 tokens."}}""");
      bodies.Add(/*lang=json,strict*/ """{"error":{"message":"prompt is too long: 250000 tokens > 200000 maximum"}}""");
      bodies.Add(/*lang=json,strict*/ """{"error":{"message":"The input token count (120000) exceeds the maximum token count (100000)"}}""");
      bodies.Add(/*lang=json,strict*/ """{"error":{"message":"Input length and max_tokens exceed context limit. 100000 + 4096 > 100000"}}""");
      bodies.Add(/*lang=json,strict*/ """{"error":{"message":"Please reduce the length of the messages."}}""");
      return bodies;
    }
  }

  [Theory]
  [MemberData(nameof(OverflowBodies))]
  public async Task Overflow400_MapsToContextWindowExceeded(string body)
  {
    OpenRouterModelProvider provider = Provider(() => BadRequest(body));

    Result<ModelResponse> result = await provider.SendAsync(
        Model, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ContextWindowExceeded", result.Error.Code);
    Assert.Contains("OpenRouter returned HTTP 400:", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(body, result.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"error":{"message":"Model not allowed"}}""")]
  [InlineData(/*lang=json,strict*/ """{"error":{"message":"Invalid max_output_tokens value"}}""")]
  [InlineData(/*lang=json,strict*/ """{"error":{"message":"The requested model returned a maximum completion length error"}}""")]
  public async Task Other400_KeepsProviderError(string body)
  {
    OpenRouterModelProvider provider = Provider(() => BadRequest(body));

    Result<ModelResponse> result = await provider.SendAsync(
        Model, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
  }

  [Theory]
  [InlineData(500)]
  [InlineData(502)]
  [InlineData(503)]
  public async Task ServerErrors_KeepProviderError(int status)
  {
    OpenRouterModelProvider provider = Provider(() => Wire.Json((HttpStatusCode)status,
        /*lang=json,strict*/ """{"error":{"message":"This model's maximum context length is 131072 tokens."}}"""));

    Result<ModelResponse> result = await provider.SendAsync(
        Model, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
  }

  [Fact]
  public async Task RateLimit429_KeepsRateLimitedClassification()
  {
    OpenRouterModelProvider provider = Provider(() => Wire.Json(HttpStatusCode.TooManyRequests,
        /*lang=json,strict*/ """{"error":{"message":"This model's maximum context length is 131072 tokens."}}"""));

    Result<ModelResponse> result = await provider.SendAsync(
        Model, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("RateLimited", result.Error.Code);
  }

  [Fact]
  public async Task Timeout408_KeepsProviderTimeoutClassification()
  {
    OpenRouterModelProvider provider = Provider(() => Wire.Json(HttpStatusCode.RequestTimeout,
        /*lang=json,strict*/ """{"error":{"message":"This model's maximum context length is 131072 tokens."}}"""));

    Result<ModelResponse> result = await provider.SendAsync(
        Model, new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]),
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderTimeout", result.Error.Code);
  }
}
