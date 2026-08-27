namespace eThangAgent.Zai.ACL;

/// <summary>Credentials and transport for the z.ai platform API. The base URL points at the
///     API root (default <c>https://api.z.ai/api</c>); chat completions live under
///     <c>/paas/v4/chat/completions</c> relative to it. Override the base URL to point tests
///     at a local mock server.</summary>
public sealed record ZaiConfiguration(string ApiKey, Uri BaseUrl)
{
  public const string DefaultBaseUrl = "https://api.z.ai/api";

  /// <summary>Transient-failure retry policy. Defaults to four attempts with exponential backoff.</summary>
  public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
}
