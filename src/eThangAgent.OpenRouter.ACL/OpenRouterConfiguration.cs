namespace eThangAgent.OpenRouter.ACL;

public sealed record OpenRouterConfiguration(string ApiKey, Uri BaseUrl, string? ManagementKey = null)
{
  /// <summary>Transient-failure retry policy. Defaults to four attempts with exponential backoff.</summary>
  public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

  /// <summary>Warm-read freshness TTL (24 hours). Disk hydration always revalidates once; settings can explicitly join revalidation.</summary>
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

  /// <summary>Builds an API endpoint with a QUERY STRING, which <see cref="Endpoint"/> cannot
  ///     express: it assigns the whole argument to <see cref="UriBuilder.Path"/>, so a '?'
  ///     inside it is escaped to %3F and the request silently 404s against a path that
  ///     contains the query. The query is passed separately and assigned to
  ///     <see cref="UriBuilder.Query"/>, where it belongs.</summary>
  /// <param name="path">The path, without a query string.</param>
  /// <param name="query">The query, with or without a leading '?'.</param>
  public Uri EndpointWithQuery(string path, string query)
  {
    ArgumentNullException.ThrowIfNull(query);
    UriBuilder builder = new(Endpoint(path))
    {
      Query = query.TrimStart('?'),
    };
    return builder.Uri;
  }
}
