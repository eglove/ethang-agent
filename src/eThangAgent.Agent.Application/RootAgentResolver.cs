using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Resolves the root agent's model for an upcoming turn. The session's live
///     model choice (the host's model picker, when set) serves every turn — a recent
///     user choice outranks everything static. Otherwise, the two-stage
///     <see cref="IModelSelector"/> pipeline runs on the turn's prompt at the first turn
///     and every <see cref="Recadence"/> user messages thereafter; on success the root
///     <see cref="AgentRecord.ModelUsed"/> is updated. Selection failures fall back to
///     the host-injected <paramref name="fallbackModelId"/> and are surfaced via the
///     notice string so the user sees that selection failed. Mirrors
///     <c>StartSpawnHandler.ResolveModelAsync</c> for the root path, which previously ran
///     once at startup with a canned prompt.</summary>
public sealed class RootAgentResolver(
    IModelSelector? selector,
    IAgentStore? store,
    RootSessionIdentity? identity,
    string fallbackModelId,
    int maxTokens,
    float temperature,
    SessionModelPreferences? preferences = null)
{
  /// <summary>Reclassify every this many user messages. Turn 1 is the first classification;
  ///     turn <c>Recadence + 1</c> the second, and so on.</summary>
  public const int Recadence = 10;

  private readonly IModelSelector? _selector = selector;
  private readonly IAgentStore? _store = store;
  private readonly RootSessionIdentity? _identity = identity;
  private readonly string _fallbackModelId = fallbackModelId ?? throw new ArgumentNullException(nameof(fallbackModelId));
  private readonly int _maxTokens = maxTokens;
  private readonly float _temperature = temperature;
  private readonly SessionModelPreferences? _preferences = preferences;

  /// <summary>True when the caller may skip selection entirely — no selector is wired.
  ///     Exposed for diagnostics and tests.</summary>
  public bool IsExplicit => _selector is null;

  /// <summary>Resolves the model to serve the upcoming turn. Returns the <see cref="ModelConfig"/>
  ///     and a notice string to surface to the user (null when nothing notable happened — e.g.
  ///     the chosen model is unchanged, or selection ran cleanly with no model change).</summary>
  public async Task<(ModelConfig Config, string? Notice)> ResolveAsync(
      Conversation conversation, string prompt, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(conversation);

    // 0. The user's live model choice wins over everything static — selection,
    //    cadence, and fallback. Runtime preferences (reasoning effort) still apply:
    //    the choice fixes the model identity, not the knobs.
    if (_preferences?.ModelId is { } preferred)
    {
      string? preferredNotice = await TryPersistModelAsync(preferred, ct).ConfigureAwait(false);
      return (Make(preferred),
          preferredNotice is null ? null : $"Model selected: {preferredNotice}");
    }

    // 1. No selector wired: use the fallback for every turn.
    if (_selector is null)
    {
      return (Make(_fallbackModelId, null), null);
    }

    // 2. Decide whether this turn is on the reclassification cadence.
    int priorUserMessages = CountUserMessages(conversation);
    if (!IsCadenceBoundary(priorUserMessages))
    {
      // Off-cadence turns serve the fallback (OpenRouter's openrouter/auto routes
      // server-side); reclassification only runs on the cadence boundaries below.
      return (Make(_fallbackModelId, null), null);
    }

    // 3. Run selection on the actual prompt.
    Result<ModelSelectionResult> selection = await _selector.SelectAsync(prompt, excludedKeys: null, ct).ConfigureAwait(false);
    if (!selection.IsSuccess)
    {
      return (Make(_fallbackModelId, null),
          $"Model selection failed: {selection.Error.Message}; using {_fallbackModelId}.");
    }

    string modelId = selection.Value.ModelId;
    string? providerName = selection.Value.ProviderName;

    // 4. Persist the resolved model onto the root record (best effort — a store failure
    //    must not stop the turn; it surfaces as a notice instead).
    string? persistNotice = await TryPersistModelAsync(modelId, ct).ConfigureAwait(false);

    // Verbatim contract parsed by AgentSessionViewModel.TryExtractModelId: a success
    // notice carries "Model selected: <modelId>". The failure branch above carries
    // "using <fallback>"; both shapes are recognized by the parser.
    string? notice = persistNotice is null
        ? null
        : $"Model selected: {persistNotice}";
    return (Make(modelId, providerName), notice);
  }

  private static int CountUserMessages(Conversation conversation) =>
      conversation.Messages.Count(m => m.Role is Role.User);

  /// <summary>Turn 1 (the first user message) and every <see cref="Recadence"/> user messages
  ///     thereafter: 1, 11, 21, … The user-message count is taken BEFORE the upcoming prompt is
  ///     appended (the resolver runs pre-turn), so the boundary is 0-based here: the first turn
  ///     has 0 prior user messages and reclassifies; the 11th reclassifies when 10 already exist.</summary>
  private static bool IsCadenceBoundary(int priorUserMessages)
      => priorUserMessages % Recadence == 0;

  private ModelConfig Make(string modelId, string? providerName = null)
  {
    Result<ModelConfig> created = ModelConfig.Create(
        modelId, providerName, _maxTokens, _temperature, _preferences?.ReasoningEffort);
    return created.IsSuccess ? created.Value : Make(_fallbackModelId, null);
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
