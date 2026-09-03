using eThangAgent.ModelDomain;

namespace eThangAgent.Local.ACL;

/// <summary>Creates providers for per-spawn models. One credential set serves every model: the
///     model id travels per request, so created providers share the base configuration and
///     transport.</summary>
public sealed class LocalModelProviderFactory(LocalConfiguration baseConfig, HttpClient http) : IModelProviderFactory
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly LocalConfiguration _baseConfig = baseConfig ?? throw new ArgumentNullException(nameof(baseConfig));

  public IModelProvider Create(ModelConfig config)
  {
    ArgumentNullException.ThrowIfNull(config);
    return new LocalModelProvider(_http, _baseConfig);
  }
}
