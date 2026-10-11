using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Resolves the root agent's model for an upcoming turn. Order: the user's
/// live model choice wins; then the session pin (the persisted ModelUsed, the record
/// of what actually served - a pinned session never re-selects); then smart
/// selection for an unpinned session; then the fallback, which leaves the session
/// unpinned so selection retries next turn. No cadence, no mid-session re-decision.
/// </summary>
public sealed class RootAgentResolver(
    RootModelContext context,
    IModelSelector? selector,
    SessionModelPreferences? preferences = null)
{
  private readonly IModelSelector? _selector = selector;
  private readonly IAgentStore? _store = context.Store;
  private readonly RootSessionIdentity? _identity = context.Identity;
  private readonly string _fallbackModelId = context.FallbackModelId ?? throw new ArgumentNullException(nameof(context), "FallbackModelId must not be null.");
  private readonly int _maxTokens = context.MaxTokens;
  private readonly float _temperature = context.Temperature;
  private readonly SessionModelPreferences? _preferences = preferences;
  private readonly IContextWindowSource? _windowSource = context.WindowSource;
  private readonly IModelCatalog? _catalog = context.Catalog;
  private readonly Func<SmartSelectionOptions>? _smartOptions = context.SmartOptions;

  /// <summary>True when the caller may skip selection entirely - no selector is wired.
  /// Exposed for diagnostics and tests.</summary>
  public bool IsExplicit => _selector is null;

  /// <summary>Resolves the model to serve the upcoming turn; requireVision is
  /// computed by the caller (history images OR upcoming images). Returns the
  /// config and a notice (null when nothing notable happened).</summary>
  public async Task<(ModelConfig Config, string? Notice)> ResolveAsync(bool requireVision, CancellationToken ct = default)
  {
    // 0. The user's live model choice wins over everything static. Runtime
    //    preferences (reasoning effort, sampling knobs) still apply.
    if (_preferences?.ModelId is { } preferred)
    {
      string? preferredNotice = await TryPersistModelAsync(preferred, ct).ConfigureAwait(false);
      return (await MakeAsync(preferred, null, ct).ConfigureAwait(false),
          preferredNotice is null ? null : $"Model selected: {preferredNotice}");
    }

    // 1. No selector wired: the fallback serves every turn.
    if (_selector is null)
    {
      return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false), null);
    }

    // 2. The session pin: a persisted ModelUsed that is not the fallback id is the
    //    record of what actually served - serve it, never re-select.
    string? pinned = await PinnedModelAsync(ct).ConfigureAwait(false);
    if (pinned is not null)
    {
      return (await MakeAsync(pinned, null, ct).ConfigureAwait(false), null);
    }

    // 3. Unpinned: run smart selection. Failure leaves the session unpinned - the
    //    fallback serves this turn and selection retries next turn.
    SmartSelectionOptions options = _smartOptions?.Invoke() ?? SmartSelectionOptions.Default;
    Result<ModelSelectionResult> selection = await _selector.SelectAsync(options, requireVision, ct).ConfigureAwait(false);
    if (!selection.IsSuccess)
    {
      return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false),
          $"Model selection failed: {selection.Error.Message}; using {_fallbackModelId}.");
    }

    string modelId = selection.Value.ModelId;
    string? providerName = selection.Value.ProviderName;

    // 4. Persist the pin (best effort - a store failure must not stop the turn;
    //    it surfaces as a notice instead).
    string? persistNotice = await TryPersistModelAsync(modelId, ct).ConfigureAwait(false);
    string? notice = persistNotice is null ? null : $"Model selected: {persistNotice}";
    return (await MakeAsync(modelId, providerName, ct).ConfigureAwait(false), notice);
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

    // Unknown window (or invalid create): the fallback serves instead - the same
    // failure-notice chain that a failed selection takes.
    return await MakeFallbackAsync(ct).ConfigureAwait(false);
  }

  private async Task<ModelConfig> MakeFallbackAsync(CancellationToken ct)
  {
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(_fallbackModelId, null, ct).ConfigureAwait(false);
    return window is { } resolved
        ? ModelPreferencesOverlay.Apply(
            ModelConfig.Create(_fallbackModelId, null, _maxTokens, _temperature, resolved, _preferences?.ReasoningEffort,
                acceptsImageInput: await AcceptsImagesAsync(_fallbackModelId, null, ct).ConfigureAwait(false)).Value!,
            _preferences)
        : throw new InvalidOperationException(
            $"Fallback model '{_fallbackModelId}' has no catalog context window (catalog unavailable or model unknown); the resolver cannot serve any turn. "
            + "This is a composition wiring fault: the fallback must be a model the catalog knows.");
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
