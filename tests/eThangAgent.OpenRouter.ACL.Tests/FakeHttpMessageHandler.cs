namespace eThangAgent.OpenRouter.ACL.Tests;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => _respond(request);
}
