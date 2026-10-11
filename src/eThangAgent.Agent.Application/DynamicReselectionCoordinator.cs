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
    //    A persist failure keeps the old pin: skip the audit row and the notice
    //    (the pin was not rewritten, so there is no decision to surface).
    string oldModelId = await CurrentPinAsync(ct).ConfigureAwait(false);
    string? oldProviderName = CurrentPinProvider(entries.Value, oldModelId);
    string? persisted = await TryPersistModelAsync(newModelId, ct).ConfigureAwait(false);
    if (persisted is null)
    {
      return (false, null);
    }

    // 6. Audit row (best effort).
    await TryAppendAuditAsync(oldModelId, oldProviderName, newModelId, providerName, decision.MeasuredWeight, qualifiers.Count, ct).ConfigureAwait(false);

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

  /// <summary>Persists the new pin, best effort. Returns the model id on success
  /// (including the no-op case where ModelUsed already equals it), null on any
  /// failure (missing store/identity, read failure, or write failure). Mirrors
  /// <see cref="RootAgentResolver.TryPersistModelAsync"/>.</summary>
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
      return modelId;
    }

    Result<string> updated = await _store.UpdateAsync(record.Value with { ModelUsed = modelId }, ct)
        .ConfigureAwait(false);
    return updated.IsSuccess ? modelId : null;
  }

  /// <summary>The provider name of the currently pinned model, resolved from
  /// the already-fetched catalog entries. Null when no entry matches the
  /// pinned model id (or the pin is the fallback / unassigned).</summary>
  private static string? CurrentPinProvider(IReadOnlyList<ModelProviderEntry> entries, string pinnedModelId)
  {
    if (pinnedModelId is null)
    {
      return null;
    }

    ModelProviderEntry? match = entries.FirstOrDefault(e => e.ModelId == pinnedModelId);
    return match?.ProviderName;
  }

  /// <summary>Appends the audit row, best effort. The row includes old/new
  /// provider names so it alone reconstructs the decision; ' via (prov)' is
  /// omitted when the provider is null (same rule as the notice).</summary>
  private async Task TryAppendAuditAsync(
      string oldModelId, string? oldProviderName,
      string newModelId, string? newProviderName,
      decimal weight, int qualifiers, CancellationToken ct)
  {
    AgentId? rootId = _identity?.Id;
    if (_store is null || rootId is null)
    {
      return;
    }

    string oldPart = oldProviderName is null ? oldModelId : $"{oldModelId} via {oldProviderName}";
    string newPart = newProviderName is null ? newModelId : $"{newModelId} via {newProviderName}";
    string content = string.Create(
        CultureInfo.InvariantCulture,
        $"[reselection] {oldPart} -> {newPart} | w={weight:0.##} | qualifiers={qualifiers}");
    Message audit = new(Role.System, content, DateTimeOffset.UtcNow);
    _ = await _store.AppendMessageAsync(rootId.Value, audit, ct).ConfigureAwait(false);
  }
}
