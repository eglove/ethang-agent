using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Provides a provider's model catalog entries.</summary>
public interface IModelCatalog
{
  Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default);
}
