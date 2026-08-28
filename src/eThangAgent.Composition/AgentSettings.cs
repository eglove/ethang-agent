using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>OpenRouter credentials. ApiKey may be null — the provider is only offered
///     when a key is configured, and a session that selects it without one fails
///     with a structured error. Hosts source the key themselves (the Desktop reads it
///     from app preferences via the Settings modal) and overlay it with
///     <see cref="AgentSettings.WithApiKeys"/>.</summary>
public sealed record OpenRouterSettings(string? ApiKey, Uri BaseUrl)
{
  /// <summary>App-preference key the Desktop stores the (protected) OpenRouter key under.</summary>
  public const string PreferenceKey = "openrouter_api_key";
}

/// <summary>z.ai credentials. ApiKey may be null — same rule as
///     <see cref="OpenRouterSettings"/>. BaseUrl defaults to the platform API root.</summary>
public sealed record ZaiSettings(string? ApiKey, Uri BaseUrl)
{
  /// <summary>App-preference key the Desktop stores the (protected) z.ai key under.</summary>
  public const string PreferenceKey = "zai_api_key";
}

/// <summary>Everything a host needs before building the core. Provider keys are
///     independent: every configured provider is offered, and each opened session
///     picks one by id (<see cref="Providers"/>). ApiKeys may be null — each host
///     decides how to present a missing key (Desktop shows a dialog). There is no
///     configured model pin: the model is chosen per session (intelligent selection
///     on OpenRouter, the provider default on z.ai) or by the user through the
///     host's model picker.</summary>
public sealed record AgentSettings(
    OpenRouterSettings OpenRouter,
    ZaiSettings Zai,
    SubAgentOptions SubAgents)
{
  /// <summary>True when an OpenRouter API key (non-blank) is configured.</summary>
  public bool HasOpenRouter => !string.IsNullOrWhiteSpace(OpenRouter.ApiKey);

  /// <summary>True when a z.ai API key (non-blank) is configured.</summary>
  public bool HasZai => !string.IsNullOrWhiteSpace(Zai.ApiKey);

  /// <summary>Returns the same settings with the two provider API keys overlaid. Null
  ///     clears a key — the provider stops being offered. Hosts use this to lift keys
  ///     from their own credential source (app preferences) onto the loaded settings.</summary>
  public AgentSettings WithApiKeys(string? openRouterApiKey, string? zaiApiKey) => this with
  {
    OpenRouter = OpenRouter with { ApiKey = openRouterApiKey },
    Zai = Zai with { ApiKey = zaiApiKey },
  };
}
