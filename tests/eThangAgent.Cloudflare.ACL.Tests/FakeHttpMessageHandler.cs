using System.Net;
using System.Text;

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>A stand-in for Cloudflare's HTTP surface: every request is answered by the supplied
///     responder, and every request that was made is recorded so a test can assert that a cached
///     read made none.
///     <para>THE RECORDED REQUEST'S CONTENT IS READ EAGERLY, because an HttpRequestMessage's
///     content is disposed with the message: a test that read it afterwards would find nothing.
///     </para></summary>
/// <param name="respond">The response to return for a request.</param>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
  private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond = respond;

  /// <summary>Every request this handler answered, in the order they arrived.</summary>
  public List<HttpRequestMessage> Requests { get; } = [];

  /// <summary>Every request body this handler received, in the order they arrived.</summary>
  public List<string> Bodies { get; } = [];

  /// <summary>The number of requests this handler answered.</summary>
  public int CallCount => Requests.Count;

  /// <summary>The single body this handler received. Fails when it received any other number.
  ///     </summary>
  public string Body => Assert.Single(Bodies);

  protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
  {
    Requests.Add(request);
    Bodies.Add(request.Content is null
        ? string.Empty
        : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    return _respond(request);
  }

  /// <summary>A handler that always answers with one JSON body and status.</summary>
  /// <param name="json">The body.</param>
  /// <param name="status">The status.</param>
  public static FakeHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
      => new(_ => new HttpResponseMessage(status)
      {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
      });

  /// <summary>A handler that answers with an event stream.</summary>
  /// <param name="frames">The SSE frames, already formatted.</param>
  public static FakeHttpMessageHandler Sse(string frames)
      => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(frames, Encoding.UTF8, "text/event-stream"),
      });
}
