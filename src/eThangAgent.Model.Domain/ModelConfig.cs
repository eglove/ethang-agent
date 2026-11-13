using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public sealed record ModelConfig(
    string ModelId,
    int MaxTokens,
    float Temperature,
    int? ContextWindow,
    ReasoningEffort? Effort = null,
    float? TopP = null,
    int? TopK = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    float? RepetitionPenalty = null,
    float? MinP = null,
    float? TopA = null,
    int? Seed = null,
    VerbosityLevel? Verbosity = null,
    bool? ParallelToolCalls = null,
    string? ProviderSettings = null,
    bool AcceptsImageInput = false,
    string? PricingProvider = null,
    ModelVariant Variant = ModelVariant.Standard,
     RetentionPolicy Retention = RetentionPolicy.ZeroDataRetention,
     IReadOnlyList<string>? FallbackModelIds = null)
{
  /// <summary>The ordered models to fall through to when <see cref="ModelId"/> cannot serve,
  ///     most preferred first. Never null, and never containing the head.
  ///     <para>THIS IS A ROUTING PREFERENCE, NOT A SELECTION: the head remains the model the turn
  ///     is budgeted against and the id reported when the provider names no serving model. A
  ///     backup serves only when the head cannot, and the request itself carries the order.</para>
  ///     <para>EMPTY MEANS NO FALLBACK, which is what a single-model session resolves to, so the
  ///     request built from it is the one that session always sent.</para></summary>
  public IReadOnlyList<string> FallbackModelIds { get; init; } = FallbackModelIds ?? [];

  /// <summary>Equality over the scalars AND the ordered fallback list.
  ///     <para>THE RECORD'S OWN EQUALITY COMPARES THE LIST BY REFERENCE, so a config rebuilt from
  ///     the same preferences would never equal the one already serving. Two comparisons depend on
  ///     this: <c>RootAgentHolder.Build</c> decides from it whether the serving agent must be
  ///     rebuilt at all, and a rebuild replaces the context accountant that carried the turn's
  ///     usage report.</para>
  ///     <para>A NULL MEMBER COMPARES AS EMPTY, so a config built without a list equals one whose
  ///     list is empty - both state that nothing follows the head.</para></summary>
  public bool Equals(ModelConfig? other)
      => other is not null
          && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
          && IdentityFactsEqual(other)
          && SamplingFactsEqual(other)
          && RoutingFactsEqual(other)
          && (FallbackModelIds ?? []).SequenceEqual(other.FallbackModelIds ?? [], StringComparer.Ordinal);

  /// <summary>The budget and capability facts.</summary>
  private bool IdentityFactsEqual(ModelConfig other)
      => MaxTokens == other.MaxTokens
          && ContextWindow == other.ContextWindow
          && Effort == other.Effort
          && Seed == other.Seed
          && Verbosity == other.Verbosity
          && ParallelToolCalls == other.ParallelToolCalls
          && AcceptsImageInput == other.AcceptsImageInput;

  /// <summary>The sampling knobs. Bit equality is the comparison: these are the values the request
  ///     is built from, so a config whose knob differs by any bit is a different request.</summary>
  private bool SamplingFactsEqual(ModelConfig other)
      => SameBit(Temperature, other.Temperature)
          && SameBit(TopP, other.TopP)
          && SameBit(FrequencyPenalty, other.FrequencyPenalty)
          && SameBit(PresencePenalty, other.PresencePenalty)
          && SameBit(RepetitionPenalty, other.RepetitionPenalty)
          && SameBit(MinP, other.MinP)
          && SameBit(TopA, other.TopA)
          && TopK == other.TopK;

  /// <summary>The routing and provider facts, including the opaque settings string compared
  ///     verbatim.</summary>
  private bool RoutingFactsEqual(ModelConfig other)
      => string.Equals(ProviderSettings, other.ProviderSettings, StringComparison.Ordinal)
          && string.Equals(PricingProvider, other.PricingProvider, StringComparison.Ordinal)
          && Variant == other.Variant
          && Retention == other.Retention;

  /// <summary>True when both nullable floats hold the same value bit for bit, with null equal only
  ///     to null.</summary>
  private static bool SameBit(float? left, float? right)
      => left.HasValue == right.HasValue
          && (!left.HasValue || BitConverter.SingleToInt32Bits(left.Value)
                  == BitConverter.SingleToInt32Bits(right!.Value));

  /// <summary>The hash matching <see cref="Equals(ModelConfig)"/>: the scalars, then each fallback
  ///     in order, so equal configs hash equally.</summary>
  public override int GetHashCode()
  {
    HashCode hash = default;
    hash.Add(ModelId, StringComparer.Ordinal);
    hash.Add(MaxTokens);
    hash.Add(ContextWindow);
    hash.Add(Effort);
    hash.Add(Seed);
    hash.Add(Verbosity);
    hash.Add(ParallelToolCalls);
    hash.Add(AcceptsImageInput);
    hash.Add(BitConverter.SingleToInt32Bits(Temperature));
    hash.Add(TopP is { } topP ? BitConverter.SingleToInt32Bits(topP) : 0);
    hash.Add(FrequencyPenalty is { } frequency ? BitConverter.SingleToInt32Bits(frequency) : 0);
    hash.Add(PresencePenalty is { } presence ? BitConverter.SingleToInt32Bits(presence) : 0);
    hash.Add(RepetitionPenalty is { } repetition ? BitConverter.SingleToInt32Bits(repetition) : 0);
    hash.Add(MinP is { } minP ? BitConverter.SingleToInt32Bits(minP) : 0);
    hash.Add(TopA is { } topA ? BitConverter.SingleToInt32Bits(topA) : 0);
    hash.Add(TopK);
    hash.Add(ProviderSettings, StringComparer.Ordinal);
    hash.Add(PricingProvider, StringComparer.Ordinal);
    hash.Add(Variant);
    hash.Add(Retention);
    foreach (string fallback in FallbackModelIds ?? [])
    {
      hash.Add(fallback, StringComparer.Ordinal);
    }

    return hash.ToHashCode();
  }

  public static Result<ModelConfig> Create(
      string modelId,
      int maxTokens,
      float temperature,
      int? contextWindow,
      ReasoningEffort? effort = null,
      float? topP = null,
      int? topK = null,
      float? frequencyPenalty = null,
      float? presencePenalty = null,
      float? repetitionPenalty = null,
      float? minP = null,
      float? topA = null,
      int? seed = null,
      VerbosityLevel? verbosity = null,
      bool? parallelToolCalls = null,
      string? providerSettings = null,
      bool acceptsImageInput = false,
      string? pricingProvider = null,
      ModelVariant variant = ModelVariant.Standard,
       RetentionPolicy retention = RetentionPolicy.ZeroDataRetention,
      IReadOnlyList<string>? fallbackModelIds = null)
  {
    if (ValidateBasics(modelId, maxTokens) is { } basicsError)
    {
      return Result.Failure<ModelConfig>(basicsError);
    }

    if (ValidateCore(temperature, contextWindow) is { } coreError)
    {
      return Result.Failure<ModelConfig>(coreError);
    }

    if (ValidateSampling(topP, topK, frequencyPenalty, presencePenalty, repetitionPenalty, minP, topA) is { } samplingError)
    {
      return Result.Failure<ModelConfig>(samplingError);
    }

    ModelConfig config = new(
        modelId, maxTokens, temperature, contextWindow, effort,
        topP, topK, frequencyPenalty, presencePenalty, repetitionPenalty, minP, topA,
        seed, verbosity, parallelToolCalls, providerSettings, acceptsImageInput, pricingProvider, variant,
        retention, fallbackModelIds is { Count: > 0 } ? [.. fallbackModelIds] : null);
    return Result.Success(config);
  }

  /// <summary>Identity and token-budget validation: the checks every config must pass
  ///     regardless of sampling shape. Returns the first failure, or null when valid.</summary>
  private static DomainError? ValidateBasics(string modelId, int maxTokens)
  {
    return (modelId, maxTokens) switch
    {
      _ when string.IsNullOrWhiteSpace(modelId) => new DomainError("InvalidModel", "Model ID is required."),
      _ when maxTokens < 1 => new DomainError("InvalidModel", "MaxTokens must be positive."),
      _ => null,
    };
  }

  /// <summary>Core numeric validation: temperature range and the context window.
  ///     Returns the first failure, or null when valid.</summary>
  private static DomainError? ValidateCore(float temperature, int? contextWindow)
  {
    if (!float.IsFinite(temperature) || temperature is < 0f or > 2f)
    {
      return new DomainError("InvalidModel", "Temperature must be between 0 and 2.");
    }

    // Null = the window is not yet known (no catalog resolution yet): allowed and
    // distinct from unbounded. A non-null window stays strictly validated.
    return contextWindow is { } w && w < 1
        ? new DomainError("InvalidContextWindow", "Context window must be positive.")
        : null;
  }

  /// <summary>Sampling-knob validation: the top-K floor plus every float knob's
  ///     documented range. Returns the first failure (in the original check order:
  ///     topP, topK, frequency, presence, repetition, minP, topA), or null when valid.</summary>
  private static DomainError? ValidateSampling(
      float? topP,
      int? topK,
      float? frequencyPenalty,
      float? presencePenalty,
      float? repetitionPenalty,
      float? minP,
      float? topA)
  {
    // float.IsFinite guards: NaN fails every range comparison vacuously, so each
    // float knob's documented range is enforced against it explicitly. The checks
    // run in the original order (topP, then topK, then the remaining knobs) so
    // the first-failure error precedence is unchanged.
    if (OutOfRange(topP, 0f, 1f))
    {
      return new DomainError("InvalidModel", "TopP must be between 0 and 1.");
    }

    if (topK < 0)
    {
      return new DomainError("InvalidModel", "TopK must be zero or greater.");
    }

    List<FloatRangeSpec> specs =
    [
        new(frequencyPenalty, -2f, 2f, "FrequencyPenalty must be between -2 and 2."),
        new(presencePenalty, -2f, 2f, "PresencePenalty must be between -2 and 2."),
        new(repetitionPenalty, 0f, 2f, "RepetitionPenalty must be between 0 and 2."),
        new(minP, 0f, 1f, "MinP must be between 0 and 1."),
        new(topA, 0f, 1f, "TopA must be between 0 and 1."),
    ];
    return specs
        .Where(spec => OutOfRange(spec.Value, spec.Min, spec.Max))
        .Select(spec => new DomainError("InvalidModel", spec.Message))
        .FirstOrDefault();

    // True when the knob is set and falls outside its inclusive range (NaN counts
    // as outside: it fails every comparison vacuously, so the range is enforced
    // against finiteness explicitly).
    static bool OutOfRange(float? value, float min, float max) =>
        value is { } v && (!float.IsFinite(v) || v < min || v > max);
  }

  /// <summary>One float knob's validation inputs: the value (null = no preference),
  ///     its inclusive range, and the failure message naming the knob.</summary>
  private sealed record FloatRangeSpec(float? Value, float Min, float Max, string Message);
}
