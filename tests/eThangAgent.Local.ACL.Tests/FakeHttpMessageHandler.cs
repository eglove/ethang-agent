namespace eThangAgent.Local.ACL.Tests;

// CA1812: deliberately scaffolded ahead of its first consumer — the wire tests that
// drive HttpClient through this handler land in a later task of the same plan.
// Remove the pragma when those tests arrive.
#pragma warning disable CA1812
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
#pragma warning restore CA1812
{
  private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond = respond;

  protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
      => _respond(request);
}
