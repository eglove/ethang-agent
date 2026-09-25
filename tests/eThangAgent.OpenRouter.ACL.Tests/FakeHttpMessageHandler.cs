using System.Net;
using System.Text;

namespace eThangAgent.OpenRouter.ACL.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
  private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond = respond;

  protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
      => _respond(request);
}

/// <summary>Shared wire fixtures for the OpenRouter Responses API surface: the
///     minimal successful non-streaming body, an SSE envelope, and a JSON status
///     response. Every provider test that needs a canned OK answer uses these so a
///     parser change surfaces as one fixture edit, not twenty.</summary>
internal static class Wire
{
  /// <summary>A minimal successful Responses API body: one assistant message item
  ///     carrying a single output_text part, status completed, no usage.</summary>
  public const string OkBody =
      /*lang=json,strict*/
      """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}""";

  /// <summary>The terminal stream event with the same shape as the JSON body's
  ///     core — the streaming parser's contract mirror of <see cref="OkBody"/>.</summary>
  public const string CompletedTerminal =
      """data: {"type":"response.completed","response":{"status":"completed","output":[]}}""" + "\n\n";

  public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
      new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  public static HttpResponseMessage Ok(HttpStatusCode code = HttpStatusCode.OK) =>
      Json(code, OkBody);

  public static HttpResponseMessage Sse(string raw) =>
      new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") };
}
