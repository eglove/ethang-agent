using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>One choosable AI provider integration, as presented by host UIs.</summary>
/// <param name="Id">The provider id.</param>
/// <param name="DisplayName">The human-facing name.</param>
public sealed record ProviderOption(string Id, string DisplayName);

/// <summary>Provider ids plus the per-provider model identities that code paths must not
///     hardcode: the fallback model for failed or unavailable selection, and the selector
///     model that powers the two-stage selection pipeline's own LLM calls. OpenRouter's
///     "auto" pseudo-model routes server-side.
///     <para>THE VENDOR VOCABULARY ITSELF LIVES IN <see cref="ModelVendors"/>, because the Model
///     Domain cannot reference this assembly and needs the same ids to carry a vendor on a
///     selection entry. This type delegates to it, so there is exactly one copy of each literal.
///     </para></summary>
public static class Providers
{
  /// <summary>OpenRouter's provider id.</summary>
  public const string OpenRouter = ModelVendors.OpenRouter;

  /// <summary>Cloudflare Workers AI's provider id.</summary>
  public const string Cloudflare = ModelVendors.Cloudflare;

  /// <summary>Preference key under which the last user-chosen provider is persisted
  ///     in the app database.</summary>
  public const string PreferenceKey = "active_provider";

  /// <summary>True when <paramref name="providerName"/> names a provider this build knows.
  ///     </summary>
  /// <param name="providerName">The provider id to check.</param>
  public static bool IsKnown(string? providerName) => ModelVendors.IsKnown(providerName);

  /// <summary>Human-facing provider name (dropdowns, status bars). An id that is no
  ///     longer known — a persisted row written before its provider was removed — renders
  ///     as its raw id instead of throwing: this is a display function over historical
  ///     data, and a list surface must never crash on a row it cannot name.</summary>
  /// <param name="providerName">The provider id to render.</param>
  public static string DisplayName(string providerName) => ModelVendors.DisplayName(providerName);

  /// <summary>Fallback model id when selection fails or no selector is wired.</summary>
  /// <param name="providerName">The provider id to answer for.</param>
  public static string FallbackModelId(string providerName) => providerName switch
  {
    OpenRouter => "openrouter/auto",
    _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id.")
  };

  /// <summary>The OpenRouter server-side routing pseudo-model id. Its context window
  ///     and capability facts resolve from the fetched catalog like every other model —
  ///     no curated constants.</summary>
  public const string RoutingModelId = "openrouter/auto";
}
