using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public interface IModelProvider
{
    Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default);
}
