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
  ///     <para>THE ROUTING VARIANT IS APPLIED LAST, AFTER the model id is settled, because
  ///     part of its wire form is a suffix on that id. See <see cref="ApplyVariant"/>.</para></summary>
  public static ModelConfig Apply(ModelConfig config, SessionModelPreferences? prefs)
  {
    ArgumentNullException.ThrowIfNull(config);
    return prefs is null
        ? config
        : ApplyVariant(OverlayMisc(OverlayPenalties(OverlayCore(config, prefs), prefs), prefs), prefs);
  }

  /// <summary>Carries the session's routing variant onto the config: the VARIANT FIELD plus
  ///     the wire model id, which is only one of the variant's spellings.
  ///     <para>THE FIELD IS THE DECISION AND THE SUFFIX IS ONE OF ITS WIRE FORMS. Three variants
  ///     are expressed as a suffix on the model id; <see cref="ModelVariant.CacheFloor"/> has no
  ///     documented suffix and travels on the request body as a provider order the ACL computes.
  ///     Writing only the suffix would therefore drop a Cache Floor choice silently before the
  ///     request was ever built, so the field is what survives that trip and the ACL reads it to
  ///     decide whether a provider block is needed. <see cref="ModelVariants.ApplyTo"/> is
  ///     <c>BaseOf + Suffix</c>, so it strips any existing suffix first and appends nothing for a
  ///     suffixless variant - which is what makes switching back to Standard actually remove a
  ///     previously written <c>:nitro</c> and what keeps a re-application from doubling it.</para>
  ///     <para>APPLIED ONLY WHEN A VARIANT WAS COMMITTED: with no preference the resolver's own
  ///     id passes through untouched, so a session that never opened the dropdown is
  ///     byte-identical to before this change.</para>
  ///     <para>IT APPLIES TO THE CONFIG'S MODEL, NOT THE PREFERENCE'S. The two paths that reach
  ///     here resolve their model differently: the live-choice path has one in the preference,
  ///     and the SESSION-PIN path has only the persisted record while the preference carries
  ///     nothing but the variant. Gating on the preference's model would silently drop the
  ///     tradeoff on every resumed session - the variant would vanish from the request while the
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

    // THE VARIANT IS CARRIED AS A FIELD; the suffix is only one of its wire spellings.
    // ApplyTo is BaseOf + Suffix, so a variant with no documented suffix (Cache Floor) strips
    // any existing suffix and appends nothing - the base id. Nitro and Floor keep their
    // documented suffixes so the provider's own sorting still applies to them. The ACL reads
    // Variant to decide whether a provider block is needed.
    return config with { ModelId = ModelVariants.ApplyTo(variant, config.ModelId), Variant = variant };
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
