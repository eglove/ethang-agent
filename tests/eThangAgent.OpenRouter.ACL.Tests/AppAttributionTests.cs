using System.Net;

#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>App attribution (https://openrouter.ai/docs/app-attribution): every
///     outgoing OpenRouter request carries HTTP-Referer, X-OpenRouter-Title, and
///     X-OpenRouter-Categories so the app appears in OpenRouter's analytics and
///     rankings under its own identity. The ACL owns the header names and values;
///     hosts apply them to their shared HttpClient through the one helper.</summary>
public class AppAttributionTests
{
  [Fact]
  public void Apply_SetsAllThreeHeaders()
  {
    using HttpClient http = new();

    OpenRouterAppAttribution.Apply(http);

    Assert.Equal("https://github.com/eglove/ethang-agent",
        http.DefaultRequestHeaders.GetValues("HTTP-Referer").Single());
    Assert.Equal("eThang Agent",
        http.DefaultRequestHeaders.GetValues("X-OpenRouter-Title").Single());
    Assert.Equal("programming-app",
        http.DefaultRequestHeaders.GetValues("X-OpenRouter-Categories").Single());
  }

  [Fact]
  public void Apply_IsIdempotent()
  {
    using HttpClient http = new();

    OpenRouterAppAttribution.Apply(http);
    OpenRouterAppAttribution.Apply(http);

    _ = Assert.Single(http.DefaultRequestHeaders.GetValues("HTTP-Referer"));
  }

  [Fact]
  public async Task Apply_ReachesTheWire()
  {
    // Headers on DefaultRequestHeaders must actually ride outgoing requests —
    // the assertion that matters for attribution.
    HttpRequestMessage? captured = null;
    using HttpClient http = new(new CapturingHandler(r => captured = r));
    OpenRouterAppAttribution.Apply(http);

    using HttpRequestMessage request = new(HttpMethod.Post, "https://openrouter.test/api/v1/responses");
    using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal("eThang Agent", captured!.Headers.GetValues("X-OpenRouter-Title").Single());
  }

  private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      capture(request);
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
  }
}
