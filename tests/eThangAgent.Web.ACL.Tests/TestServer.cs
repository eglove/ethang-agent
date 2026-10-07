using System.Net.Sockets;

namespace eThangAgent.Web.ACL.Tests;

/// <summary>Tiny in-process HTTP server for integration tests: real HTTP over
///     loopback, no external network. One handler delegate per server.</summary>
internal sealed class TestServer : IDisposable
{
  private readonly HttpListener _listener;
  private bool _disposed;

  /// <summary>Bind retries after a stolen probe; five matches the mock provider's
  ///     listener in Desktop.Tests.</summary>
  private const int MaxBindAttempts = 5;

  public Uri BaseUrl { get; }

  public TestServer(Action<HttpListenerContext> respond)
  {
    // Port 0 is not supported by HttpListener; probe a free high port instead and
    // retry on a stolen probe: the TcpListener probe closes before HttpListener
    // binds, and a concurrently starting listener can win that port under the
    // solution's parallel test modules (CI: HttpListenerException 'file in use').
    // A FAILED Start() closes the HttpListener (state Closed), so each attempt
    // binds a FRESH listener - retrying Start() on the same instance throws
    // ObjectDisposedException.
    Uri baseUri = null!;
    HttpListener listener = null!;
    for (int attempt = 1; attempt <= MaxBindAttempts; attempt++)
    {
      listener = new HttpListener();
      int port = GetFreePort();
      string prefix = $"http://127.0.0.1:{port}/";
      listener.Prefixes.Add(prefix);
      baseUri = new Uri(prefix);
      try
      {
        listener.Start();
        break;
      }
      catch (HttpListenerException) when (attempt < MaxBindAttempts)
      {
        listener.Close(); // the failed attempt's listener is dead - drop it
      }
    }

    _listener = listener;
    BaseUrl = baseUri;
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

  /// <summary>Probes a free loopback port: binds TcpListener on port 0, reads the
  ///     assigned port, closes. The probe window is tiny but real - callers retry the
  ///     whole probe+bind on collision.</summary>
  private static int GetFreePort()
  {
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
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
