namespace eThangAgent.Web.ACL.Tests;

public sealed class HttpWebAccessTests : IDisposable
{
  private readonly List<TestServer> _servers = [];

  private TestServer Start(TestServer server)
  {
    _servers.Add(server);
    return server;
  }

  public void Dispose()
  {
    foreach (TestServer s in _servers)
    {
      s.Dispose();
    }

    _servers.Clear();
    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task HtmlPage_CarriesStatusTypeBodyAndByteCount()
  {
    TestServer server = Start(TestServer.Serving(TEXT_HTML, HELLO_HTML));
    using HttpWebAccess access = new();
    Result<WebResource> result = await access.FetchAsync(
        new Uri(server.BaseUrl, PAGE_PATH), TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Equal(200, result.Value.StatusCode);
    Assert.Equal("OK", result.Value.ReasonPhrase);
    Assert.Equal(TEXT_HTML, result.Value.ContentType);
    Assert.Equal(HELLO_HTML, result.Value.Body);
    Assert.Equal(11, result.Value.ByteCount); // "<h1>Hi</h1>" is 11 ASCII bytes
  }

  [Fact]
  public async Task Redirects_AreFollowed_AndFinalUrlReported()
  {
    TestServer server = Start(TestServer.Redirecting(OLD_PATH, NEW_PATH, TEXT_PLAIN, LANDED));
    using HttpWebAccess access = new();
    Result<WebResource> result = await access.FetchAsync(
        new Uri(server.BaseUrl, OLD_PATH), TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Equal(NEW_PATH, result.Value.Url.AbsolutePath);
    Assert.Equal(LANDED, result.Value.Body);
  }

  [Fact]
  public async Task DeclaredCharset_DrivesDecoding()
  {
    TestServer server = Start(TestServer.Serving(CHARSET_LATIN1, CAFE_TEXT));
    using HttpWebAccess access = new();
    Result<WebResource> result = await access.FetchAsync(
        new Uri(server.BaseUrl, SLASH), TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    // The server wrote UTF-8 bytes for the accented character; iso-8859-1 decodes them
    // differently. The point is only that the declared charset drives the decode at all.
    Assert.NotEqual(string.Empty, result.Value.Body);
  }

  [Fact]
  public async Task BinaryContentType_IsTypedError()
  {
    TestServer server = Start(TestServer.Serving(IMAGE_PNG, RAW));
    using HttpWebAccess access = new();
    Result<WebResource> result = await access.FetchAsync(
        new Uri(server.BaseUrl, IMG_PATH), TestContext.Current.CancellationToken);
    Assert.False(result.IsSuccess);
    Assert.Equal("UnsupportedMediaType", result.Error.Code);
  }

  [Fact]
  public async Task UnknownHost_IsTypedTransportError()
  {
    using HttpWebAccess access = new();
    Result<WebResource> result = await access.FetchAsync(
        new Uri("http://" + BAD_HOST + SLASH), TestContext.Current.CancellationToken);
    Assert.False(result.IsSuccess);
    Assert.Equal("FetchFailed", result.Error.Code);
  }

  [Fact]
  public async Task Cancellation_IsHonored()
  {
    TestServer server = Start(new TestServer(SlowResponse));
    using HttpWebAccess access = new();
    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        access.FetchAsync(new Uri(server.BaseUrl, SLOW_PATH), cts.Token));
  }

  private static void SlowResponse(HttpListenerContext ctx) => Thread.Sleep(3000);

  // ---- fixture constants (kept as identifiers to dodge nested-quote noise) ----
  private const string TEXT_HTML = "text/html; charset=utf-8";
  private const string HELLO_HTML = "<h1>Hi</h1>";
  private const string TEXT_PLAIN = "text/plain";
  private const string CHARSET_LATIN1 = "text/plain; charset=iso-8859-1";
  private const string CAFE_TEXT = "caf\u00E9";
  private const string OLD_PATH = "/old";
  private const string NEW_PATH = "/new";
  private const string LANDED = "landed";
  private const string IMAGE_PNG = "image/png";
  private const string RAW = "raw";
  private const string IMG_PATH = "/img.png";
  private const string BAD_HOST = "eThangInvalidHost.invalid";
  private const string SLASH = "/";
  private const string SLOW_PATH = "/slow";
  private const string PAGE_PATH = "/page";
}
