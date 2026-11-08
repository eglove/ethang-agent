using System.Net;
using eThangAgent.SharedKernel;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>The loopback endpoint one sign-in attempt listens on for the provider's
///     redirect. A seam so the flow is testable without binding a real port: tests
///     substitute a listener that hands back a scripted code.</summary>
public interface IOpenRouterCallbackListener : IAsyncDisposable
{
  /// <summary>The URL the provider redirects to, including its port.</summary>
  Uri CallbackUrl { get; }

  /// <summary>Waits for the redirect and returns its authorization code. A redirect
  ///     carrying a state other than <paramref name="expectedState"/> is refused without
  ///     consuming the attempt; a redirect carrying an error, a timeout, or cancellation
  ///     is a typed failure.</summary>
  Task<Result<string>> WaitForCodeAsync(string expectedState, TimeSpan timeout, CancellationToken ct);
}

/// <summary><see cref="IOpenRouterCallbackListener"/> over a loopback
///     <see cref="HttpListener"/>. The provider accepts localhost callbacks on any port,
///     so the port is chosen at start time and the URL is only known once it is bound.</summary>
public sealed class LoopbackCallbackListener : IOpenRouterCallbackListener
{
  /// <summary>Path the provider redirects to.</summary>
  public const string CallbackPath = "/callback";

  private readonly HttpListener _listener;
  private bool _disposed;

  private LoopbackCallbackListener(HttpListener listener, Uri callbackUrl)
  {
    _listener = listener;
    CallbackUrl = callbackUrl;
  }

  public Uri CallbackUrl { get; }

  /// <summary>Binds a free loopback port and starts listening. Ports are probed rather
  ///     than reserved: the window between probing and binding is a race the retry loop
  ///     absorbs.</summary>
  public static LoopbackCallbackListener Start()
  {
    for (int attempt = 0; attempt < 5; attempt++)
    {
      int port = FreeLoopbackPort();
      string prefix = $"http://127.0.0.1:{port}/";
      HttpListener listener = new();
      listener.Prefixes.Add(prefix);
      try
      {
        listener.Start();
        return new LoopbackCallbackListener(listener, new Uri(prefix + CallbackPath.TrimStart('/')));
      }
      catch (HttpListenerException)
      {
        listener.Close();
      }
    }

    throw new InvalidOperationException("No loopback port could be bound for the sign-in callback.");
  }

  public async Task<Result<string>> WaitForCodeAsync(string expectedState, TimeSpan timeout,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(expectedState);
    using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
    bound.CancelAfter(timeout);

    while (true)
    {
      HttpListenerContext context;
      try
      {
        context = await _listener.GetContextAsync().WaitAsync(bound.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return Result.Failure<string>(ct.IsCancellationRequested
            ? new DomainError("SignInCancelled", "The OpenRouter sign-in was cancelled.")
            : new DomainError("SignInTimeout",
                "The OpenRouter sign-in timed out waiting for the browser to return."));
      }
      catch (HttpListenerException)
      {
        return Result.Failure<string>(new DomainError("SignInCancelled",
            "The OpenRouter sign-in stopped listening for the browser."));
      }

      Result<string> outcome = Answer(context, expectedState);
      if (outcome.IsSuccess)
      {
        return outcome;
      }

      // A stray request (favicon, a second tab, a stale callback) is answered and skipped:
      // the attempt is still waiting for the redirect that carries the expected state.
      if (outcome.Error.Code != "SignInStateMismatch")
      {
        return outcome;
      }
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    try
    {
      _listener.Stop();
      _listener.Close();
    }
    catch (ObjectDisposedException)
    {
      // Already closed - disposal is idempotent by contract.
    }

    await Task.CompletedTask.ConfigureAwait(false);
  }

  /// <summary>Answers one request: writes the browser-facing page and folds the query
  ///     into a code, a typed failure, or a state mismatch the loop retries on.</summary>
  private static Result<string> Answer(HttpListenerContext context, string expectedState)
  {
    string? code = context.Request.QueryString["code"];
    string? state = context.Request.QueryString["state"];
    string? error = context.Request.QueryString["error"];

    if (error is { Length: > 0 })
    {
      WritePage(context, false, "Sign-in was not authorized. You can close this window.");
      return Result.Failure<string>(new DomainError("SignInDenied",
          $"OpenRouter reported the sign-in was not authorized: {error}"));
    }

    if (code is not { Length: > 0 })
    {
      return Result.Failure<string>(new DomainError("SignInStateMismatch",
          "A request arrived without an authorization code."));
    }

    if (!string.Equals(state, expectedState, StringComparison.Ordinal))
    {
      WritePage(context, false, "This sign-in response does not belong to this request.");
      return Result.Failure<string>(new DomainError("SignInStateMismatch",
          "The callback state did not match the request that started the sign-in."));
    }

    WritePage(context, true, "Signed in. You can close this window and return to eThang Agent.");
    return Result.Success(code);
  }

  private static void WritePage(HttpListenerContext context, bool success, string message)
  {
    string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>eThang Agent</title>"
        + "</head><body style=\"font-family:Segoe UI,sans-serif;padding:2rem\"><h2>"
        + (success ? "Signed in" : "Sign-in failed") + "</h2><p>" + WebUtility.HtmlEncode(message)
        + "</p></body></html>";
    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(html);
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = bytes.Length;
    context.Response.OutputStream.Write(bytes);
    context.Response.Close();
  }

  /// <summary>One free loopback port, found by binding port 0 and reading back the
  ///     assignment.</summary>
  private static int FreeLoopbackPort()
  {
    using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
    probe.Start();
    try
    {
      return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
    finally
    {
      probe.Stop();
    }
  }
}
