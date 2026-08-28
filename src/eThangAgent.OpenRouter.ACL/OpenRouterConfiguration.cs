namespace eThangAgent.OpenRouter.ACL;

public sealed record OpenRouterConfiguration(string ApiKey, Uri BaseUrl)
{
  /// <summary>Transient-failure retry policy. Defaults to four attempts with exponential backoff.</summary>
  public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

  /// <summary>How long the model catalog cache is valid before a re-fetch is triggered. Default: 24 hours.</summary>
  public TimeSpan CatalogCacheTtl { get; init; } = TimeSpan.FromHours(24);
  /// <summary>Max parallel requests when fetching per-model endpoints. Default: 8.</summary>
  public int EndpointFetchConcurrency { get; init; } = 8;

  /// <summary>Builds an API endpoint by appending <paramref name="path"/> to the base
  ///     URL's path. The two-argument <see cref="Uri"/> constructor cannot express this: a
  ///     leading-slash reference is host-root-absolute, so a base URL that itself carries
  ///     a path segment would have it silently dropped. All OpenRouter endpoints MUST be
  ///     built through this method.</summary>
  public Uri Endpoint(string path)
  {
    UriBuilder builder = new(BaseUrl);
    builder.Path = $"{builder.Path.TrimEnd('/')}{path}";
    return builder.Uri;
  }
}
