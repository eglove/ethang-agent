using eThangAgent.ModelDomain;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>Creates Cloudflare providers for per-spawn models. One credential set serves every
///     model: the model id travels in the request path, so every provider this factory creates
///     shares the base configuration and the transport.
///     <para>THE MODEL CONFIG IS NOT BOUND HERE. A Cloudflare call names its model per request,
///     and the harness's walk resolves one entry per attempt, so a provider built once serves
///     every model the session selects.</para></summary>
/// <param name="baseConfig">The account, token, and host every created provider calls.</param>
/// <param name="http">The HTTP client every created provider goes through.</param>
public sealed class CloudflareModelProviderFactory(CloudflareConfiguration baseConfig, HttpClient http)
    : IModelProviderFactory
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

  private readonly CloudflareConfiguration _baseConfig =
      baseConfig ?? throw new ArgumentNullException(nameof(baseConfig));

  /// <inheritdoc />
  public IModelProvider Create(ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    return new CloudflareModelProvider(_http, _baseConfig);
  }
}
