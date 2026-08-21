using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderTests
{
    private static readonly Uri BaseUrl = new("https://openrouter.test");
    private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

    private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsContent()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"Hello back"}}]}""")));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var config = ModelConfig.Create("openai/gpt-4o-mini", 256, 0.7f).Value!;

        var result = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]));

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value!.Content);
        Assert.Empty(result.Value.ToolCalls);
    }

    [Fact]
    public async Task SendAsync_SendsBearerTokenAndModel()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            captured = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("openai/gpt-4o-mini", 128, 0.7f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
        Assert.Equal("https://openrouter.test/api/v1/chat/completions", captured!.RequestUri!.ToString());
        Assert.Contains("openai/gpt-4o-mini", capturedBody);
    }

    [Fact]
    public async Task SendAsync_WhenToolsPresent_SerializesRequiredAndAdditionalProperties()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var tools = new List<ToolDefinition>
        {
            new("read", "desc",
            [
                new ToolParameter("path", ToolParameterType.String, "file path"),
                new ToolParameter("startLine", ToolParameterType.Integer, "start", Minimum: 1),
                new ToolParameter("endLine", ToolParameterType.Integer, "end", Minimum: 1),
            ])
        };

        await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")], tools));

        Assert.Contains("\"required\":[\"path\",\"startLine\",\"endLine\"]", capturedBody);
        Assert.Contains("\"additionalProperties\":false", capturedBody);
        Assert.Contains("\"minimum\":1", capturedBody);
    }

    [Fact]
    public async Task SendAsync_ParsesToolCallsFromResponse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"read","arguments":"{\"path\":\"test.txt\"}"}}]}}]}""")));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Content);
        Assert.Single(result.Value.ToolCalls);
        Assert.Equal("call_1", result.Value.ToolCalls[0].Id);
        Assert.Equal("read", result.Value.ToolCalls[0].Name);
        Assert.Contains("test.txt", result.Value.ToolCalls[0].Arguments);
    }

    [Fact]
    public async Task SendAsync_SendsToolMessageWithToolCallId()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"final"}}]}""");
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);
        var messages = new List<Message>
        {
            UserMsg("hi"),
            new(Role.Assistant, "", DateTimeOffset.UtcNow,
                [new ToolCall("call_1", "read", "{}")]),
            new(Role.Tool, "result content", DateTimeOffset.UtcNow, ToolCallId: "call_1"),
        };

        await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest(messages));

        Assert.Contains("\"role\":\"tool\"", capturedBody);
        Assert.Contains("\"tool_call_id\":\"call_1\"", capturedBody);
        Assert.Contains("result content", capturedBody);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"call_1\"}]}}]}")]
    [InlineData("not json")]
    public async Task SendAsync_WhenSuccessPayloadIsMalformed_ReturnsProviderError(string payload)
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, payload)));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderError", result.Error!.Code);
    }

    [Fact]
    public async Task SendAsync_OnRateLimit_ReturnsRateLimitedError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.False(result.IsSuccess);
        Assert.Equal("RateLimited", result.Error!.Code);
    }

    [Fact]
    public async Task SendAsync_OnTimeout_ReturnsProviderTimeoutError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, Config);

        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([UserMsg("hi")]));

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderTimeout", result.Error!.Code);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
