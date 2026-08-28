namespace eThangAgent.ModelDomain;

/// <summary>Session-scoped model preferences the user can change at runtime through
///     commands (today: reasoning effort via /effort, model via /model). The root resolver
///     and the child spawner read the current values when building each turn's
///     <see cref="ModelConfig"/>, so changes take effect from the next turn without
///     rebuilding the session.</summary>
public sealed class SessionModelPreferences
{
  /// <summary>Current reasoning effort, or null for the provider's own default.</summary>
  public ReasoningEffort? ReasoningEffort { get; set; }

  /// <summary>The user's live model choice, or null to follow the session's normal
  ///     resolution (pin or selection). Only wired when the provider exposes a
  ///     user-selectable lineup (z.ai); set exclusively through the validated /model
  ///     command, so consumers trust it without re-validating.</summary>
  public string? ModelId { get; set; }
}
