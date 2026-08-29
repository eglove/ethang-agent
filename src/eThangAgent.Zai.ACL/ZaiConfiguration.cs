namespace eThangAgent.Zai.ACL;

/// <summary>Credentials and transport for the z.ai platform API. The base URL points at the
///     API root (default <c>https://api.z.ai/api</c>); chat completions live under
///     <c>/coding/paas/v4/chat/completions</c> (CodingPlan mode) or
///     <c>/paas/v4/chat/completions</c> (GeneralApi mode) relative to it. Override the base
///     URL to point tests at a local mock server.</summary>
public sealed record ZaiConfiguration(string ApiKey, Uri BaseUrl)
{
  public const string DefaultBaseUrl = "https://api.z.ai/api";

  /// <summary>Transient-failure retry policy. Defaults to four attempts with exponential backoff.</summary>
  public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

  /// <summary>Which endpoint family requests target. CodingPlan (the default) routes chat
  ///     through the GLM Coding Plan path, where the capability APIs do not exist; GeneralApi
  ///     is the pay-as-you-go platform endpoint.</summary>
  public ZaiEndpointMode EndpointMode { get; init; } = ZaiEndpointMode.CodingPlan;

  /// <summary>The chat-completions endpoint for the configured <see cref="EndpointMode"/>.</summary>
  public Uri ChatCompletionsEndpoint() => Endpoint(EndpointMode == ZaiEndpointMode.CodingPlan
      ? "/coding/paas/v4/chat/completions"
      : "/paas/v4/chat/completions");

  /// <summary>Builds a platform endpoint by appending <paramref name="path"/> to the base
  ///     URL's path. The two-argument <see cref="Uri"/> constructor cannot express this: a
  ///     leading-slash reference is host-root-absolute, so combining the default base with
  ///     <c>/paas/v4/…</c> would silently drop the <c>/api</c> segment and 404 every call.
  ///     All z.ai endpoints MUST be built through this method.</summary>
  public Uri Endpoint(string path)
  {
    UriBuilder builder = new(BaseUrl);
    builder.Path = $"{builder.Path.TrimEnd('/')}{path}";
    return builder.Uri;
  }
}
