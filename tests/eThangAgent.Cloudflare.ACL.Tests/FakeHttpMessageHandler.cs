using System.Net;
using System.Text;

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>A stand-in for Cloudflare's HTTP surface: every request is answered by the supplied
///     responder, and every request that was made is recorded so a test can assert that a cached
///     read made none.</summary>
/// <param name="respond">The response to return for a request.</param>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
  private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond = respond;

  /// <summary>Every request this handler answered, in the order they arrived.</summary>
  public List<HttpRequestMessage> Requests { get; } = [];

  /// <summary>The number of requests this handler answered.</summary>
  public int CallCount => Requests.Count;

  protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
  {
    Requests.Add(request);
    return Task.FromResult(_respond(request));
  }

  /// <summary>A handler that always answers with one JSON body and status.</summary>
  /// <param name="json">The body.</param>
  /// <param name="status">The status.</param>
  public static FakeHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
      => new(_ => new HttpResponseMessage(status)
      {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
      });
}
