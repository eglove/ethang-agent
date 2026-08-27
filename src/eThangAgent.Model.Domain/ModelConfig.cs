using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public sealed record ModelConfig(string ModelId, int MaxTokens, float Temperature)
{
  public static Result<ModelConfig> Create(string modelId, int maxTokens, float temperature)
  {
    return string.IsNullOrWhiteSpace(modelId)
      ? Result.Failure<ModelConfig>(new DomainError("InvalidModel", "Model ID is required."))
      : maxTokens < 1
      ? Result.Failure<ModelConfig>(new DomainError("InvalidModel", "MaxTokens must be positive."))
      : temperature is < 0f or > 2f
      ? Result.Failure<ModelConfig>(new DomainError("InvalidModel", "Temperature must be between 0 and 2."))
      : Result.Success(new ModelConfig(modelId, maxTokens, temperature));
  }
}
