using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>The OpenRouter PKCE sign-in: authorization in the user's browser, a loopback
///     callback, and a code exchange for a user-controlled API key.
///     <para>THE FLOW MINTS A CREDENTIAL; IT DOES NOT AVOID ONE. OpenRouter answers the
///     exchange with an ordinary long-lived API key, so the host stores what it receives
///     exactly as it stores a key the user pasted. What the flow removes is the copy-and-paste,
///     not the storage.</para></summary>
/// <param name="exchange">Redeems the authorization code for the key.</param>
/// <param name="config">The provider configuration the authorization URL is built from.</param>
/// <param name="openBrowser">Hands the authorization URL to the OS default browser and
///     reports whether it launched. A host substitutes it in tests.</param>
/// <param name="listeners">Starts the callback endpoint - the loopback listener in
///     production, a scripted one in tests.</param>
/// <param name="timeout">How long to wait for the browser; null uses
///     <see cref="DefaultTimeout"/>.</param>
public sealed class OpenRouterSignIn(OpenRouterCodeExchange exchange, OpenRouterConfiguration config,
    Func<Uri, bool> openBrowser, Func<IOpenRouterCallbackListener>? listeners = null,
    TimeSpan? timeout = null) : IProviderSignIn
{
  /// <summary>The label OpenRouter gives the minted key, so it is identifiable (and
  ///     revocable) in the user's key list.</summary>
  public const string KeyLabel = "eThang Agent";

  /// <summary>How long the flow waits for the browser to come back before giving up.
  ///     The authorization code itself expires in ten minutes, so the wait never outlives
  ///     the thing it is waiting for.</summary>
  public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

  private readonly OpenRouterCodeExchange _exchange = exchange
      ?? throw new ArgumentNullException(nameof(exchange));
  private readonly OpenRouterConfiguration _config = config
      ?? throw new ArgumentNullException(nameof(config));
  private readonly Func<Uri, bool> _openBrowser = openBrowser
      ?? throw new ArgumentNullException(nameof(openBrowser));
  private readonly Func<IOpenRouterCallbackListener> _listeners = listeners
      ?? LoopbackCallbackListener.Start;
  private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

  public async Task<Result<string>> SignInAsync(CancellationToken ct = default)
  {
    IOpenRouterCallbackListener listener = _listeners();
    try
    {
      return await RunAsync(listener, ct).ConfigureAwait(false);
    }
    finally
    {
      await listener.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>The authorization URL: the provider's /auth page carrying the callback, the
  ///     S256 challenge, the state, and the key label. Built from the configured base URL so
  ///     a host pointed at a proxy authorizes against the same host it will call.</summary>
  /// <param name="callbackUrl">Where the provider redirects back to.</param>
  /// <param name="pkce">The challenge and state for this attempt.</param>
  public Uri AuthorizeUrl(Uri callbackUrl, OpenRouterPkce pkce)
  {
    ArgumentNullException.ThrowIfNull(callbackUrl);
    ArgumentNullException.ThrowIfNull(pkce);
    string query = string.Join('&',
        "callback_url=" + Uri.EscapeDataString(callbackUrl.ToString()),
        "code_challenge=" + Uri.EscapeDataString(pkce.Challenge),
        "code_challenge_method=" + OpenRouterPkce.ChallengeMethod,
        "state=" + Uri.EscapeDataString(pkce.State),
        "key_label=" + Uri.EscapeDataString(KeyLabel));
    return _config.EndpointWithQuery("/auth", query);
  }

  /// <summary>One attempt over an already-started listener: authorize, wait for the
  ///     redirect, redeem the code.</summary>
  private async Task<Result<string>> RunAsync(IOpenRouterCallbackListener listener, CancellationToken ct)
  {
    OpenRouterPkce pkce = OpenRouterPkce.Create();
    if (!_openBrowser(AuthorizeUrl(listener.CallbackUrl, pkce)))
    {
      return Result.Failure<string>(new DomainError("SignInBrowserUnavailable",
          "The sign-in page could not be opened in a browser. No API key was requested."));
    }

    Result<string> code = await listener.WaitForCodeAsync(pkce.State, _timeout, ct)
        .ConfigureAwait(false);
    return code.IsSuccess
        ? await _exchange.ExchangeAsync(code.Value, pkce.Verifier, ct).ConfigureAwait(false)
        : code;
  }
}
