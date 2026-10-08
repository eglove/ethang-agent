namespace eThangAgent.OpenRouter.ACL;

/// <summary>Holds the OpenRouter management key for a session's management client,
///     readable LIVE at dispatch: a key saved mid-session (Settings, API Keys) is
///     pushed onto the carrier and the next openrouter_management call uses it —
///     the frozen OpenRouterConfiguration snapshot forced a session restart to pick
///     a key up (2026-10-08). Seeded from settings at container build; the host
///     pushes updates. Null means unconfigured (the typed refusal still applies).</summary>
public sealed class OpenRouterManagementKeyCarrier
{
  /// <summary>The live management key, or null when unconfigured.</summary>
  public string? Current { get; set; }
}
