using System.Net;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; the test's lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The sign-in orchestration: the browser receives the authorization URL built
///     from the configured base URL, the callback's code is redeemed, and every abandoned
///     attempt ends as a typed failure with the listener disposed.</summary>
public class OpenRouterSignInTests
{
  private static OpenRouterConfiguration Config =>
      new("unused", new Uri("https://openrouter.ai"));

  private static OpenRouterCodeExchange Exchange(FakeHttpMessageHandler handler) =>
      new(new HttpClient(handler), Config);

  private static FakeHttpMessageHandler MintsKey() =>
      new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK,
          /*lang=json,strict*/ """{"key":"sk-or-v1-minted"}""")));

  [Fact]
  public async Task SignIn_OpensTheBrowserWithTheAuthorizeUrl_AndReturnsTheMintedKey()
  {
    Uri? opened = null;
    ScriptedCallbackListener listener = new(Result.Success("the-code"));
    OpenRouterSignIn signIn = new(Exchange(MintsKey()), Config,
        url =>
        {
          opened = url;
          return true;
        },
        () => listener);

    Result<string> result = await signIn.SignInAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("sk-or-v1-minted", result.Value);
    Assert.NotNull(opened);
    Assert.Equal("https://openrouter.ai/auth", opened.GetLeftPart(UriPartial.Path));
    Assert.Contains("code_challenge_method=S256", opened.Query, StringComparison.Ordinal);
    Assert.Contains("callback_url=http%3A%2F%2Flocalhost%3A4321%2Fcallback", opened.Query,
        StringComparison.Ordinal);
    Assert.Contains("key_label=eThang%20Agent", opened.Query, StringComparison.Ordinal);
    Assert.True(listener.Disposed);
  }

  [Fact]
  public async Task SignIn_HandsTheCallbackStateToTheListener()
  {
    ScriptedCallbackListener listener = new(Result.Success("the-code"));
    OpenRouterSignIn signIn = new(Exchange(MintsKey()), Config, _ => true, () => listener);

    _ = await signIn.SignInAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(listener.ExpectedState);
    Assert.NotEmpty(listener.ExpectedState);
  }

  [Fact]
  public async Task SignIn_ReturnsTheListenerFailure_WithoutRedeemingAnything()
  {
    int exchanges = 0;
    FakeHttpMessageHandler handler = new(_ =>
    {
      exchanges++;
      return Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/ """{"key":"k"}"""));
    });
    ScriptedCallbackListener listener = new(Result.Failure<string>(
        new DomainError("SignInTimeout", "timed out")));
    OpenRouterSignIn signIn = new(Exchange(handler), Config, _ => true, () => listener);

    Result<string> result = await signIn.SignInAsync(TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInTimeout", result.Error.Code);
    Assert.Equal(0, exchanges);
    Assert.True(listener.Disposed);
  }

  [Fact]
  public async Task SignIn_ReportsABrowserThatWillNotLaunch()
  {
    ScriptedCallbackListener listener = new(Result.Success("the-code"));
    OpenRouterSignIn signIn = new(Exchange(MintsKey()), Config, _ => false, () => listener);

    Result<string> result = await signIn.SignInAsync(TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInBrowserUnavailable", result.Error.Code);
    Assert.True(listener.Disposed);
  }

  [Fact]
  public void AuthorizeUrl_IsBuiltFromTheConfiguredBaseUrl()
  {
    OpenRouterSignIn signIn = new(
        Exchange(MintsKey()),
        new OpenRouterConfiguration("unused", new Uri("https://proxy.example")),
        _ => true);
    OpenRouterPkce pkce = OpenRouterPkce.Create();

    Uri url = signIn.AuthorizeUrl(new Uri("http://127.0.0.1:5000/callback"), pkce);

    Assert.Equal("proxy.example", url.Host);
    Assert.Equal("/auth", url.AbsolutePath);
    Assert.Contains("code_challenge=" + Uri.EscapeDataString(pkce.Challenge), url.Query,
        StringComparison.Ordinal);
    Assert.Contains("state=" + Uri.EscapeDataString(pkce.State), url.Query, StringComparison.Ordinal);
  }

  /// <summary>A callback listener that hands back a scripted outcome, so the orchestration
  ///     is tested without binding a port.</summary>
  private sealed class ScriptedCallbackListener(Result<string> outcome) : IOpenRouterCallbackListener
  {
    public Uri CallbackUrl { get; } = new("http://localhost:4321/callback");

    public string? ExpectedState { get; private set; }

    public bool Disposed { get; private set; }

    public Task<Result<string>> WaitForCodeAsync(string expectedState, TimeSpan timeout,
        CancellationToken ct)
    {
      ExpectedState = expectedState;
      return Task.FromResult(outcome);
    }

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }
}
