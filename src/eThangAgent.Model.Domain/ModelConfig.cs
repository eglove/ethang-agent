using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public sealed record ModelConfig(
    string ModelId, string? Provider, int MaxTokens, float Temperature, ReasoningEffort? Effort = null)
{
  public static Result<ModelConfig> Create(
      string modelId, string? provider, int maxTokens, float temperature, ReasoningEffort? effort = null)
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

    ModelConfig config = new(modelId, provider, maxTokens, temperature, effort);
    return Result.Success(config);
  }
}
