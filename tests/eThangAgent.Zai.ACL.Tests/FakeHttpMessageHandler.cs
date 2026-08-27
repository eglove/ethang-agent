namespace eThangAgent.Zai.ACL.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
  private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond = respond;

  protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
      => _respond(request);
}
