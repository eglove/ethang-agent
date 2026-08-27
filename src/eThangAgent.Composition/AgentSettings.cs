using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>OpenRouter credentials. ApiKey may be null — the provider is only offered
///     when a key is configured, and a session that selects it without one fails
///     with a structured error.</summary>
public sealed record OpenRouterSettings(string? ApiKey, Uri BaseUrl);

/// <summary>z.ai credentials. ApiKey may be null — same rule as
///     <see cref="OpenRouterSettings"/>. BaseUrl defaults to the platform API root.</summary>
public sealed record ZaiSettings(string? ApiKey, Uri BaseUrl);

/// <summary>Everything a host needs before building the core. Provider keys are
///     independent: every configured provider is offered, and each opened session
///     picks one by id (<see cref="Providers"/>). ApiKeys may be null — each host
///     decides how to present a missing key (Desktop shows a dialog). ModelId, when
///     set, pins the root agent model and skips intelligent selection.</summary>
public sealed record AgentSettings(
    OpenRouterSettings OpenRouter,
    ZaiSettings Zai,
    SubAgentOptions SubAgents,
    string? ModelId = null)
{
  /// <summary>True when an OpenRouter API key (non-blank) is configured.</summary>
  public bool HasOpenRouter => !string.IsNullOrWhiteSpace(OpenRouter.ApiKey);

  /// <summary>True when a z.ai API key (non-blank) is configured.</summary>
  public bool HasZai => !string.IsNullOrWhiteSpace(Zai.ApiKey);
}
