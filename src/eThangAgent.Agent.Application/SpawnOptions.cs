using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Model-resolution knobs for the spawn start command. Not a DI seam — plain
///     values grouped so the handler's constructor stays small. Members mirror the
///     former loose parameters exactly.</summary>
/// <param name="FallbackModelId">The host-injected fallback model: the resolution
///     chain's last resort when no explicit model, session preference, configured
///     default, or selection succeeds.</param>
/// <param name="Preferences">The session's live model choice (the host's model picker);
///     children follow it ahead of the static configured default.</param>
/// <param name="MaxTokens">Max tokens for the child's model config.</param>
/// <param name="Temperature">Temperature for the child's model config.</param>
public sealed record SpawnOptions(
    string FallbackModelId,
    SessionModelPreferences? Preferences = null,
    int MaxTokens = 4096,
    float Temperature = 0.7f);
