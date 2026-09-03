namespace eThangAgent.Local.ACL;

/// <summary>Transport for one OpenAI-compatible local server: chat completions plus
///     the model-listing and context-probe endpoints the ACL consumes. The API key is
///     optional — most local servers need none; auth-enforcing ones (e.g. llama.cpp
///     --api-key) still work. Endpoints MUST be built through <see cref="Endpoint"/>:
///     a two-argument Uri combine with a leading-slash reference is host-root-absolute
///     and would silently drop the base path (e.g. LM Studio's /v1).</summary>
public sealed record LocalConfiguration(Uri BaseUrl, string? ApiKey = null)
{
  /// <summary>Transient-failure retry policy. Defaults to four attempts with exponential backoff.</summary>
  public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

  public Uri ChatCompletionsEndpoint() => Endpoint("/chat/completions");
  public Uri ModelsEndpoint() => Endpoint("/v1/models");
  public Uri LmStudioModelsEndpoint() => Endpoint("/api/v0/models");
  public Uri OllamaShowEndpoint() => Endpoint("/api/show");

  public Uri Endpoint(string path)
  {
    UriBuilder builder = new(BaseUrl);
    builder.Path = $"{builder.Path.TrimEnd('/')}{path}";
    return builder.Uri;
  }
}
