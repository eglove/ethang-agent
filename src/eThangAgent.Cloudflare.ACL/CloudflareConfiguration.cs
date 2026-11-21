namespace eThangAgent.Cloudflare.ACL;

/// <summary>Everything a Cloudflare Workers AI call needs to name: the account the models are
///     billed to, the API token that authorizes the call, and the host the API is reached on.
///     <para>THE BASE URL IS A HOST, NOT A PATH. Every endpoint is built through
///     <see cref="Endpoint"/>, which appends the account-scoped path to whatever path the base
///     already carries, so a configured base that itself has a path segment is not silently
///     dropped the way a host-root-absolute reference would drop it.</para>
///     <para>THE OPENAI-COMPATIBLE PATH IS NOT THIS HOST'S MODEL LIST: it answers
///     method-not-allowed for a model search, so the catalog reads the account-scoped search path
///     on the API host instead.</para></summary>
/// <param name="AccountId">The Cloudflare account id the models are billed to.</param>
/// <param name="ApiToken">The API token presented as a bearer credential.</param>
/// <param name="BaseUrl">The API host, or null for <see cref="DefaultBaseUrl"/>.</param>
public sealed record CloudflareConfiguration(string AccountId, string ApiToken, Uri? BaseUrl = null)
{
  /// <summary>The Cloudflare API host every endpoint is built on.</summary>
#pragma warning disable S1075 // the provider's documented public host, not a machine-local path
  public const string DefaultBaseUrl = "https://api.cloudflare.com";
#pragma warning restore S1075

  /// <summary>The API host this configuration reaches, defaulted when none was supplied.
  ///     </summary>
  public Uri ApiHost { get; init; } = BaseUrl ?? new Uri(DefaultBaseUrl);

  /// <summary>Warm-read freshness TTL, matching the OpenRouter catalog's: a second read inside
  ///     the window is served from memory and makes no HTTP call.</summary>
  public TimeSpan CatalogCacheTtl { get; init; } = TimeSpan.FromHours(24);

  /// <summary>Builds an account-scoped endpoint from a path that starts at the account root.
  ///     <para>The path is appended to whatever path the host already carries, so a base with a
  ///     segment keeps it. Every Cloudflare endpoint MUST be built through this method.</para>
  ///     </summary>
  /// <param name="path">The account-relative path, with or without a leading '/'.</param>
  public Uri Endpoint(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    UriBuilder builder = new(ApiHost)
    {
      Path = $"{ApiHost.AbsolutePath.TrimEnd('/')}/accounts/{AccountId}/{path.TrimStart('/')}",
    };
    return builder.Uri;
  }
}
