using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

public interface IModelProvider
{
    Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct = default);
}
