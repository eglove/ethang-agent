using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Read-only lookup of a model's context-window size from the provider
/// catalog: the first entry matching the model id where the provider name is null
/// or equal. Null means "no authoritative window" — the model is unknown to the
/// catalog OR the catalog fetch failed; callers take their fallback path either way.</summary>
public interface IContextWindowSource
{
  Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IContextWindowSource"/>: resolves context windows
/// from the cached provider catalog supplied by <see cref="IModelCatalog"/>.</summary>
public sealed class CatalogContextWindowSource(IModelCatalog catalog) : IContextWindowSource
{
  public async Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(modelId);
    Result<IReadOnlyList<ModelProviderEntry>> entries = await catalog.GetAsync(ct).ConfigureAwait(false);
    return entries.Match(
        success => success.FirstOrDefault(e => e.ModelId == modelId
            && (providerName is null || e.ProviderName == providerName))?.ContextLength,
        _ => null);
  }
}
