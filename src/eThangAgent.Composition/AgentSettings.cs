using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Zai.ACL;

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
///     <see cref="OpenRouterSettings"/>. BaseUrl defaults to the platform API root.
///     EndpointMode selects the GLM Coding Plan endpoint (default) or the general
///     pay-as-you-go endpoint; coding-plan keys only work on the coding path.</summary>
public sealed record ZaiSettings(string? ApiKey, Uri BaseUrl,
    ZaiEndpointMode EndpointMode = ZaiEndpointMode.CodingPlan)
{
  /// <summary>App-preference key the Desktop stores the (protected) z.ai key under.</summary>
  public const string PreferenceKey = "zai_api_key";

  /// <summary>App-preference key the Desktop stores the endpoint mode under (not a secret).</summary>
  public const string EndpointModePreferenceKey = "zai_endpoint_mode";
}

/// <summary>Local OpenAI-compatible provider settings. The base URL is carried as
///     raw text and resolved by the caller through <see cref="ResolveBaseUrl"/>: hosts
///     remember exactly what the user typed and validation errors surface at use time.
///     ApiKey may be null — same rule as <see cref="OpenRouterSettings"/>.</summary>
// CA1054/CA1056: BaseUrlText is deliberately string, not Uri — raw text is the
// contract (spec pin); parsing happens in ResolveBaseUrl, never at the boundary.
#pragma warning disable CA1054, CA1056
public sealed record LocalSettings(string? BaseUrlText, string? ApiKey)
{
  /// <summary>App-preference key the Desktop stores the (protected) local key under.</summary>
  public const string PreferenceKey = "local_api_key";

  /// <summary>App-preference key the Desktop stores the base URL under (not a secret).</summary>
  public const string BaseUrlPreferenceKey = "local_base_url";

  /// <summary>True when a base URL text (non-blank) is configured.</summary>
  public bool HasText => !string.IsNullOrWhiteSpace(BaseUrlText);

  /// <summary>Parses the configured base URL text. Success carries the absolute URI;
  ///     Failure carries the named <c>InvalidLocalBaseUrl</c> error — including for
  ///     blank text (callers check <see cref="HasText"/> first) — so nothing is ever
  ///     silently defaulted.</summary>
  public Result<Uri> ResolveBaseUrl()
  {
    return !HasText || !Uri.TryCreate(BaseUrlText, UriKind.Absolute, out Uri? parsed)
        ? Result.Failure<Uri>(new DomainError("InvalidLocalBaseUrl",
            "Local base URL is not a valid absolute URI: '" + BaseUrlText + "'."))
        : Result.Success(parsed);
  }
}
#pragma warning restore CA1054, CA1056

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
    SubAgentOptions SubAgents,
    bool RemoteHost = false,
    WatchdogSettings? Watchdog = null,
    LocalSettings? Local = null)
{
  // Local: null (the default) means unconfigured — a named decision, never silent
  // leniency: it keeps every existing construction site compiling, and hosts (the
  // Desktop) overlay it via WithLocalSettings/WithApiKeys.
  // Watchdog (W1.2): null means no SubAgent:Watchdog configuration — the host watchdog
  // runs WatchdogOptions.Default. The value travels to the child host inside the
  // settings JSON the RemoteHostSupervisor writes.
  /// <summary>True when an OpenRouter API key (non-blank) is configured.</summary>
  public bool HasOpenRouter => !string.IsNullOrWhiteSpace(OpenRouter.ApiKey);

  /// <summary>True when a z.ai API key (non-blank) is configured.</summary>
  public bool HasZai => !string.IsNullOrWhiteSpace(Zai.ApiKey);

  /// <summary>True when the local provider has a base URL text (non-blank) configured —
  ///     the point where it starts being offered.</summary>
  public bool HasLocal => Local is { } l && l.HasText;

  /// <summary>Returns the same settings with the two provider API keys overlaid. Null
  ///     clears a key — the provider stops being offered. Hosts use this to lift keys
  ///     from their own credential source (app preferences) onto the loaded settings.</summary>
  public AgentSettings WithApiKeys(string? openRouterApiKey, string? zaiApiKey, string? localApiKey = null) => this with
  {
    OpenRouter = OpenRouter with { ApiKey = openRouterApiKey },
    Zai = Zai with { ApiKey = zaiApiKey },
    Local = (Local ?? new LocalSettings(null, null)) with { ApiKey = localApiKey },
  };

  /// <summary>Returns the same settings with the z.ai endpoint mode overlaid. Hosts whose
  ///     durable preference store remembers the mode (the Desktop) use this to lift it onto
  ///     the loaded settings.</summary>
  public AgentSettings WithZaiEndpointMode(ZaiEndpointMode endpointMode) => this with
  {
    Zai = Zai with { EndpointMode = endpointMode },
  };

  /// <summary>Returns the same settings with the local provider's base URL and key
  ///     overlaid. Null text clears the base URL — the provider stops being offered.
  ///     Mirrors <see cref="WithZaiEndpointMode"/> for hosts whose durable preference
  ///     store remembers the local configuration (the Desktop).</summary>
  // CA1054: same deliberate raw-text contract as LocalSettings.BaseUrlText above.
#pragma warning disable CA1054
  public AgentSettings WithLocalSettings(string? baseUrlText, string? apiKey) => this with
  {
    Local = (Local ?? new LocalSettings(null, null)) with { BaseUrlText = baseUrlText, ApiKey = apiKey },
  };
#pragma warning restore CA1054
}
