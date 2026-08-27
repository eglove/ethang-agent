using eThangAgent.ModelDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Creates providers for per-spawn models. One credential set serves every model: the
///     model id travels per request, so created providers share the base configuration and
///     transport.</summary>
public sealed class ZaiModelProviderFactory(ZaiConfiguration baseConfig, HttpClient http) : IModelProviderFactory
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _baseConfig = baseConfig ?? throw new ArgumentNullException(nameof(baseConfig));

  public IModelProvider Create(ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    return new ZaiModelProvider(_http, _baseConfig);
  }
}
