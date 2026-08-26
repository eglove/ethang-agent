namespace eThangAgent.OpenRouter.ACL;

public sealed record OpenRouterConfiguration(string ApiKey, Uri BaseUrl)
{
  /// <summary>Transient-failure retry policy. Defaults to four attempts with exponential backoff.</summary>
  public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
}
