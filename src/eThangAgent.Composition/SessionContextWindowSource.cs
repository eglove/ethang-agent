using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Session-level context-window source: OpenRouter's <c>openrouter/auto</c>
///     routing pseudo-model has no catalog row (it routes server-side across upstreams),
///     so it is served from a curated operational constant — the same curated-fact
///     pattern as the z.ai static catalog — and every other model resolves through the
///     real catalog. The constant is a conservative floor: auto-routed sessions account
///     against it, and compaction may fire earlier than strictly necessary, never later
///     than the smallest plausible upstream window.</summary>
public sealed class SessionContextWindowSource(IModelCatalog catalog) : IContextWindowSource
{
  private readonly CatalogContextWindowSource _catalogSource = new(catalog);

  public async Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default)
  {
    return modelId == Providers.RoutingModelId
        ? Providers.RoutingContextWindow
        : await _catalogSource.WindowForAsync(modelId, providerName, ct).ConfigureAwait(false);
  }
}
