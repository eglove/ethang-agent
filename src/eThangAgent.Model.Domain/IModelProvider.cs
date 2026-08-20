using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain;

public interface IModelProvider
{
    Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct = default);
}
