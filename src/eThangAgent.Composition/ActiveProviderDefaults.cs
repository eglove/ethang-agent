namespace eThangAgent.Composition;

/// <summary>Model identities the active provider contributes for code paths that must not
///     hardcode a vendor: the fallback used when selection fails or is unavailable, and the
///     selector model that powers the two-stage selection pipeline's own LLM calls. OpenRouter's
///     "auto" pseudo-model routes server-side; a provider without an equivalent supplies a
///     concrete cheap model instead.</summary>
internal static class ActiveProviderDefaults
{
  internal const string FallbackModelId = "openrouter/auto";

  internal const string SelectorModelId = "openrouter/auto";
}
