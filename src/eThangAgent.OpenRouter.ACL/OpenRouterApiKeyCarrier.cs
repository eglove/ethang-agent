namespace eThangAgent.OpenRouter.ACL;

/// <summary>Holds the model-call API key for a session, readable LIVE at dispatch: a key
///     saved mid-session (Settings, API Keys) is pushed onto the carrier and the next
///     request presents it, instead of the frozen <see cref="OpenRouterConfiguration"/>
///     snapshot the container was built with. Seeded from settings at container build;
///     the host pushes updates. Null falls back to the configuration's own key.</summary>
public sealed class OpenRouterApiKeyCarrier
{
  /// <summary>The live model key, or null to keep using the configured one.</summary>
  public string? Current { get; set; }
}
