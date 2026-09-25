// Integration fixture: real HTTP over loopback, no external network.

namespace eThangAgent.Web.ACL.Tests;

/// <summary>HttpSkillsShAccess against a fixture HTTP server (plan #29
/// task 8): URL construction with encoding, JSON passthrough, typed
/// failures for non-2xx and oversized bodies.</summary>
public sealed class HttpSkillsShAccessTests : IDisposable
{
  private readonly List<TestServer> _servers = [];

  public void Dispose()
  {
    foreach (TestServer s in _servers)
    {
      s.Dispose();
    }

    _servers.Clear();
    GC.SuppressFinalize(this);
  }

  private TestServer Start(TestServer server)
  {
    _servers.Add(server);
    return server;
  }

  [Fact]
  public async Task Search_QueriesTheApiPath_AndReturnsBodyVerbatim()
  {
    string lastPath = string.Empty;
    TestServer server = Start(new TestServer(ctx =>
    {
      lastPath = ctx.Request.Url!.PathAndQuery;
      byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"skills\":[]}");
      ctx.Response.StatusCode = 200;
      ctx.Response.ContentType = "application/json";
      ctx.Response.ContentLength64 = bytes.Length;
      ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }));

    using HttpSkillsShAccess access = new(server.BaseUrl);
    Result<string> r = await access.FetchSearchAsync("deploy", TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.Equal("{\"skills\":[]}", r.Value);
    Assert.Contains("/api/search", lastPath, StringComparison.Ordinal);
    Assert.Contains("q=deploy", lastPath, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Search_EncodesTheQueryTerm()
  {
    string lastPath = string.Empty;
    TestServer server = Start(new TestServer(ctx =>
    {
      lastPath = ctx.Request.Url!.PathAndQuery;
      byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"skills\":[]}");
      ctx.Response.StatusCode = 200;
      ctx.Response.ContentType = "application/json";
      ctx.Response.ContentLength64 = bytes.Length;
      ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }));

    using HttpSkillsShAccess access = new(server.BaseUrl);
    _ = await access.FetchSearchAsync("deploy skills", TestContext.Current.CancellationToken);
    Assert.Contains("q=deploy%20skills", lastPath, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NotFound_IsFetchFailed()
  {
    TestServer server = Start(TestServer.Serving("text/html", "<p>nope</p>", 404));
    using HttpSkillsShAccess access = new(server.BaseUrl);
    Result<string> r = await access.FetchSearchAsync("ghost", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("FetchFailed", r.Error.Code);
  }

  [Fact]
  public async Task OversizedBody_IsFetchFailed()
  {
    TestServer server = Start(TestServer.Serving("application/json", new string('x', 5000)));
    using HttpSkillsShAccess access = new(server.BaseUrl, maxBodyBytes: 1024);
    Result<string> r = await access.FetchSearchAsync("big", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("FetchFailed", r.Error.Code);
  }

  [Fact]
  public async Task UnknownHost_IsFetchFailed()
  {
    using HttpSkillsShAccess access = new(new Uri("http://eThangInvalidHost.invalid"));
    Result<string> r = await access.FetchSearchAsync("x", TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("FetchFailed", r.Error.Code);
  }
}
