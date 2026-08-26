using eThangAgent.ModelDomain;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>Creates providers for per-spawn models. One credential set serves every model: the model id travels per request, so created providers share the base configuration and transport.</summary>
public sealed class OpenRouterModelProviderFactory(OpenRouterConfiguration baseConfig, HttpClient http) : IModelProviderFactory
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _baseConfig = baseConfig ?? throw new ArgumentNullException(nameof(baseConfig));

  public IModelProvider Create(ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    return new OpenRouterModelProvider(_http, _baseConfig);
  }
}
