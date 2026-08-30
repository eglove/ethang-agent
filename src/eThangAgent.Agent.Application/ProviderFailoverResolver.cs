using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Resolves the root agent's model with failover. When a model+provider fails
///     mid-turn, <see cref="ReSelectExcludingAsync"/> records the exclusion and re-runs
///     selection with the failed pair filtered out. Mirrors <see cref="RootAgentResolver"/>
///     for the normal path but adds exclusion-aware re-selection. Sessions wired without a
///     selector (z.ai picks its model through the host's model picker instead) resolve the
///     fallback on every path — there is nothing to re-select from.</summary>
public sealed class ProviderFailoverResolver(
    RootModelContext context,
    IProviderExclusionStore exclusions,
    IModelSelector? selector = null)
{
  public static readonly TimeSpan DefaultExclusionTtl = TimeSpan.FromMinutes(10);

  private readonly IModelSelector? _selector = selector;
  private readonly IProviderExclusionStore _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
  private readonly RootSessionIdentity? _identity = context.Identity;
  private readonly IAgentStore? _store = context.Store;
  private readonly string _fallbackModelId = context.FallbackModelId ?? throw new ArgumentNullException(nameof(context), "FallbackModelId must not be null.");
  private readonly int _maxTokens = context.MaxTokens;
  private readonly float _temperature = context.Temperature;
  private readonly IContextWindowSource? _windowSource = context.WindowSource;

  public async Task<(ModelConfig Config, string? Notice)> ResolveAsync(
      Conversation conversation, string prompt, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(conversation);

    if (_selector is null)
    {
      return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false), null);
    }

    IReadOnlySet<string> excluded = await _exclusions.GetActiveExclusionsAsync(ct).ConfigureAwait(false);
    Result<ModelSelectionResult> selection = await _selector.SelectAsync(prompt, excluded, ct).ConfigureAwait(false);
    if (!selection.IsSuccess)
    {
      return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false),
          $"Model selection failed: {selection.Error.Message}; using {_fallbackModelId}.");
    }

    string modelId = selection.Value.ModelId;
    string? providerName = selection.Value.ProviderName;

    string? persistNotice = await TryPersistModelAsync(modelId, ct).ConfigureAwait(false);
    string? notice = persistNotice is null ? null : $"Model selected: {persistNotice}";
    return (await MakeAsync(modelId, providerName, ct).ConfigureAwait(false), notice);
  }

  public async Task<(ModelConfig Config, string? Notice)> ReSelectExcludingAsync(
      string failedModelId, string failedProviderName, string taskPrompt, CancellationToken ct = default)
  {
    string failedKey = $"{failedModelId}:{failedProviderName}";
    _ = await _exclusions.AddExclusionAsync(failedKey, DefaultExclusionTtl, ct).ConfigureAwait(false);

    IReadOnlySet<string> existing = await _exclusions.GetActiveExclusionsAsync(ct).ConfigureAwait(false);
    HashSet<string> excluded = [.. existing, failedKey];

    // No selector (z.ai picks its model via the host's model picker): the exclusion is
    // still recorded, but there is nothing to re-select from — the fallback serves the
    // retry turn.
    if (_selector is null)
    {
      return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false),
          $"Model {failedModelId} via {failedProviderName} failed; using {_fallbackModelId}.");
    }

    Result<ModelSelectionResult> selection = await _selector.SelectAsync(taskPrompt, excluded, ct).ConfigureAwait(false);
    if (!selection.IsSuccess)
    {
      return (await MakeAsync(_fallbackModelId, null, ct).ConfigureAwait(false),
          $"Model {failedModelId} via {failedProviderName} failed; all alternatives exhausted, using {_fallbackModelId}.");
    }

    string modelId = selection.Value.ModelId;
    string? providerName = selection.Value.ProviderName;
    _ = await TryPersistModelAsync(modelId, ct).ConfigureAwait(false);

    string notice = $"Model {failedModelId} via {failedProviderName} failed; falling back to {modelId} via {providerName}.";
    return (await MakeAsync(modelId, providerName, ct).ConfigureAwait(false), notice);
  }

  private async Task<ModelConfig> MakeAsync(string modelId, string? providerName, CancellationToken ct)
  {
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(modelId, providerName, ct).ConfigureAwait(false);
    if (window is { } resolved)
    {
      Result<ModelConfig> created = ModelConfig.Create(modelId, providerName, _maxTokens, _temperature, resolved);
      if (created.IsSuccess)
      {
        return created.Value;
      }
    }

    // Unknown window (or invalid create): the fallback serves instead — the same
    // failure-notice chain that a failed selection takes.
    return await MakeFallbackAsync(ct).ConfigureAwait(false);
  }

  private async Task<ModelConfig> MakeFallbackAsync(CancellationToken ct)
  {
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(_fallbackModelId, null, ct).ConfigureAwait(false);
    return window is { } resolved
        ? ModelConfig.Create(_fallbackModelId, null, _maxTokens, _temperature, resolved).Value!
        : throw new InvalidOperationException(
            $"Fallback model '{_fallbackModelId}' has no catalog context window; the resolver cannot serve any turn. "
            + "This is a composition wiring fault: the fallback must be a model the catalog (or a curated constant) knows.");
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
