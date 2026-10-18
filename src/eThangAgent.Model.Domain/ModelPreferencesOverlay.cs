namespace eThangAgent.ModelDomain;

/// <summary>Overlays the session's runtime sampling preferences onto a resolved
///     <see cref="ModelConfig"/>: for every knob, a non-null preference replaces the
///     config's value and a null preference preserves it. Only
///     <see cref="ReasoningEffort"/> is NOT touched — it has its own overlay
///     path (the effort flows through the config constructors' effort parameter) -
///     and capability facts (<see cref="ModelConfig.AcceptsImageInput"/>) are
///     carried through untouched: an overlay rewrites knobs, never capabilities.
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
        : OverlayMisc(OverlayPenalties(OverlayCore(config, prefs), prefs), prefs);
  }

  /// <summary>Core sampling knobs: temperature, max tokens, and the top-p/top-k
  ///     cutoffs. A non-null preference replaces the config's value; null keeps it.</summary>
  private static ModelConfig OverlayCore(ModelConfig config, SessionModelPreferences prefs) =>
      config with
      {
        Temperature = prefs.Temperature ?? config.Temperature,
        MaxTokens = prefs.MaxTokens ?? config.MaxTokens,
        TopP = prefs.TopP ?? config.TopP,
        TopK = prefs.TopK ?? config.TopK,
      };

  /// <summary>Penalty and cutoff knobs: frequency, presence, repetition, min-p, and
  ///     top-a. A non-null preference replaces the config's value; null keeps it.</summary>
  private static ModelConfig OverlayPenalties(ModelConfig config, SessionModelPreferences prefs) =>
      config with
      {
        FrequencyPenalty = prefs.FrequencyPenalty ?? config.FrequencyPenalty,
        PresencePenalty = prefs.PresencePenalty ?? config.PresencePenalty,
        RepetitionPenalty = prefs.RepetitionPenalty ?? config.RepetitionPenalty,
        MinP = prefs.MinP ?? config.MinP,
        TopA = prefs.TopA ?? config.TopA,
      };

  /// <summary>Remaining knobs: seed, verbosity, parallel tool calls, and the opaque
  ///     provider settings (copied verbatim, never parsed). A non-null preference
  ///     replaces the config's value; null keeps it.</summary>
  private static ModelConfig OverlayMisc(ModelConfig config, SessionModelPreferences prefs) =>
      config with
      {
        Seed = prefs.Seed ?? config.Seed,
        Verbosity = prefs.Verbosity ?? config.Verbosity,
        ParallelToolCalls = prefs.ParallelToolCalls ?? config.ParallelToolCalls,
        ProviderSettings = prefs.ProviderSettings ?? config.ProviderSettings,
      };
}
