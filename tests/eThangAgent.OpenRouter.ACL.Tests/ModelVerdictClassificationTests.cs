using System.Net;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The statuses a model uses to refuse a request are its own code, not a
///     ProviderError: a 400 naming a fault other than the context-length one and a 404 carry
///     ModelRejected, while a 401 or 403 carries ProviderUnauthorized — a refused credential is
///     a configuration fault about the account rather than the model's verdict, so a caller
///     stops and reports it instead of walking to the next model. A 429 stays RateLimited, a 408
///     stays ProviderTimeout, and a 5xx stays ProviderError. A refusal is spent on ONE HTTP
///     attempt, because repeating the identical request fails identically.</summary>
public class ModelVerdictClassificationTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static ModelConfig Model => ModelConfig.Create("m", 256, 0.7f, 4096).Value!;

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  /// <summary>Counts HTTP attempts and records the backoff waits, so a case can assert
  ///     both that no retry was spent and that a retryable fault does spend them.</summary>
  private sealed class Recorder
  {
    internal int _calls;

    public List<TimeSpan> Delays { get; } = [];

    public Func<int, HttpResponseMessage> Respond { get; set; } = _ => Wire.Ok();

    public HttpClient Client() => new(new FakeHttpMessageHandler(_ =>
    {
      _calls++;
      return Task.FromResult(Respond(_calls));
    }));
  }

  private static OpenRouterModelProvider Provider(Recorder rec, int maxAttempts = 4) =>
      new(rec.Client(),
          new OpenRouterConfiguration("test-key", BaseUrl) { Retry = new RetryPolicy(maxAttempts) },
          delay: (wait, _) =>
          {
            rec.Delays.Add(wait);
            return Task.CompletedTask;
          },
          jitter: () => 0.0);

  private static string RefusalBody(int status) => status switch
  {
    400 => /*lang=json,strict*/ "{\"error\":{\"message\":\"invalid tools: too many\"}}",
    401 => /*lang=json,strict*/ "{\"error\":{\"message\":\"invalid api key\"}}",
    403 => /*lang=json,strict*/ "{\"error\":{\"message\":\"this key is not allowed to use this model\"}}",
    404 => /*lang=json,strict*/ "{\"error\":{\"message\":\"no such model\"}}",
    _ => throw new ArgumentOutOfRangeException(nameof(status), status, "only the refusal statuses are in this table."),
  };

  private static Task<Result<ModelResponse>> SendAsync(OpenRouterModelProvider provider, bool streaming) =>
      streaming
          ? provider.SendStreamingAsync(Model, new ModelRequest([UserMsg("hi")]), _ => { },
              ct: TestContext.Current.CancellationToken)
          : provider.SendAsync(Model, new ModelRequest([UserMsg("hi")]),
              TestContext.Current.CancellationToken);

  /// <summary>The statuses that are the MODEL's verdict on the request or on itself.</summary>
  public static TheoryData<int> ModelRejectedStatuses
  {
    get
    {
      TheoryData<int> statuses = [];
      statuses.Add(400);
      statuses.Add(404);
      return statuses;
    }
  }

  /// <summary>The statuses that are a REFUSED CREDENTIAL: a configuration fault about the
  ///     account, never the model's verdict.</summary>
  public static TheoryData<int> UnauthorizedStatuses
  {
    get
    {
      TheoryData<int> statuses = [];
      statuses.Add(401);
      statuses.Add(403);
      return statuses;
    }
  }

  [Theory]
  [MemberData(nameof(ModelRejectedStatuses))]
  public async Task SendAsync_OnAModelVerdict_CarriesModelRejected(int status) =>
      await RefusalOutcomeAsync(status, streaming: false, "ModelRejected");

  [Theory]
  [MemberData(nameof(ModelRejectedStatuses))]
  public async Task SendStreamingAsync_OnAModelVerdict_CarriesModelRejected(int status) =>
      await RefusalOutcomeAsync(status, streaming: true, "ModelRejected");

  [Theory]
  [MemberData(nameof(UnauthorizedStatuses))]
  public async Task SendAsync_OnARefusedCredential_CarriesProviderUnauthorized(int status) =>
      await RefusalOutcomeAsync(status, streaming: false, "ProviderUnauthorized");

  [Theory]
  [MemberData(nameof(UnauthorizedStatuses))]
  public async Task SendStreamingAsync_OnARefusedCredential_CarriesProviderUnauthorized(int status) =>
      await RefusalOutcomeAsync(status, streaming: true, "ProviderUnauthorized");

  private static async Task RefusalOutcomeAsync(int status, bool streaming, string expectedCode)
  {
    Recorder rec = new() { Respond = _ => Wire.Json((HttpStatusCode)status, RefusalBody(status)) };
    OpenRouterModelProvider provider = Provider(rec);

    Result<ModelResponse> result = await SendAsync(provider, streaming).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal(expectedCode, result.Error.Code);
    Assert.Contains($"OpenRouter returned HTTP {status}", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(RefusalBody(status), result.Error.Message, StringComparison.Ordinal);
    // ONE ATTEMPT AND NO BACKOFF: the request was refused on its merits, so a repeat
    // would be refused identically.
    Assert.Equal(1, rec._calls);
    Assert.Empty(rec.Delays);
  }

  [Fact]
  public async Task SendAsync_OnAContextOverflow400_CarriesContextWindowExceeded() =>
      await OverflowOutcomeAsync(streaming: false);

  [Fact]
  public async Task SendStreamingAsync_OnAContextOverflow400_CarriesContextWindowExceeded() =>
      await OverflowOutcomeAsync(streaming: true);

  private static async Task OverflowOutcomeAsync(bool streaming)
  {
    const string Body = /*lang=json,strict*/
        "{\"error\":{\"message\":\"This model's maximum context length is 200000 tokens.\"}}";
    Recorder rec = new() { Respond = _ => Wire.Json(HttpStatusCode.BadRequest, Body) };
    OpenRouterModelProvider provider = Provider(rec);

    Result<ModelResponse> result = await SendAsync(provider, streaming).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    // The overflow branch is decided BEFORE the refusal branch: the agent loop's forced
    // compaction fires on this code, so the 400 that names the fault must never reach
    // ModelRejected.
    Assert.Equal("ContextWindowExceeded", result.Error.Code);
    Assert.Equal(1, rec._calls);
  }

  [Fact]
  public async Task SendAsync_On429_CarriesRateLimited_NamingTheStatusAndTheProvidersWords() =>
      await RateLimitOutcomeAsync(streaming: false);

  [Fact]
  public async Task SendStreamingAsync_On429_CarriesRateLimited_NamingTheStatusAndTheProvidersWords() =>
      await RateLimitOutcomeAsync(streaming: true);

  private static async Task RateLimitOutcomeAsync(bool streaming)
  {
    const string Body = /*lang=json,strict*/ "{\"error\":{\"message\":\"rate limited by upstream\"}}";
    Recorder rec = new() { Respond = _ => Wire.Json(HttpStatusCode.TooManyRequests, Body) };
    OpenRouterModelProvider provider = Provider(rec, maxAttempts: 6);

    Result<ModelResponse> result = await SendAsync(provider, streaming).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RateLimited", result.Error.Code);
    Assert.Contains("OpenRouter returned HTTP 429", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(Body, result.Error.Message, StringComparison.Ordinal);
    // THE PROVIDER LAYER'S OWN RETRYABILITY IS UNCHANGED for a rate limit: the code only
    // reaches the domain once the provider's attempts are spent.
    Assert.Equal(6, rec._calls);
    Assert.Equal(5, rec.Delays.Count);
  }

  [Fact]
  public async Task SendAsync_On503_CarriesProviderError_AndIsRetried() =>
      await RetryableOutcomeAsync(HttpStatusCode.ServiceUnavailable, "ProviderError", streaming: false);

  [Fact]
  public async Task SendStreamingAsync_On503_CarriesProviderError_AndIsRetried() =>
      await RetryableOutcomeAsync(HttpStatusCode.ServiceUnavailable, "ProviderError", streaming: true);

  [Fact]
  public async Task SendAsync_On408_CarriesProviderTimeout_AndIsRetried() =>
      await RetryableOutcomeAsync(HttpStatusCode.RequestTimeout, "ProviderTimeout", streaming: false);

  [Fact]
  public async Task SendStreamingAsync_On408_CarriesProviderTimeout_AndIsRetried() =>
      await RetryableOutcomeAsync(HttpStatusCode.RequestTimeout, "ProviderTimeout", streaming: true);

  private static async Task RetryableOutcomeAsync(HttpStatusCode status, string code, bool streaming)
  {
    Recorder rec = new() { Respond = _ => Wire.Json(status, /*lang=json,strict*/ "{\"error\":{\"message\":\"try again\"}}") };
    OpenRouterModelProvider provider = Provider(rec, maxAttempts: 3);

    Result<ModelResponse> result = await SendAsync(provider, streaming).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal(code, result.Error.Code);
    // Three attempts of three: this classification is retryable, which is what tells it
    // apart from the refusals above.
    Assert.Equal(3, rec._calls);
    Assert.Equal(2, rec.Delays.Count);
  }
}
