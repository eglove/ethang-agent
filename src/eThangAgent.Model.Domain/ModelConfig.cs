using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public sealed record ModelConfig(string ModelId, int MaxTokens, float Temperature)
{
    public static Result<ModelConfig> Create(string modelId, int maxTokens, float temperature)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return Result<ModelConfig>.Failure(new Error("InvalidModel", "Model ID is required."));
        if (maxTokens < 1)
            return Result<ModelConfig>.Failure(new Error("InvalidModel", "MaxTokens must be positive."));
        if (temperature < 0f || temperature > 2f)
            return Result<ModelConfig>.Failure(new Error("InvalidModel", "Temperature must be between 0 and 2."));
        return Result<ModelConfig>.Success(new ModelConfig(modelId, maxTokens, temperature));
    }
}
