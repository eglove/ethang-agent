namespace eThangAgent.ModelDomain;

/// <summary>Session-scoped model preferences the user can change at runtime through the
///     host's left-menu pickers (model, reasoning effort, and the sampling knobs the
///     provider surface exposes). The root resolver and the child spawner read the
///     current values when building each turn's <see cref="ModelConfig"/>, so changes
///     take effect from the next turn without rebuilding the session. Every knob is
///     nullable: null means "no runtime preference — keep what resolution produced";
///     the overlay in <see cref="ModelPreferencesOverlay"/> applies the non-null ones.</summary>
public sealed class SessionModelPreferences
{
  /// <summary>Current reasoning effort, or null for the provider's own default. Overlaid
  ///     by the resolvers directly (its own path), never by <see cref="ModelPreferencesOverlay"/>.</summary>
  public ReasoningEffort? ReasoningEffort { get; set; }

  /// <summary>The user's live model choice, or null to follow the session's normal
  ///     resolution (intelligent selection, or the provider fallback when no selector is
  ///     wired). Set exclusively through the host's model picker, which validates the id
  ///     against the provider's catalog at pick time, so consumers trust it without
  ///     re-validating. (Choices restored from a persisted per-workspace preference are
  ///     NOT re-validated — a stale id surfaces as a provider error the user re-picks
  ///     away.)</summary>
  public string? ModelId { get; set; }

  /// <summary>Nucleus-sampling cutoff, or null to keep the resolved config's value.</summary>
  public float? TopP { get; set; }

  /// <summary>Top-K sample size, or null to keep the resolved config's value.</summary>
  public int? TopK { get; set; }

  /// <summary>Frequency penalty, or null to keep the resolved config's value.</summary>
  public float? FrequencyPenalty { get; set; }

  /// <summary>Presence penalty, or null to keep the resolved config's value.</summary>
  public float? PresencePenalty { get; set; }

  /// <summary>Repetition penalty, or null to keep the resolved config's value.</summary>
  public float? RepetitionPenalty { get; set; }

  /// <summary>Minimum probability cutoff, or null to keep the resolved config's value.</summary>
  public float? MinP { get; set; }

  /// <summary>Top-A sampling cutoff, or null to keep the resolved config's value.</summary>
  public float? TopA { get; set; }

  /// <summary>Deterministic seed, or null to keep the resolved config's value.</summary>
  public int? Seed { get; set; }

  /// <summary>Output verbosity, or null to keep the resolved config's value.</summary>
  public VerbosityLevel? Verbosity { get; set; }

  /// <summary>Whether parallel tool calls are allowed, or null to keep the resolved
  ///     config's value.</summary>
  public bool? ParallelToolCalls { get; set; }

  /// <summary>Opaque provider-specific settings carried verbatim onto the provider
  ///     request (OpenRouter-only; no file outside the OpenRouter ACL interprets its
  ///     content), or null to keep the resolved config's value. The overlay copies the
  ///     string; it never parses it.</summary>
  public string? ProviderSettings { get; set; }
}
