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
/// <para>The pin is never re-decided - it is the record of what served.</para></summary>
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
  /// served - never a substituted one.
  ///     <para>THE COMMITTED PROVIDER RIDES THE RESOLUTION (issue #296, D7). A model chosen for
  ///     its cache economics is only the cheap choice on the provider whose cache prices make
  ///     it so, and the status line names the pair - so the pair must be what SERVES. Passing
  ///     null here, as this resolver did on every resolution, would leave the display claiming
  ///     a provider the request never pinned: the same class of lie as the 2026-10-22 model
  ///     substitution. A null preference still means UNSET (the provider's own routing), which
  ///     is what every session that committed no provider does.</para>
  ///     <para>THE PIN IS NOW A LOCK (issue #297). The wire carries <c>allow_fallbacks: false</c>
  ///     with it (D2); a configured routing that would replace it fails the turn with
  ///     <c>ProviderRoutingConflict</c> instead of silently winning (D3); a provider the catalog
  ///     no longer offers for the chosen model fails with <c>UnknownPinnedProvider</c> (D5)
  ///     through the shared <see cref="PinnedProvider"/> rule; and children inherit the pin
  ///     (D4) — see <c>SubAgentSpawner.ChildConfigAsync</c>, which builds the child's config
  ///     with it.</para></summary>
  public async Task<Result<(ModelConfig Config, string? Notice)>> ResolveAsync(CancellationToken ct = default)
  {
    // 0. The user's live model choice wins over everything static. Runtime
    //    preferences (reasoning effort, sampling knobs) still apply.
    if (_preferences?.ModelId is { } preferred)
    {
      Result<ModelConfig> resolved = await MakeAsync(preferred, _preferences.ProviderName, ct).ConfigureAwait(false);
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
      Result<ModelConfig> resolved = await MakeAsync(pinned, _preferences?.ProviderName, ct).ConfigureAwait(false);
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

  /// <summary>Builds the config for <paramref name="modelId"/>, or fails with a named error.
  ///     An unknown context window is a FAILURE, not a reason to serve another model: the
  ///     window is what the context accountant budgets against, and silently swapping the
  ///     model to obtain one is the substitution this class refuses.
  ///     <para>A PINNED PROVIDER THE CATALOG DOES NOT KNOW IS ITS OWN NAMED FAILURE (issue #297,
  ///     D5). The pin is a hard constraint, so a provider that has disappeared from the catalog
  ///     cannot be served and must not be swapped for one that can: the turn fails naming the
  ///     provider, exactly as an unknown model window does. The check runs BEFORE the window
  ///     lookup so the message names the real cause — a vanished provider would otherwise
  ///     surface as 'no catalog context window', which reads as a model problem and sends the
  ///     user to change the wrong half of the pair. A catalog that is unavailable or whose
  ///     fetch failed is NOT treated as 'the provider vanished': that is an environmental
  ///     fault, and the window lookup below reports it.</para></summary>
  private async Task<Result<ModelConfig>> MakeAsync(string modelId, string? providerName, CancellationToken ct)
  {
    if (await PinnedProviderMissingAsync(modelId, providerName, ct).ConfigureAwait(false) is { } missing)
    {
      return Result.Failure<ModelConfig>(missing);
    }

    int? window = _windowSource is null ? null : await _windowSource.WindowForAsync(modelId, providerName, ct).ConfigureAwait(false);
    if (window is not { } resolved)
    {
      return Result.Failure<ModelConfig>(new DomainError("UnknownModelWindow",
          $"model '{modelId}'{(providerName is null ? string.Empty : $" on provider '{providerName}'")} has no catalog context window "
          + "(the catalog is unavailable or the model is unknown), so the turn cannot be budgeted; "
          + "the harness never substitutes a different model."));
    }

    Result<ModelConfig> created = ModelConfig.Create(
        modelId, providerName, _maxTokens, _temperature, resolved, _preferences?.ReasoningEffort,
        acceptsImageInput: await AcceptsImagesAsync(modelId, providerName, ct).ConfigureAwait(false));
    return created.IsSuccess
        ? Result.Success(ModelPreferencesOverlay.Apply(created.Value, _preferences))
        : created;
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

  /// <summary>The named failure for a pinned provider the catalog does not list, or null when
  ///     the pin is servable (or unset, or the catalog is unavailable). The RULE lives in
  ///     <see cref="PinnedProvider"/> so the child spawner applies exactly the same one; this
  ///     method only supplies the catalog read.
  ///     <para>A FAILED OR EMPTY CATALOG READS AS 'UNVERIFIABLE', never as 'the provider
  ///     vanished': a transient fetch fault must not tell the user their choice is gone.</para>
  ///     </summary>
  private async Task<DomainError?> PinnedProviderMissingAsync(string modelId, string? providerName, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(providerName) || _catalog is null)
    {
      return null;
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    return PinnedProvider.MissingFor(entries.IsSuccess ? entries.Value : null, modelId, providerName);
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
