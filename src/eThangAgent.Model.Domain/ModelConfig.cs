using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public sealed record ModelConfig(
    string ModelId,
    string? Provider,
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
    bool AcceptsImageInput = false)
{
  public static Result<ModelConfig> Create(
      string modelId,
      string? provider,
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
      bool acceptsImageInput = false)
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
        modelId, provider, maxTokens, temperature, contextWindow, effort,
        topP, topK, frequencyPenalty, presencePenalty, repetitionPenalty, minP, topA,
        seed, verbosity, parallelToolCalls, providerSettings, acceptsImageInput);
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
