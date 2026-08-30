using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition;

/// <summary>Resolves the summarizer model for one session's compactor: the
///     per-workspace preference (compaction_model:{provider}:{workspaceRoot}) when set
///     and known to the catalog, otherwise the cheapest tool-capable catalog entry.
///     Returns null when nothing resolvable — the compactor falls back to the serving
///     model for that one summary (documented hint semantics).</summary>
public sealed class CompactionModelResolver(IAppPreferenceStore preferences, IModelCatalog catalog,
    string providerName, string workspaceRoot)
{
  /// <summary>The preference key for a (provider, workspace) pair: verbatim contract shared
  ///     with the Desktop settings surface.</summary>
  public static string PreferenceKey(string providerName, string workspaceRoot)
      => $"compaction_model:{providerName}:{workspaceRoot}";

  public async Task<ModelConfig?> ResolveAsync(int maxTokens, float temperature, CancellationToken ct = default)
  {
    string? preferredId = await preferences.GetAsync(PreferenceKey(providerName, workspaceRoot), ct).ConfigureAwait(false);

    Result<IReadOnlyList<ModelProviderEntry>> entries = await catalog.GetAsync(ct).ConfigureAwait(false);
    if (!entries.IsSuccess || entries.Value.Count == 0)
    {
      return null;
    }

    ModelProviderEntry? chosen = null;
    if (!string.IsNullOrWhiteSpace(preferredId))
    {
      chosen = entries.Value.FirstOrDefault(e => e.ModelId == preferredId && e.ProviderName == providerName)
          ?? entries.Value.FirstOrDefault(e => e.ModelId == preferredId);
    }

    chosen ??= entries.Value
        .Where(e => e.SupportsToolUse)
        .OrderBy(e => e.PromptPricePerToken)
        .FirstOrDefault()
        ?? entries.Value[0];

    Result<ModelConfig> created = ModelConfig.Create(chosen.ModelId, chosen.ProviderName,
        maxTokens, temperature, chosen.ContextLength);
    return created.IsSuccess ? created.Value : null;
  }
}
