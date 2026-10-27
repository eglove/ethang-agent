using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Read-only lookup of a model's context-window size from the provider catalog. Null
///     means "no authoritative window" — the model is unknown to the catalog OR the catalog
///     fetch failed; callers take their fallback path either way.
///     <para>THE KEY IS THE BASE MODEL, and there is deliberately no provider parameter: the
///     catalog carries one row per routable endpoint, all sharing the model id, and every
///     endpoint of one model serves the same window. A routing variant (<c>:nitro</c> /
///     <c>:floor</c>) is a SUFFIX on that id and names a routing intent, not a different model,
///     so it is stripped before the lookup.</para></summary>
public interface IContextWindowSource
{
  Task<int?> WindowForAsync(string modelId, CancellationToken ct = default);
}

/// <summary>Catalog-backed <see cref="IContextWindowSource"/>: resolves context windows
///     from the cached provider catalog supplied by <see cref="IModelCatalog"/>.
///     <para>THE WIDEST WINDOW OF THE MODEL'S ENDPOINTS WINS. The endpoints of one model can
///     report different windows, and the budget must fit the endpoint that actually serves —
///     which is not knowable before the request. Taking the widest is the honest reading for a
///     router: it is the window the user is entitled to when routing picks the best endpoint,
///     and it never under-budgets a turn whose context would have fit.</para></summary>
public sealed class CatalogContextWindowSource(IModelCatalog catalog) : IContextWindowSource
{
  public async Task<int?> WindowForAsync(string modelId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
    string baseModel = ModelVariants.BaseOf(modelId);
    Result<IReadOnlyList<ModelProviderEntry>> entries = await catalog.GetAsync(ct).ConfigureAwait(false);
    return entries.Match(
        success => success.Where(e => e.ModelId == baseModel).Max(e => (int?)e.ContextLength),
        _ => null);
  }
}
