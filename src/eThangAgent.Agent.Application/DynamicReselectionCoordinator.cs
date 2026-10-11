using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Applies a measured-mix policy crossing at the turn-start safe point: when
/// the session's measured output/input token mix has crossed a price break-even, it
/// re-selects the cheapest pair at the measured weight, rewrites the persisted pin,
/// emits the notice, and appends an audit row. Degradation is silence - every failure
/// branch keeps the pin and returns (false, null); no exception escapes.</summary>
public sealed class DynamicReselectionCoordinator(
    IModelCatalog catalog,
    Func<SmartSelectionOptions> smartOptions,
    SessionModelPreferences? preferences,
    MeasuredMixPolicy policy,
    IAgentStore? store,
    RootSessionIdentity? identity,
    string fallbackModelId)
{
  private readonly IModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly Func<SmartSelectionOptions> _smartOptions = smartOptions ?? throw new ArgumentNullException(nameof(smartOptions));
  private readonly SessionModelPreferences? _preferences = preferences;
  private readonly MeasuredMixPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
  private readonly IAgentStore? _store = store;
  private readonly RootSessionIdentity? _identity = identity;
  private readonly string _fallbackModelId = fallbackModelId ?? throw new ArgumentNullException(nameof(fallbackModelId));

  /// <summary>Evaluates the policy at the turn-start safe point and, on a crossing,
  /// rewrites the pin. Returns whether a reselection was applied and the notice to
  /// surface (null when nothing notable happened).</summary>
  public async Task<(bool Applied, string? Notice)> ApplyAsync(bool requireVision, CancellationToken ct = default)
  {
    // 1. Gates: an explicit user pick or a non-price sort means the measured mix
    //    cannot steer selection. Neither touches the catalog.
    if (_preferences?.ModelId is not null)
    {
      return (false, null);
    }

    SmartSelectionOptions options = _smartOptions();
    if (options.Sort != SmartSortCriterion.Price)
    {
      return (false, null);
    }

    // 2. The catalog. A failure keeps the pin.
    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    if (!entries.IsSuccess)
    {
      return (false, null);
    }

    // 3. The policy decision over exactly the qualifying set the price sort ranks.
    IReadOnlyList<ModelProviderEntry> qualifiers = SmartModelSelectorCore.QualifyingEntries(entries.Value, options, requireVision);
    ReselectionConsidered? decision = _policy.Evaluate(qualifiers);
    if (decision is null)
    {
      return (false, null);
    }

    // 4. Re-select at the measured weight. A failure (incl. NoMatchingModels) keeps
    //    the pin.
    Result<ModelSelectionResult> selection = SmartModelSelectorCore.Select(entries.Value, options, requireVision, _policy.MeasuredWeight);
    if (!selection.IsSuccess)
    {
      return (false, null);
    }

    string newModelId = selection.Value.ModelId;
    string? providerName = selection.Value.ProviderName;

    // 5. Resolve the CURRENT pinned pair BEFORE the overwrite, then persist.
    string oldModelId = await CurrentPinAsync(ct).ConfigureAwait(false);
    await TryPersistModelAsync(newModelId, ct).ConfigureAwait(false);

    // 6. Audit row (best effort).
    await TryAppendAuditAsync(oldModelId, newModelId, decision.MeasuredWeight, qualifiers.Count, ct).ConfigureAwait(false);

    // 7. Notice.
    string notice = providerName is null
        ? $"Model selected: {newModelId}"
        : $"Model selected: {newModelId} via {providerName}";
    return (true, notice);
  }

  /// <summary>The currently pinned model id: the record's ModelUsed when it names a
  /// real selection, else the fallback. A missing store/identity or a failed read
  /// resolves the fallback.</summary>
  private async Task<string> CurrentPinAsync(CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return _fallbackModelId;
    }

    Result<AgentRecord> record = await _store.GetAsync(rootId.Value, ct).ConfigureAwait(false);
    if (!record.IsSuccess)
    {
      return _fallbackModelId;
    }

    string? modelUsed = record.Value.ModelUsed;
    return string.IsNullOrEmpty(modelUsed)
        || modelUsed == AgentRecord.UnassignedModel
        ? _fallbackModelId
        : modelUsed;
  }

  /// <summary>Persists the new pin, best effort. The write is skipped when ModelUsed
  /// already equals the new id (no-op rewrite).</summary>
  private async Task TryPersistModelAsync(string modelId, CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return;
    }

    Result<AgentRecord> record = await _store.GetAsync(rootId.Value, ct).ConfigureAwait(false);
    if (!record.IsSuccess || record.Value.ModelUsed == modelId)
    {
      return;
    }

    _ = await _store.UpdateAsync(record.Value with { ModelUsed = modelId }, ct).ConfigureAwait(false);
  }

  /// <summary>Appends the audit row, best effort.</summary>
  private async Task TryAppendAuditAsync(string oldModelId, string newModelId, decimal weight, int qualifiers, CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return;
    }

    string content = string.Create(
        CultureInfo.InvariantCulture,
        $"[reselection] {oldModelId} -> {newModelId} | w={weight:0.##} | qualifiers={qualifiers}");
    Message audit = new(Role.System, content, DateTimeOffset.UtcNow);
    _ = await _store.AppendMessageAsync(rootId.Value, audit, ct).ConfigureAwait(false);
  }
}
