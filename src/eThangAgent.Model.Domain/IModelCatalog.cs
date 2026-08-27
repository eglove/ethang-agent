using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Provides the cached OpenRouter model+provider catalog.</summary>
public interface IModelCatalog
{
  Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default);
}
