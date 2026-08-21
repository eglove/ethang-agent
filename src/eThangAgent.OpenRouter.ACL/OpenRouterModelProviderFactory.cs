using eThangAgent.ModelDomain;

namespace eThangAgent.OpenRouter.ACL;

/// <summary>Creates providers for per-spawn models. One credential set serves every model: the model id travels per request, so created providers share the base configuration and transport.</summary>
public sealed class OpenRouterModelProviderFactory : IModelProviderFactory
{
    private readonly HttpClient _http;
    private readonly OpenRouterConfiguration _baseConfig;

    public OpenRouterModelProviderFactory(OpenRouterConfiguration baseConfig, HttpClient http)
    {
        _baseConfig = baseConfig ?? throw new ArgumentNullException(nameof(baseConfig));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public IModelProvider Create(ModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new OpenRouterModelProvider(_http, _baseConfig);
    }
}