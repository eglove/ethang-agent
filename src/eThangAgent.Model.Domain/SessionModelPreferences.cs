using System.Text.Json.Serialization;

namespace eThangAgent.ModelDomain;

/// <summary>Session-scoped model preferences the user can change at runtime through the
///     host's left-menu pickers (model, routing variant, reasoning effort, and the sampling
///     knobs the provider surface exposes). The root resolver and the child spawner read the
///     current values when building each turn's <see cref="ModelConfig"/>, so changes take
///     effect from the next turn without rebuilding the session. Every knob is nullable: null
///     means "no runtime preference — keep what resolution produced"; the overlay in
///     <see cref="ModelPreferencesOverlay"/> applies the non-null ones.</summary>
public sealed class SessionModelPreferences
{
  /// <summary>Current reasoning effort, or null for the provider's own default. Overlaid
  ///     by the resolvers directly (its own path), never by <see cref="ModelPreferencesOverlay"/>.</summary>
  public ReasoningEffort? ReasoningEffort { get; set; }

  /// <summary>The user's live ordered model list, or null to follow the session's normal
  ///     resolution. Entry 1 is the model that runs and the remaining entries are its ordered
  ///     fallbacks. Stored as BASE catalog ids; the routing variant is carried separately in
  ///     <see cref="ModelVariant"/> and combined onto the wire by the overlay. Set exclusively
  ///     through the host's model picker, which validates the ids against the provider's catalog
  ///     at pick time, so consumers trust it without re-validating.
  ///     (Choices restored from a persisted per-workspace preference are NOT re-validated — a
  ///     stale id surfaces as a provider error the user re-picks away.)
  ///     <para>ASSIGNING THIS REPLACES <see cref="Entries"/> WITH VENDOR-UNSET ENTRIES, so a
  ///     caller that names only ids can never leave a stale vendor behind from an earlier
  ///     assignment. Read the vendors from <see cref="Entries"/>.</para></summary>
  [JsonIgnore]
  public IReadOnlyList<string>? ModelIds
  {
    get => _modelIds;
    set
    {
      _modelIds = value;
      _entries = value is null ? null : [.. value.Select(id => new ModelSelectionEntry(id))];
    }
  }

  /// <summary>The user's live ordered model list WITH the vendor that serves each entry, or null
  ///     to follow the session's normal resolution.
  ///     <para>THIS IS THE LIST A TURN WALKS: the same model id under two vendors is two rows,
  ///     and only this member can say which one an entry means. <see cref="ModelIds"/> is its
  ///     projection, so the two never disagree.</para>
  ///     <para>ASSIGNING THIS REPLACES <see cref="ModelIds"/> WITH THE PROJECTED IDS, so every
  ///     consumer that reads only ids keeps working.</para></summary>
  [JsonIgnore]
  public IReadOnlyList<ModelSelectionEntry>? Entries
  {
    get => _entries;
    set
    {
      _entries = value;
      _modelIds = value is null ? null : [.. value.Select(entry => entry.ModelId)];
    }
  }

  private IReadOnlyList<string>? _modelIds;
  private IReadOnlyList<ModelSelectionEntry>? _entries;

  /// <summary>The carrier's <c>ModelIds</c> member: written only when NO entry names a vendor, so
  ///     a carrier produced by a selection that states nothing about vendors is the shape earlier
  ///     builds wrote and can still read.</summary>
  [JsonPropertyName("ModelIds")]
  [JsonInclude]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  internal IReadOnlyList<string>? WireModelIds
  {
    get => AnyVendorNamed ? null : _modelIds;
    set
    {
      _modelIds = value;
      _entries = value is null ? null : [.. value.Select(id => new ModelSelectionEntry(id))];
    }
  }

  /// <summary>The carrier's <c>Entries</c> member: written only when an entry names a vendor.
  ///     </summary>
  [JsonPropertyName("Entries")]
  [JsonInclude]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  internal IReadOnlyList<ModelSelectionEntry>? WireEntries
  {
    get => AnyVendorNamed ? _entries : null;
    set
    {
      _entries = value;
      _modelIds = value is null ? null : [.. value.Select(entry => entry.ModelId)];
    }
  }

  /// <summary>Whether any entry names a vendor, which is what decides which of the two carrier
  ///     members is written.</summary>
  private bool AnyVendorNamed => _entries is not null && _entries.Any(entry => entry.HasVendor);

