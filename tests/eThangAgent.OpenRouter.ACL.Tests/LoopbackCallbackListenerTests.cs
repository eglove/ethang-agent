using System.Net;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The loopback callback endpoint over a real listener: it answers the browser,
///     returns the code only for the state it was started with, and every abandonment
///     (timeout, cancellation, a stray request) ends as a typed failure.</summary>
[Trait("Category", "Process")]
public class LoopbackCallbackListenerTests
{
  [Fact]
  public async Task WaitForCode_ReturnsTheCode_WhenTheStateMatches()
  {
    await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();
    Task<Result<string>> waiting = listener.WaitForCodeAsync("expected-state",
        TestCeiling.Value, TestContext.Current.CancellationToken);

    await RedirectAsync(listener.CallbackUrl, "?code=the-code&state=expected-state");
    Result<string> result = await waiting;

    Assert.True(result.IsSuccess);
    Assert.Equal("the-code", result.Value);
  }

  [Fact]
  public async Task WaitForCode_SkipsAStrayRequest_AndStillReturnsTheRealCallback()
  {
    await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();
    Task<Result<string>> waiting = listener.WaitForCodeAsync("expected-state",
        TestCeiling.Value, TestContext.Current.CancellationToken);

    await RedirectAsync(listener.CallbackUrl, "?code=stale&state=someone-else");
    await RedirectAsync(listener.CallbackUrl, "?code=the-code&state=expected-state");
    Result<string> result = await waiting;

    Assert.True(result.IsSuccess);
    Assert.Equal("the-code", result.Value);
  }

  [Fact]
  public async Task WaitForCode_ReportsADeniedAuthorization()
  {
    await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();
    Task<Result<string>> waiting = listener.WaitForCodeAsync("expected-state",
        TestCeiling.Value, TestContext.Current.CancellationToken);

    await RedirectAsync(listener.CallbackUrl, "?error=access_denied&state=expected-state");
    Result<string> result = await waiting;

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInDenied", result.Error.Code);
  }

  [Fact]
  public async Task WaitForCode_TimesOut_WhenTheBrowserNeverReturns()
  {
    await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();

    Result<string> result = await listener.WaitForCodeAsync("expected-state",
        TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInTimeout", result.Error.Code);
  }

  [Fact]
  public async Task WaitForCode_ReportsCancellation()
  {
    await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();
    using CancellationTokenSource cancelled = new();
    await cancelled.CancelAsync();

    Result<string> result = await listener.WaitForCodeAsync("expected-state",
        TestCeiling.Value, cancelled.Token);

    Assert.False(result.IsSuccess);
    Assert.Equal("SignInCancelled", result.Error.Code);
  }

  [Fact]
  public async Task Start_BindsALoopbackCallbackUrl()
  {
    await using LoopbackCallbackListener listener = LoopbackCallbackListener.Start();

    Assert.Equal("127.0.0.1", listener.CallbackUrl.Host);
    Assert.Equal("/callback", listener.CallbackUrl.AbsolutePath);
    Assert.True(listener.CallbackUrl.Port > 0);
  }

  /// <summary>Plays the browser: one GET to the callback URL with the given query.
  ///     The listener's answer is read and discarded - the code comes back through
  ///     <see cref="LoopbackCallbackListener.WaitForCodeAsync"/>.</summary>
  private static async Task RedirectAsync(Uri callbackUrl, string query)
  {
    using HttpClient http = new();
    using HttpResponseMessage response = await http.GetAsync(new Uri(callbackUrl + query),
        TestContext.Current.CancellationToken);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }
}
