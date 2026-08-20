using System.Net;
using System.Text;
using eThangAgent.Model.Domain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderTests
{
    private static readonly Uri BaseUrl = new("https://openrouter.test");
    private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsContent()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"Hello back"}}]}"""));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var modelConfig = ModelConfig.Create("openai/gpt-4o-mini", 256, 0.7f).Value!;

        var result = await provider.SendAsync(modelConfig, "hi", default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value);
    }

    [Fact]
    public async Task SendAsync_SendsBearerTokenModelAndPath()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("openai/gpt-4o-mini", 128, 0.7f).Value!, "hi", default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
        Assert.Equal("https://openrouter.test/api/v1/chat/completions", captured!.RequestUri!.ToString());
        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("openai/gpt-4o-mini", body);
    }

    [Fact]
    public async Task SendAsync_OnRateLimit_ReturnsRateLimitedError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!, "hi", default);

        Assert.False(result.IsSuccess);
        Assert.Equal("RateLimited", result.Error!.Code);
    }

    [Fact]
    public async Task SendAsync_OnTimeout_ReturnsProviderTimeoutError()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!, "hi", default);

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderTimeout", result.Error!.Code);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
        => new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