  /// <summary>The routing variant the session's model should be served under, or null when no
  ///     variant was committed (which resolves to <see cref="ModelVariant.Standard"/>).
  ///     <para>THIS REPLACED THE PROVIDER PIN. Choosing the provider and the endpoint was a
  ///     decision the harness could already rank from measured price, latency, and cache
  ///     economics; what it cannot infer is the tradeoff the user wants, which OpenRouter
  ///     publishes as three routing variants. The variant is a SUFFIX ON THE MODEL ID, so the
  ///     whole selection is one value on the wire.</para>
  ///     <para>Null means UNSET, never "standard was chosen": a session that never committed a
  ///     variant keeps the provider's own routing, which is what every pre-existing session
  ///     does.</para></summary>
  public ModelVariant? ModelVariant { get; set; }

  /// <summary>Sampling temperature, or null to keep the resolved config's value.</summary>
  public float? Temperature { get; set; }

  /// <summary>Maximum output tokens, or null to keep the resolved config's value.</summary>
  public int? MaxTokens { get; set; }

  /// <summary>Nucleus-sampling cutoff, or null to keep the resolved config's value.</summary>
  public float? TopP { get; set; }

  /// <summary>Top-K sample size, or null to keep the resolved config's value.</summary>
  public int? TopK { get; set; }

  /// <summary>Frequency penalty, or null to keep the resolved config's value.</summary>
  public float? FrequencyPenalty { get; set; }

  /// <summary>Presence penalty, or null to keep the resolved config's value.</summary>
  public float? PresencePenalty { get; set; }

  /// <summary>Repetition penalty, or null to keep the resolved config's value.</summary>
  public float? RepetitionPenalty { get; set; }

  /// <summary>Minimum probability cutoff, or null to keep the resolved config's value.</summary>
  public float? MinP { get; set; }

  /// <summary>Top-A sampling cutoff, or null to keep the resolved config's value.</summary>
  public float? TopA { get; set; }

  /// <summary>Deterministic seed, or null to keep the resolved config's value.</summary>
  public int? Seed { get; set; }

  /// <summary>Output verbosity, or null to keep the resolved config's value.</summary>
  public VerbosityLevel? Verbosity { get; set; }

  /// <summary>Whether parallel tool calls are allowed, or null to keep the resolved
  ///     config's value.</summary>
  public bool? ParallelToolCalls { get; set; }

  /// <summary>Opaque provider-specific settings carried verbatim onto the provider
  ///     request (OpenRouter-only; no file outside the OpenRouter ACL interprets its
  ///     content), or null to keep the resolved config's value. The overlay copies the
  ///     string; it never parses it.</summary>
  public string? ProviderSettings { get; set; }

  /// <summary>The retention tier the session routes under, or null to keep the resolved
  ///     config's value.
  ///     <para>NULL MEANS KEEP THE CONFIG'S TIER, and the overlay is where that is decided: a
  ///     session that has never stated a preference keeps the tier resolution produced, which
  ///     is the restrictive default. The default is supplied by the config's own parameter
  ///     default rather than here, so a resolved tier is never overwritten by silence.</para>
  ///     </summary>
  public RetentionPolicy? Retention { get; set; }

  /// <summary>Replaces every knob with <paramref name="source"/>'s values. Used where a
  ///     live preferences instance must ADOPT a set that travelled from another process
  ///     (the host applying the app's choice): consumers hold this instance, so the values
  ///     must land on it rather than on a replacement object.</summary>
  public void CopyFrom(SessionModelPreferences source)
  {
    ArgumentNullException.ThrowIfNull(source);
    ReasoningEffort = source.ReasoningEffort;
    Entries = source.Entries;
    ModelVariant = source.ModelVariant;
    Temperature = source.Temperature;
    MaxTokens = source.MaxTokens;
    TopP = source.TopP;
    TopK = source.TopK;
    FrequencyPenalty = source.FrequencyPenalty;
    PresencePenalty = source.PresencePenalty;
    RepetitionPenalty = source.RepetitionPenalty;
    MinP = source.MinP;
    TopA = source.TopA;
    Seed = source.Seed;
    Verbosity = source.Verbosity;
    ParallelToolCalls = source.ParallelToolCalls;
    ProviderSettings = source.ProviderSettings;
    Retention = source.Retention;
  }
}
