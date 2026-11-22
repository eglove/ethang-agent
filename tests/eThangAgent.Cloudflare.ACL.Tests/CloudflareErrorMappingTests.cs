using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it
// JSON002 fires only in the format/IDE host, which cannot see that these literals are wire
// fixtures under test - the pragma pair is the repo's named decision for hand-written shapes.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>How the provider maps a failed status to a named failure: every code carries
///     Cloudflare's own text, and a 429 never claims which Cloudflare limit was reached.
///     <para>THE CODES ARE THE CONTRACT step 5's model walk reads, so each one is asserted by
///     its exact literal.</para></summary>
public class CloudflareErrorMappingTests
{
  private static CloudflareModelProvider Provider(HttpStatusCode status, string body)
      => new(new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(status)
      {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
      })), new CloudflareConfiguration("acct-123", "token-abc"));

  private static ModelConfig Model()
      => ModelConfig.Create("@cf/meta/llama-3.1-8b-instruct", 256, 0.7f, 4096).Value!;

  /// <summary>Cloudflare's error envelope around one message.</summary>
  /// <param name="message">The message Cloudflare states.</param>
  private static string Envelope(string message)
      => $"{{\"success\":false,\"errors\":[{{\"code\":1000,\"message\":\"{message}\"}}],"
          + "\"messages\":[],\"result\":null}";

  private static async Task<Result<ModelResponse>> Send(HttpStatusCode status, string message)
      => await Provider(status, Envelope(message))
          .SendAsync(Model(), new ModelRequest([]), TestContext.Current.CancellationToken);

  [Fact]
  public async Task ARateLimitedStatus_IsRateLimitedCarryingCloudflaresText()
  {
    const string text = "you have used up your daily allocation of neurons";

    Result<ModelResponse> sent = await Send(HttpStatusCode.TooManyRequests, text);

    Assert.False(sent.IsSuccess);
    Assert.Equal("RateLimited", sent.Error.Code);
    Assert.Contains(text, sent.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ARateLimit_IsNeverDescribedAsASpentDailyAllowance()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.TooManyRequests,
        "you have used up your daily allocation of neurons");

    Assert.False(sent.IsSuccess);
    Assert.DoesNotContain("daily allowance is spent", sent.Error.Message,
        StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("per-minute", sent.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task AForbiddenStatus_IsAProviderUnauthorizedCarryingCloudflaresText()
  {
    const string text = "Unable to authenticate request";

    Result<ModelResponse> sent = await Send(HttpStatusCode.Forbidden, text);

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderUnauthorized", sent.Error.Code);
    Assert.Contains(text, sent.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnUnauthorizedStatus_IsProviderUnauthorized()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.Unauthorized, "invalid token");

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderUnauthorized", sent.Error.Code);
  }

  [Fact]
  public async Task ABadRequestNamingAContextLengthFault_IsContextWindowExceeded()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.BadRequest,
        "the prompt exceeds the maximum context length of this model");

    Assert.False(sent.IsSuccess);
    Assert.Equal("ContextWindowExceeded", sent.Error.Code);
  }

  [Fact]
  public async Task ABadRequestNamingAnUnrelatedFault_IsModelRejected()
  {
    const string text = "the model does not support this request";

    Result<ModelResponse> sent = await Send(HttpStatusCode.BadRequest, text);

    Assert.False(sent.IsSuccess);
    Assert.Equal("ModelRejected", sent.Error.Code);
    Assert.Contains(text, sent.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ANotFoundStatus_IsModelRejected()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.NotFound,
        "no such model for this account");

    Assert.False(sent.IsSuccess);
    Assert.Equal("ModelRejected", sent.Error.Code);
  }

  [Fact]
  public async Task ARequestTimeout_IsProviderTimeout()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.RequestTimeout, "took too long");

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderTimeout", sent.Error.Code);
  }

  [Fact]
  public async Task AServerError_IsProviderError()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.InternalServerError, "boom");

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderError", sent.Error.Code);
    Assert.Contains("boom", sent.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnUnrecognisedStatus_IsProviderError()
  {
    Result<ModelResponse> sent = await Send((HttpStatusCode)418, "nonsense");

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderError", sent.Error.Code);
  }

  [Fact]
  public async Task AFailureQuotesAtMostTheCappedBodyLength()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.InternalServerError,
        new string('x', 4000));

    Assert.False(sent.IsSuccess);
    Assert.True(sent.Error.Message.Length < 700, sent.Error.Message.Length.ToString(
        System.Globalization.CultureInfo.InvariantCulture));
  }

  [Fact]
  public async Task AFailureWithNoBody_StillNamesTheStatus()
  {
    CloudflareModelProvider provider = new(
        new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.BadGateway))),
        new CloudflareConfiguration("acct-123", "token-abc"));

    Result<ModelResponse> sent = await provider.SendAsync(Model(), new ModelRequest([]),
        TestContext.Current.CancellationToken);

    Assert.False(sent.IsSuccess);
    Assert.Equal("ProviderError", sent.Error.Code);
    Assert.Contains("502", sent.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ACredentialRefusal_IsNotReportedAsAModelVerdict()
  {
    Result<ModelResponse> sent = await Send(HttpStatusCode.Forbidden, "invalid token");

    Assert.False(sent.IsSuccess);
    Assert.NotEqual("ModelRejected", sent.Error.Code);
    Assert.NotEqual("RateLimited", sent.Error.Code);
  }
#pragma warning restore JSON002 // Probable JSON string detected
}
