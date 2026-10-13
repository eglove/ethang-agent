using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Resolves the root agent's model for an upcoming turn. Order: the user's
/// live model choice wins; then the session pin (the persisted ModelUsed, the record
/// of what actually served); then the fallback. The pin is never re-decided — it is
/// the record of what served.</summary>
public sealed class RootAgentResolver(
    RootModelContext context,
    SessionModelPreferences? preferences = null)
{
  private readonly IAgentStore? _store = context.Store;
  private readonly RootSessionIdentity? _identity = context.Identity;
  private readonly string _fallbackModelId = context.FallbackModelId ?? throw new ArgumentNullException(nameof(context), "FallbackModelId must not be null.");
  private readonly int _maxTokens = context.MaxTokens;
  private readonly float _temperature = context.Temperature;
  private readonly SessionModelPreferences? _preferences = preferences;
  private readonly IContextWindowSource? _windowSource = context.WindowSource;
  private readonly IModelCatalog? _catalog = context.Catalog;

  /// <summary>Resolves the model to serve the upcoming turn. Returns the
  /// config and a notice (null when nothing notable happened).</summary>
  public async Task<(ModelConfig Config, string? Notice)> ResolveAsync(CancellationToken ct = default)
  {
    // 0. The user's live model choice wins over everything static. Runtime
    //    preferences (reasoning effort, sampling knobs) still apply.
    if (_preferences?.ModelId is { } preferred)
    {
      string? preferredNotice = await TryPersistModelAsync(preferred, ct).ConfigureAwait(false);
      return (await MakeAsync(preferred, null, ct).ConfigureAwait(false),
          preferredNotice is null ? null : $"Model selected: {preferredNotice}");
    }

    // 1. The session pin: a persisted ModelUsed that is not the fallback id is the
    //    record of what actually served - serve it.
    string? pinned = await PinnedModelAsync(ct).ConfigureAwait(false);
    if (pinned is not null)
    {
      return (await MakeAsync(pinned, null, ct).ConfigureAwait(false), null);
    }

    // 2. The fallback serves an unpinned session.
    return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false), null);
  }

  /// <summary>The persisted ModelUsed when it names a real selection: non-null and
  /// different from the fallback id. The bootstrapper stamps the fallback id on
  /// fresh rows, so fallback == unpinned; null ModelUsed (legacy rows) is unpinned
  /// too. The persisted provider name is not restored - the pair rides the catalog
  /// like every other resolution.</summary>
  private async Task<string?> PinnedModelAsync(CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return null;
    }

    Result<AgentRecord> record = await _store.GetAsync(rootId.Value, ct).ConfigureAwait(false);
    if (!record.IsSuccess)
    {
      return null;
    }

    string? modelUsed = record.Value.ModelUsed;
    return string.IsNullOrEmpty(modelUsed)
        || modelUsed == AgentRecord.UnassignedModel
        || modelUsed == _fallbackModelId
        ? null
        : modelUsed;
  }

  private async Task<ModelConfig> MakeAsync(string modelId, string? providerName, CancellationToken ct)
  {
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(modelId, providerName, ct).ConfigureAwait(false);
    if (window is { } resolved)
    {
      Result<ModelConfig> created = ModelConfig.Create(
          modelId, providerName, _maxTokens, _temperature, resolved, _preferences?.ReasoningEffort,
          acceptsImageInput: await AcceptsImagesAsync(modelId, providerName, ct).ConfigureAwait(false));
      if (created.IsSuccess)
      {
        return ModelPreferencesOverlay.Apply(created.Value, _preferences);
      }
    }

    // Unknown window (or invalid create): the fallback serves instead.
    return await MakeFallbackAsync(ct).ConfigureAwait(false);
  }

  /// <summary>The fallback config. A catalog-resolved window is stamped when the
  /// source knows one; a degraded or fallback-less catalog serves the fallback with
  /// a NULL window - the exact shape of the bootstrap config (ModelConfig treats
  /// null as "not yet known", and the provider resolves the routing pseudo-model
  /// server-side anyway). A null window is a transient degraded state, not a wiring
  /// fault: the next turn's resolution stamps the real window.</summary>
  private async Task<ModelConfig> MakeFallbackAsync(CancellationToken ct)
  {
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(_fallbackModelId, null, ct).ConfigureAwait(false);
    return ModelPreferencesOverlay.Apply(
        ModelConfig.Create(_fallbackModelId, null, _maxTokens, _temperature, window, _preferences?.ReasoningEffort,
            acceptsImageInput: await AcceptsImagesAsync(_fallbackModelId, null, ct).ConfigureAwait(false)).Value!,
        _preferences);
  }

  /// <summary>The model's vision capability from the session catalog: the first entry
  /// matching the model (and provider, when named) that accepts image input. A
  /// missing or failed catalog resolves false - capability is never guessed.</summary>
  private async Task<bool> AcceptsImagesAsync(string modelId, string? providerName, CancellationToken ct)
  {
    if (_catalog is null)
    {
      return false;
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    return entries.IsSuccess && entries.Value.Any(e =>
        e.ModelId == modelId
        && (providerName is null || e.ProviderName == providerName)
        && e.SupportsVision);
  }

  private async Task<string?> TryPersistModelAsync(string modelId, CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return null;
    }

    Result<AgentRecord> record = await _store.GetAsync(rootId.Value, ct).ConfigureAwait(false);
    if (!record.IsSuccess)
    {
      return null;
    }

    if (record.Value.ModelUsed == modelId)
    {
      return null;
    }

    Result<string> updated = await _store.UpdateAsync(record.Value with { ModelUsed = modelId }, ct)
        .ConfigureAwait(false);
    return updated.IsSuccess ? modelId : null;
  }
}
