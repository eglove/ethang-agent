namespace eThangAgent.ModelDomain;

/// <summary>Session-scoped model preferences the user can change at runtime through
///     commands (today: reasoning effort via /effort). The root resolver and the child
///     spawner read the current value when building each turn's <see cref="ModelConfig"/>,
///     so changes take effect from the next turn without rebuilding the session.</summary>
public sealed class SessionModelPreferences
{
  /// <summary>Current reasoning effort, or null for the provider's own default.</summary>
  public ReasoningEffort? ReasoningEffort { get; set; }
}
