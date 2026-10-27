using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Resolves the root agent's model for an upcoming turn: the user's live model
/// choice wins, then the session pin (the persisted ModelUsed, the record of what actually
/// served). There is NO fallback substitution - a session with no usable selection FAILS
/// the turn with a named error.
/// <para>WHY NO FALLBACK (2026-10-22): the resolver used to substitute the provider's
/// routing pseudo-model whenever the chosen model's context window was unknown, and to
/// serve it for any unpinned session. Both substitutions were SILENT: the user picked
/// <c>minimax/minimax-m3.1-flash:free</c>, the app displayed that pick, and the turns were
/// served by <c>openrouter/auto</c> - a different model, a different price, a different
/// answer. A model substitution the user did not ask for is a lie about what ran, so the
/// resolver reports the fault instead: an unknown window is <c>UnknownModelWindow</c>, and
/// nothing selected is <c>NoModelSelected</c>.</para>
/// <para>The pin is never re-decided - it is the record of what served.</para>
/// <para>THE ROUTING VARIANT RIDES THE RESOLUTION (it replaced the provider pin). The model
/// id is resolved at the BASE catalog key, then the session's committed variant is written as
/// a suffix by <see cref="ModelPreferencesOverlay"/> - so the config the provider sends
/// carries the routing intent while the catalog lookups here still see the real model.
/// Because the variant sorts the endpoint pool rather than narrowing it, there is no
/// vanished-provider failure to raise: a model's every endpoint stays eligible under all
/// three variants.</para></summary>
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

  /// <summary>Resolves the model to serve the upcoming turn. Returns the config and a
  /// notice (null when nothing notable happened), or a named failure when no model can be
  /// served - never a substituted one.</summary>
  public async Task<Result<(ModelConfig Config, string? Notice)>> ResolveAsync(CancellationToken ct = default)
  {
    // 0. The user's live model choice wins over everything static. Runtime preferences
    //    (reasoning effort, sampling knobs, and the routing variant) still apply.
    if (_preferences?.ModelId is { } preferred)
    {
      Result<ModelConfig> resolved = await MakeAsync(preferred, ct).ConfigureAwait(false);
      if (!resolved.IsSuccess)
      {
        return Result.Failure<(ModelConfig, string?)>(resolved.Error);
      }

      string? preferredNotice = await TryPersistModelAsync(preferred, ct).ConfigureAwait(false);
      return Result.Success<(ModelConfig, string?)>(
          (resolved.Value, preferredNotice is null ? null : $"Model selected: {preferredNotice}"));
    }

    // 1. The session pin: a persisted ModelUsed that is not the fallback id is the
    //    record of what actually served - serve it.
    string? pinned = await PinnedModelAsync(ct).ConfigureAwait(false);
    if (pinned is not null)
    {
      Result<ModelConfig> resolved = await MakeAsync(pinned, ct).ConfigureAwait(false);
      return resolved.IsSuccess
          ? Result.Success<(ModelConfig, string?)>((resolved.Value, null))
          : Result.Failure<(ModelConfig, string?)>(resolved.Error);
    }

    // 2. Nothing is selected. Serving the routing pseudo-model here is exactly the silent
    //    substitution this resolver exists to refuse: the user must pick a model.
    return Result.Failure<(ModelConfig, string?)>(new DomainError("NoModelSelected",
        "no model is selected for this session, and the harness never substitutes one; "
        + "choose a model in Model Settings and send the turn again."));
  }

  /// <summary>The persisted ModelUsed when it names a real selection: non-null and different
  ///     from the fallback id. The bootstrapper stamps the fallback id on fresh rows, so
  ///     fallback == unpinned; null ModelUsed (legacy rows) is unpinned too.
  ///     <para>A STORED ID MAY CARRY A VARIANT SUFFIX: it is the record of what served, and a
  ///     session served under <c>:nitro</c> recorded exactly that. It is normalized to the
  ///     BASE model here so the catalog lookups below find the real model.</para></summary>
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
        : ModelVariants.BaseOf(modelUsed);
  }

  /// <summary>Builds the config for <paramref name="modelId"/>, or fails with a named error.
  ///     An unknown context window is a FAILURE, not a reason to serve another model: the
  ///     window is what the context accountant budgets against, and silently swapping the model
  ///     to obtain one is the substitution this class refuses.
  ///     <para>The id is the BASE model (a catalog key). The routing variant is not consulted
  ///     here at all: it does not change the model, its window, or its vision capability - it
  ///     only sorts the endpoints that may serve it - so it is applied once, by the overlay, to
  ///     the id that goes on the wire.</para></summary>
  private async Task<Result<ModelConfig>> MakeAsync(string modelId, CancellationToken ct)
  {
    string baseModel = ModelVariants.BaseOf(modelId);
    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(baseModel, ct).ConfigureAwait(false);
    if (window is not { } resolved)
    {
      return Result.Failure<ModelConfig>(new DomainError("UnknownModelWindow",
          $"model '{baseModel}' has no catalog context window (the catalog is unavailable or the "
          + "model is unknown), so the turn cannot be budgeted; the harness never substitutes a "
          + "different model."));
    }

    Result<ModelConfig> created = ModelConfig.Create(
        baseModel, _maxTokens, _temperature, resolved, _preferences?.ReasoningEffort,
        acceptsImageInput: await AcceptsImagesAsync(baseModel, ct).ConfigureAwait(false));
    return created.IsSuccess
        ? Result.Success(ModelPreferencesOverlay.Apply(created.Value, _preferences))
        : created;
  }

  /// <summary>The model's vision capability from the session catalog: any entry matching the
  ///     base model that accepts image input. A missing or failed catalog resolves false -
  ///     capability is never guessed, and one capable endpoint of a router is enough.</summary>
  private async Task<bool> AcceptsImagesAsync(string baseModel, CancellationToken ct)
  {
    if (_catalog is null)
    {
      return false;
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    return entries.IsSuccess && entries.Value.Any(e => e.ModelId == baseModel && e.SupportsVision);
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
