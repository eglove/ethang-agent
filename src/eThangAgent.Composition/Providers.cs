namespace eThangAgent.Composition;

/// <summary>One choosable AI provider integration, as presented by host UIs.</summary>
public sealed record ProviderOption(string Id, string DisplayName);

/// <summary>Provider ids plus the per-provider model identities that code paths must not
///     hardcode: the fallback model for failed or unavailable selection, and the selector
///     model that powers the two-stage selection pipeline's own LLM calls. OpenRouter's
///     "auto" pseudo-model routes server-side; a provider without an equivalent supplies
///     a concrete cheap model instead.</summary>
public static class Providers
{
  public const string OpenRouter = "openrouter";

  public const string Zai = "zai";

  public const string Local = "local";

  /// <summary>Preference key under which the last user-chosen provider is persisted
  ///     in the app database.</summary>
  public const string PreferenceKey = "active_provider";

  public static bool IsKnown(string? providerName)
      => providerName is OpenRouter or Zai or Local;

  /// <summary>Human-facing provider name (dropdowns, status bars).</summary>
  public static string DisplayName(string providerName) => providerName switch
  {
    OpenRouter => "OpenRouter",
    Zai => "z.ai",
    Local => "Local (OpenAI-compatible)",
    _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id.")
  };

  /// <summary>Fallback model id when selection fails or no selector is wired.</summary>
  public static string FallbackModelId(string providerName) => providerName switch
  {
    OpenRouter => "openrouter/auto",
    Zai => "glm-5.3-flash",
    _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id.")
  };

  /// <summary>Model id for the selection pipeline's own categorize/decide calls.</summary>
  public static string SelectorModelId(string providerName) => FallbackModelId(providerName);

  /// <summary>The OpenRouter server-side routing pseudo-model id; it has no catalog row.</summary>
  public const string RoutingModelId = "openrouter/auto";

  /// <summary>Curated operational context window for the routing pseudo-model: a
  ///     conservative floor (the smallest common upstream window), never a guessed exact.</summary>
  public const int RoutingContextWindow = 128_000;
}
