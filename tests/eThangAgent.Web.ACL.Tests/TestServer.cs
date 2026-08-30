namespace eThangAgent.Web.ACL.Tests;

/// <summary>Tiny in-process HTTP server for integration tests: real HTTP over
///     loopback, no external network. One handler delegate per server.</summary>
internal sealed class TestServer : IDisposable
{
  private readonly HttpListener _listener;
  private bool _disposed;

  public Uri BaseUrl { get; }

  public TestServer(Action<HttpListenerContext> respond)
  {
    _listener = new HttpListener();
    // Port 0 is not supported by HttpListener; bind a random high port instead.
    int port = System.Security.Cryptography.RandomNumberGenerator.GetInt32(41000, 60000);
    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    _listener.Start();
    BaseUrl = new Uri($"http://127.0.0.1:{port}");
    _ = Task.Run(() =>
    {
      while (_listener.IsListening)
      {
        try
        {
          HttpListenerContext ctx = _listener.GetContext();
          respond(ctx);
          ctx.Response.Close();
        }
        catch (HttpListenerException) when (_disposed)
        {
          break;
        }
        catch (HttpListenerException)
        {
          break; // listener stopped mid-accept during dispose
        }
        catch (ObjectDisposedException)
        {
          break;
        }
      }
    });
  }

  public static TestServer Serving(string contentType, string body, int status = 200)
  {
    return new TestServer(ctx =>
    {
      byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
      ctx.Response.StatusCode = status;
      ctx.Response.ContentType = contentType;
      ctx.Response.ContentLength64 = bytes.Length;
      ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    });
  }

  public static TestServer Redirecting(string fromPath, string redirectTo, string contentType, string body)
  {
    return new TestServer(ctx =>
    {
      if (ctx.Request.Url!.AbsolutePath == fromPath)
      {
        ctx.Response.RedirectLocation = redirectTo;
        ctx.Response.StatusCode = 301;
      }
      else
      {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
      }
    });
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _listener.Stop();
    _listener.Close();
  }
}
