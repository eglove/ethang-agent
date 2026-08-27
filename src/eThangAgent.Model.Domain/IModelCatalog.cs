using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Provides the cached OpenRouter model catalog.</summary>
public interface IModelCatalog
{
  Task<Result<IReadOnlyList<ModelCatalogEntry>>> GetAsync(CancellationToken ct = default);
}
