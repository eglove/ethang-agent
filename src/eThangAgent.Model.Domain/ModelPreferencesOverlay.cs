namespace eThangAgent.ModelDomain;

/// <summary>Overlays the session's runtime sampling preferences onto a resolved
///     <see cref="ModelConfig"/>: for every knob, a non-null preference replaces the
///     config's value and a null preference preserves it. Temperature and
///     <see cref="ReasoningEffort"/> are NOT touched — they have their own overlay
///     paths (the effort flows through the config constructors' effort parameter).
///     ProviderSettings is opaque: copied verbatim when non-null, never parsed. A
///     config whose knobs are all overlaid by null preferences passes through
///     value-equal.</summary>
public static class ModelPreferencesOverlay
{
  /// <summary>Applies the non-null preference knobs onto <paramref name="config"/>,
  ///     preserving every knob the preferences leave null.</summary>
  public static ModelConfig Apply(ModelConfig config, SessionModelPreferences? prefs)
  {
    ArgumentNullException.ThrowIfNull(config);
    return prefs is null
        ? config
        : config with
        {
          TopP = prefs.TopP ?? config.TopP,
          TopK = prefs.TopK ?? config.TopK,
          FrequencyPenalty = prefs.FrequencyPenalty ?? config.FrequencyPenalty,
          PresencePenalty = prefs.PresencePenalty ?? config.PresencePenalty,
          RepetitionPenalty = prefs.RepetitionPenalty ?? config.RepetitionPenalty,
          MinP = prefs.MinP ?? config.MinP,
          TopA = prefs.TopA ?? config.TopA,
          Seed = prefs.Seed ?? config.Seed,
          Verbosity = prefs.Verbosity ?? config.Verbosity,
          ParallelToolCalls = prefs.ParallelToolCalls ?? config.ParallelToolCalls,
          ProviderSettings = prefs.ProviderSettings ?? config.ProviderSettings,
        };
  }
}
