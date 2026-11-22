namespace eThangAgent.Cloudflare.ACL;

/// <summary>Everything a Cloudflare Workers AI call needs to name: the account the models are
///     billed to, the API token that authorizes the call, and the host the API is reached on.
///     <para>THE BASE URL IS A HOST, NOT A PATH. Every endpoint is built through
///     <see cref="Endpoint"/>, which appends the account-scoped path to whatever path the base
///     already carries, so a configured base that itself has a path segment is not silently
///     dropped the way a host-root-absolute reference would drop it.</para>
///     <para>THE OPENAI-COMPATIBLE PATH IS NOT THIS HOST'S MODEL LIST: it answers
///     method-not-allowed for a model search, so the catalog reads the account-scoped search path
///     on the API host instead.</para>
///     <para>A WIRED CARRIER IS AUTHORITATIVE, INCLUDING WHEN IT HOLDS NOTHING. The positional
///     credential is the snapshot a container was built with; the carrier is what the host has
///     pushed since. A credential the user cleared must not fall back to that snapshot, which
///     would resurrect the value the save just removed.</para></summary>
/// <param name="AccountId">The Cloudflare account id the models are billed to.</param>
/// <param name="ApiToken">The API token presented as a bearer credential.</param>
/// <param name="BaseUrl">The API host, or null for <see cref="DefaultBaseUrl"/>.</param>
/// <param name="Live">The live credential carrier, or null to read the positional values.
/// </param>
/// <param name="GatewayId">The AI Gateway every inference call is routed through, or null to
///     call Workers AI directly.</param>
public sealed record CloudflareConfiguration(string? AccountId, string? ApiToken, Uri? BaseUrl = null,
    CloudflareCredentialCarrier? Live = null, string? GatewayId = null)
{
  /// <summary>The Cloudflare API host every endpoint is built on.</summary>
#pragma warning disable S1075 // the provider's documented public host, not a machine-local path
  public const string DefaultBaseUrl = "https://api.cloudflare.com";
#pragma warning restore S1075

  /// <summary>The header naming the AI Gateway an inference call is routed through.</summary>
  public const string GatewayIdHeader = "cf-aig-gateway-id";

  /// <summary>The API host this configuration reaches, defaulted when none was supplied.
  ///     </summary>
  public Uri ApiHost { get; init; } = BaseUrl ?? new Uri(DefaultBaseUrl);

  /// <summary>Warm-read freshness TTL, matching the OpenRouter catalog's: a second read inside
  ///     the window is served from memory and makes no HTTP call.</summary>
  public TimeSpan CatalogCacheTtl { get; init; } = TimeSpan.FromHours(24);

  /// <summary>The account id a call names now: the carrier's when one is wired, the snapshot
  ///     otherwise.</summary>
  public string? EffectiveAccountId => Live is null ? AccountId : Live.AccountId;

  /// <summary>The API token a call presents now: the carrier's when one is wired, the snapshot
  ///     otherwise.</summary>
  public string? EffectiveApiToken => Live is null ? ApiToken : Live.ApiToken;

  /// <summary>The AI Gateway id a call names now: the carrier's when one is wired, the snapshot
  ///     otherwise. Null means no gateway.</summary>
  public string? EffectiveGatewayId => Live is null ? GatewayId : Live.GatewayId;

  /// <summary>True when both halves of the credential are present, which is the least a call
  ///     needs. A caller checks this before building an endpoint, so an unconfigured credential
  ///     is a named failure rather than a malformed URL.</summary>
  public bool IsConfigured =>
      !string.IsNullOrWhiteSpace(EffectiveAccountId)
      && !string.IsNullOrWhiteSpace(EffectiveApiToken);

  /// <summary>Stamps a request with the credential: the token as a bearer credential, and the
  ///     AI Gateway id as <c>cf-aig-gateway-id</c> when one is configured. Every inference
  ///     request carries both, so no call can reach Cloudflare without the gateway the user
  ///     configured.</summary>
  /// <param name="request">The request to stamp.</param>
  public void ApplyCredential(HttpRequestMessage request)
  {
    ArgumentNullException.ThrowIfNull(request);
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EffectiveApiToken);
    if (EffectiveGatewayId is { } gatewayId)
    {
      _ = request.Headers.TryAddWithoutValidation(GatewayIdHeader, gatewayId);
    }
  }

  /// <summary>Builds an account-scoped endpoint from a path that starts at the account root.
  ///     <para>The path is appended to whatever path the host already carries, so a base with a
  ///     segment keeps it. Every Cloudflare endpoint MUST be built through this method.</para>
  ///     <para>AN UNCONFIGURED CREDENTIAL IS A PROGRAMMER ERROR HERE: a caller that has not
  ///     checked <see cref="IsConfigured"/> would otherwise build a URL naming no account.
  ///     </para>
  ///     </summary>
  /// <param name="path">The account-relative path, with or without a leading '/'.</param>
  public Uri Endpoint(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    string accountId = EffectiveAccountId is { } configured
        ? configured
        : throw new InvalidOperationException(
            "a Cloudflare endpoint was built without a configured account id.");
    UriBuilder builder = new(ApiHost)
    {
      Path = $"{ApiHost.AbsolutePath.TrimEnd('/')}/accounts/{accountId}/{path.TrimStart('/')}",
    };
    return builder.Uri;
  }
}
