using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public sealed record ModelConfig(
    string ModelId,
    string? Provider,
    int MaxTokens,
    float Temperature,
    int ContextWindow,
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
    string? ProviderSettings = null)
{
  public static Result<ModelConfig> Create(
      string modelId,
      string? provider,
      int maxTokens,
      float temperature,
      int contextWindow,
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
      string? providerSettings = null)
  {
    if (string.IsNullOrWhiteSpace(modelId))
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "Model ID is required."));
    }

    if (maxTokens < 1)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "MaxTokens must be positive."));
    }

    if (temperature is < 0f or > 2f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "Temperature must be between 0 and 2."));
    }

    if (contextWindow < 1)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidContextWindow", "Context window must be positive."));
    }

    if (topP is < 0f or > 1f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "TopP must be between 0 and 1."));
    }

    if (topK < 0)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "TopK must be zero or greater."));
    }

    if (frequencyPenalty is < -2f or > 2f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "FrequencyPenalty must be between -2 and 2."));
    }

    if (presencePenalty is < -2f or > 2f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "PresencePenalty must be between -2 and 2."));
    }

    if (repetitionPenalty is < 0f or > 2f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "RepetitionPenalty must be between 0 and 2."));
    }

    if (minP is < 0f or > 1f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "MinP must be between 0 and 1."));
    }

    if (topA is < 0f or > 1f)
    {
      return Result.Failure<ModelConfig>(new DomainError("InvalidModel", "TopA must be between 0 and 1."));
    }

    ModelConfig config = new(
        modelId, provider, maxTokens, temperature, contextWindow, effort,
        topP, topK, frequencyPenalty, presencePenalty, repetitionPenalty, minP, topA,
        seed, verbosity, parallelToolCalls, providerSettings);
    return Result.Success(config);
  }
}
