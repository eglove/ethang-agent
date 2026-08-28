namespace eThangAgent.ModelDomain;

/// <summary>Session-scoped model preferences the user can change at runtime through the
///     host's model picker (model) — and, where exposed, commands (reasoning effort via
///     /effort). The root resolver and the child spawner read the current values when
///     building each turn's <see cref="ModelConfig"/>, so changes take effect from the
///     next turn without rebuilding the session.</summary>
public sealed class SessionModelPreferences
{
  /// <summary>Current reasoning effort, or null for the provider's own default.</summary>
  public ReasoningEffort? ReasoningEffort { get; set; }

  /// <summary>The user's live model choice, or null to follow the session's normal
  ///     resolution (intelligent selection, or the provider fallback when no selector is
  ///     wired). Set exclusively through the host's model picker, which validates the id
  ///     against the provider's catalog at pick time, so consumers trust it without
  ///     re-validating. (Choices restored from a persisted per-workspace preference are
  ///     NOT re-validated — a stale id surfaces as a provider error the user re-picks
  ///     away.)</summary>
  public string? ModelId { get; set; }
}
