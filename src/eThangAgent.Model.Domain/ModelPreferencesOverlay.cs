namespace eThangAgent.ModelDomain;

/// <summary>Overlays the session's runtime preferences onto a resolved
///     <see cref="ModelConfig"/>: for every knob, a non-null preference replaces the config's
///     value and a null preference preserves it. Only
///     <see cref="SessionModelPreferences.ReasoningEffort"/> is NOT touched — it has its own
///     overlay path (the effort flows through the config constructors' effort parameter) —
///     and capability facts (<see cref="ModelConfig.AcceptsImageInput"/>) are carried through
///     untouched: an overlay rewrites knobs, never capabilities. ProviderSettings is opaque:
///     copied verbatim when non-null, never parsed. A config whose knobs are all overlaid by
///     null preferences passes through value-equal.</summary>
public static class ModelPreferencesOverlay
{
  /// <summary>Applies the non-null preference knobs onto <paramref name="config"/>,
  ///     preserving every knob the preferences leave null.
  ///     <para>THE ROUTING VARIANT IS APPLIED LAST, AFTER the model id is settled, because it
  ///     is expressed as a suffix on that id. See <see cref="ApplyVariant"/>.</para></summary>
  public static ModelConfig Apply(ModelConfig config, SessionModelPreferences? prefs)
  {
    ArgumentNullException.ThrowIfNull(config);
    return prefs is null
        ? config
        : ApplyVariant(OverlayMisc(OverlayPenalties(OverlayCore(config, prefs), prefs), prefs), prefs);
  }

  /// <summary>Writes the session's routing variant onto the config's wire model id.
  ///     <para>THE SUFFIX IS THE WHOLE ROUTING DECISION, so this is where a choice on the
  ///     settings surface becomes the request the provider routes. <see
  ///     cref="ModelVariants.ApplyTo"/> strips any existing suffix first, which is what makes
  ///     switching back to Standard actually remove a previously written <c>:nitro</c> and
  ///     what keeps a re-application from doubling the suffix.</para>
  ///     <para>APPLIED ONLY WHEN A VARIANT WAS COMMITTED: with no preference the resolver's own
  ///     id passes through untouched, so a session that never opened the dropdown is
  ///     byte-identical to before this change.</para>
  ///     <para>IT APPLIES TO THE CONFIG'S MODEL, NOT THE PREFERENCE'S. The two paths that reach
  ///     here resolve their model differently: the live-choice path has one in the preference,
  ///     and the SESSION-PIN path has only the persisted record while the preference carries
  ///     nothing but the variant. Gating on the preference's model would silently drop the
  ///     tradeoff on every resumed session - the suffix would vanish from the wire while the
  ///     status line still named it.</para>
  ///     <para>A CHILD IS THE SAME RULE ON ITS OWN MODEL: the variant is a property of the
  ///     conversation, so a child config naming its own model gets that model's suffix rather
  ///     than the root's.</para></summary>
  private static ModelConfig ApplyVariant(ModelConfig config, SessionModelPreferences prefs)
  {
    if (prefs.ModelVariant is not { } variant)
    {
      return config;
    }

    // A FULLY QUALIFIED CATALOG MODEL IS NEVER REROUTED BY A VARIANT. The catalog already
    // names the exact endpoints the provider serves for such a model (the "variant" family of
    // routing pseudo-models, whose ids must be taken literally), so appending a suffix would
    // ask the provider to route on a model id that does not exist. The same guard protects any
    // future id whose trailing colon means something other than routing.
    return config with { ModelId = ModelVariants.ApplyTo(variant, config.ModelId) };
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
