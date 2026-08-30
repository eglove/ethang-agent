using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Behavioral contract of the web_fetch tool against a fake IWebAccess:
///     the verbatim output format, html-to-markdown dispatch, verbatim textual pass-
///     through, binary rejection, and typed error surfacing.</summary>
public class WebFetchToolTests
{
  private static readonly Uri Target = new("https://example.com/docs");

  private static WebFetchTool Make(FakeWebAccess fake) => new(fake, new StubHtmlToMarkdown());

  private static Task<ToolResult> Call(WebFetchTool tool, string url = "https://example.com/docs") =>
      tool.ExecuteAsync(new RawToolInput("web_fetch",
          /*lang=json,strict*/ "{\"url\":\"" + url + "\",\"timeoutSeconds\":60}"),
          ct: TestContext.Current.CancellationToken);

  private static FakeWebAccess Serving(string contentType, string body, int status = 200, string reason = "OK") =>
      new(Result.Success(new WebResource(
          Target, status, reason, contentType, body, TestBodyLength.Of(body))));

  // ---- Output contract ----

  [Fact]
  public async Task HtmlResponse_RendersAnnotationAndMarkdown()
  {
    string html = "<html><body><h1>Hi</h1></body></html>";
    ToolResult result = await Call(Make(Serving("text/html; charset=utf-8", html)));
    Assert.False(result.IsError);
    Assert.Equal(
        $"""
        [web-fetch https://example.com/docs — 200 OK, text/html; charset=utf-8, {TestBodyLength.Of(html)} B → {TestBodyLength.Of(StubHtmlToMarkdown.Output)} B markdown]
        {StubHtmlToMarkdown.Output}
        """,
        result.Content);
  }

  [Fact]
  public async Task PlainTextResponse_ReturnedVerbatimWithAnnotation()
  {
    string body = "robots.txt\nsays hello";
    ToolResult result = await Call(Make(Serving("text/plain; charset=utf-8", body)));
    Assert.False(result.IsError);
    Assert.Equal(
        $"""
        [web-fetch https://example.com/docs — 200 OK, text/plain; charset=utf-8, {TestBodyLength.Of(body)} B, verbatim]
        {body}
        """,
        result.Content);
  }

  [Fact]
  public async Task JsonResponse_ReturnedVerbatim()
  {
    ToolResult result = await Call(Make(Serving("application/json", "{\"ok\":true}")));
    Assert.False(result.IsError);
    Assert.Contains("application/json", result.Content, StringComparison.Ordinal);
    Assert.Contains("\"ok\":true", result.Content, StringComparison.Ordinal);
    Assert.Contains("verbatim", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonSuccessStatus_IsReportedNotThrown()
  {
    ToolResult result = await Call(Make(Serving("text/plain", "gone", 410, "Gone")));
    Assert.False(result.IsError);
    Assert.Contains("410 Gone", result.Content, StringComparison.Ordinal);
    Assert.Contains("gone", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BinaryContentType_IsRejected()
  {
    ToolResult result = await Call(Make(Serving("image/png", "\u0001\u0002\u0003")));
    Assert.True(result.IsError);
    Assert.Contains("Error [UnsupportedMediaType]", result.Content, StringComparison.Ordinal);
    Assert.Contains("image/png", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LargeBody_SizeRenderedInKb()
  {
    ToolResult result = await Call(Make(Serving("text/plain", new string('~', 2048))));
    Assert.False(result.IsError);
    Assert.Contains("2 KB, verbatim", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TransportError_SurfacesVerbatim()
  {
    FakeWebAccess fake = new(Result.Failure<WebResource>(
        new DomainError("DnsError", "no such host: nosuchhost.invalid")));
    ToolResult result = await Call(Make(fake));
    Assert.True(result.IsError);
    Assert.Contains("Error [DnsError]: no such host", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonHtmlContent_SkipsConverter()
  {
    WebFetchTool tool = new(Serving("text/plain", "just text"), new ThrowingStubConverter());
    ToolResult result = await Call(tool);
    Assert.False(result.IsError); // converter must never run for non-html content
  }

  // ---- Input contract (tool surface) ----

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    FakeWebAccess fake = new(Result.Failure<WebResource>(new DomainError("Unused", "x")));
    WebFetchTool tool = new(fake, new StubHtmlToMarkdown());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("web_fetch",
        /*lang=json,strict*/ "{\"url\":\"https://example.com/\",\"timeoutSeconds\":60,\"depth\":2}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RelativeUrl_RejectedAtToolSurface()
  {
    FakeWebAccess fake = new(Result.Failure<WebResource>(new DomainError("Unused", "x")));
    WebFetchTool tool = new(fake, new StubHtmlToMarkdown());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("web_fetch",
        /*lang=json,strict*/ "{\"url\":\"docs/x.html\",\"timeoutSeconds\":60}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidParameterValue]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingTimeout_RejectedAtDispatch()
  {
    FakeWebAccess fake = new(Result.Failure<WebResource>(new DomainError("Unused", "x")));
    WebFetchTool tool = new(fake, new StubHtmlToMarkdown());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("web_fetch",
        /*lang=json,strict*/ "{\"url\":\"https://example.com/\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ZeroTimeout_RejectedAtDispatch()
  {
    FakeWebAccess fake = new(Result.Failure<WebResource>(new DomainError("Unused", "x")));
    WebFetchTool tool = new(fake, new StubHtmlToMarkdown());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("web_fetch",
        /*lang=json,strict*/ "{\"url\":\"https://example.com/\",\"timeoutSeconds\":0}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidParameterValue]", result.Content, StringComparison.Ordinal);
  }
}
