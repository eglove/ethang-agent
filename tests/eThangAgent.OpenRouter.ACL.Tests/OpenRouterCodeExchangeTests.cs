using System.Net;
using System.Text.Json;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; the test's lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The code exchange: the request carries the code, the verifier and the S256
///     method; a successful body's 'key' is the result; every failure is typed and never
///     an empty credential.</summary>
public class OpenRouterCodeExchangeTests
{
  private static OpenRouterConfiguration Config =>
      new("unused", new Uri("https://openrouter.ai"));

  private static OpenRouterCodeExchange Exchange(FakeHttpMessageHandler handler) =>
      new(new HttpClient(handler), Config);

  [Fact]
  public async Task Exchange_PostsTheCodeVerifierAndMethod_AndReturnsTheKey()
  {
    string? body = null;
    string? path = null;
    FakeHttpMessageHandler handler = new(request =>
    {
      path = request.RequestUri!.AbsolutePath;
      body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
      return Task.FromResult(Wire.Json(HttpStatusCode.OK,
          /*lang=json,strict*/ """{"key":"sk-or-v1-minted"}"""));
    });

    Result<string> result = await Exchange(handler).ExchangeAsync("the-code", "the-verifier",
        TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("sk-or-v1-minted", result.Value);
    Assert.Equal("/api/v1/auth/keys", path);
    Assert.NotNull(body);
    using JsonDocument sent = JsonDocument.Parse(body);
    Assert.Equal("the-code", sent.RootElement.GetProperty("code").GetString());
    Assert.Equal("the-verifier", sent.RootElement.GetProperty("code_verifier").GetString());
    Assert.Equal("S256", sent.RootElement.GetProperty("code_challenge_method").GetString());
  }

  [Fact]
  public async Task Exchange_ReportsTheProviderRefusalAsATypedFailure()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.Forbidden,
        /*lang=json,strict*/ """{"error":{"message":"Invalid code or code_verifier"}}""")));

    Result<string> result = await Exchange(handler).ExchangeAsync("stale", "verifier",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInExchangeFailed", result.Error.Code);
    Assert.Contains("Invalid code or code_verifier", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Exchange_RefusesABodyWithoutAKey()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, "{}")));

    Result<string> result = await Exchange(handler).ExchangeAsync("code", "verifier",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInExchangeFailed", result.Error.Code);
  }

  [Fact]
  public async Task Exchange_RefusesAnUnreadableBody()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, "not json")));

    Result<string> result = await Exchange(handler).ExchangeAsync("code", "verifier",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInExchangeFailed", result.Error.Code);
  }

  [Fact]
  public async Task Exchange_UsesTheConfiguredBaseUrl()
  {
    Uri? target = null;
    FakeHttpMessageHandler handler = new(request =>
    {
      target = request.RequestUri;
      return Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/ """{"key":"k"}"""));
    });
    OpenRouterCodeExchange exchange = new(new HttpClient(handler),
        new OpenRouterConfiguration("unused", new Uri("https://proxy.example")));

    _ = await exchange.ExchangeAsync("code", "verifier", TestContext.Current.CancellationToken);

    Assert.NotNull(target);
    Assert.Equal("proxy.example", target.Host);
    Assert.Equal("/api/v1/auth/keys", target.AbsolutePath);
  }
}
