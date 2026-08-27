using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Resolves the root agent's model with failover. When a model+provider fails
/// mid-turn, <see cref="ReSelectExcludingAsync"/> records the exclusion and re-runs
/// selection with the failed pair filtered out. Mirrors <see cref="RootAgentResolver"/>
/// for the normal path but adds exclusion-aware re-selection.</summary>
public sealed class ProviderFailoverResolver(
    IModelSelector selector,
    IProviderExclusionStore exclusions,
    RootSessionIdentity? identity,
    IAgentStore? store,
    ModelConfig? explicitModel,
    string fallbackModelId,
    int maxTokens,
    float temperature)
{
  public static readonly TimeSpan DefaultExclusionTtl = TimeSpan.FromMinutes(10);

  private readonly IModelSelector _selector = selector ?? throw new ArgumentNullException(nameof(selector));
  private readonly IProviderExclusionStore _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
  private readonly RootSessionIdentity? _identity = identity;
  private readonly IAgentStore? _store = store;
  private readonly ModelConfig? _explicitModel = explicitModel;
  private readonly string _fallbackModelId = fallbackModelId ?? throw new ArgumentNullException(nameof(fallbackModelId));
  private readonly int _maxTokens = maxTokens;
  private readonly float _temperature = temperature;

  public async Task<(ModelConfig Config, string? Notice)> ResolveAsync(
      Conversation conversation, string prompt, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(conversation);

    if (_explicitModel is not null)
    {
      return (_explicitModel, null);
    }

    IReadOnlySet<string> excluded = await _exclusions.GetActiveExclusionsAsync(ct).ConfigureAwait(false);
    Result<ModelSelectionResult> selection = await _selector.SelectAsync(prompt, excluded, ct).ConfigureAwait(false);
    if (!selection.IsSuccess)
    {
      return (Make(_fallbackModelId, null),
          $"Model selection failed: {selection.Error!.Message}; using {_fallbackModelId}.");
    }

    string modelId = selection.Value!.ModelId;
    string? providerName = selection.Value!.ProviderName;

    string? persistNotice = await TryPersistModelAsync(modelId, ct).ConfigureAwait(false);
    string? notice = persistNotice is null ? null : $"Model selected: {persistNotice}";
    return (Make(modelId, providerName), notice);
  }

  public async Task<(ModelConfig Config, string? Notice)> ReSelectExcludingAsync(
      string failedModelId, string failedProviderName, string taskPrompt, CancellationToken ct = default)
  {
    string failedKey = $"{failedModelId}:{failedProviderName}";
    _ = await _exclusions.AddExclusionAsync(failedKey, DefaultExclusionTtl, ct).ConfigureAwait(false);

    IReadOnlySet<string> existing = await _exclusions.GetActiveExclusionsAsync(ct).ConfigureAwait(false);
    HashSet<string> excluded = [.. existing, failedKey];

    Result<ModelSelectionResult> selection = await _selector.SelectAsync(taskPrompt, excluded, ct).ConfigureAwait(false);
    if (!selection.IsSuccess)
    {
      return (Make(_fallbackModelId, null),
          $"Model {failedModelId} via {failedProviderName} failed; all alternatives exhausted, using {_fallbackModelId}.");
    }

    string modelId = selection.Value!.ModelId;
    string? providerName = selection.Value!.ProviderName;
    _ = await TryPersistModelAsync(modelId, ct).ConfigureAwait(false);

    string notice = $"Model {failedModelId} via {failedProviderName} failed; falling back to {modelId} via {providerName}.";
    return (Make(modelId, providerName), notice);
  }

  private ModelConfig Make(string modelId, string? providerName)
  {
    Result<ModelConfig> created = ModelConfig.Create(modelId, providerName, _maxTokens, _temperature);
    return created.IsSuccess ? created.Value! : Make(_fallbackModelId, null);
  }

  private async Task<string?> TryPersistModelAsync(string modelId, CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return null;
    }

    Result<AgentRecord> record = await _store.GetAsync(rootId.Value, ct).ConfigureAwait(false);
    if (!record.IsSuccess || record.Value is null)
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
