using eThangAgent.ModelDomain;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>Creates providers for per-spawn models. One credential set serves every model: the model id travels per request, so created providers share the base configuration and transport.</summary>
/// <remarks>The usage ledger is forwarded to every provider it creates so the Cheapest variant
///     can price its order at this workspace's measured shape. It is optional: a factory built
///     without one still creates providers, and the variant prices at the documented default.
///     </remarks>
public sealed class OpenRouterModelProviderFactory(OpenRouterConfiguration baseConfig, HttpClient http,
    IModelCatalog? catalog = null, OpenRouterApiKeyCarrier? keySource = null,
    IUsageQueries? usageQueries = null) : IModelProviderFactory
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _baseConfig = baseConfig ?? throw new ArgumentNullException(nameof(baseConfig));
  private readonly OpenRouterApiKeyCarrier? _keySource = keySource;

  public IModelProvider Create(ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    return new OpenRouterModelProvider(_http, _baseConfig, catalog: catalog, keySource: _keySource,
        usageQueries: usageQueries);
  }
}
