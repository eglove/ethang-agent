using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Everything one Model Settings save carries: the live
///     <see cref="SessionModelPreferences"/> (already mutated by the save), the
///     dialog's twelve knob text fields verbatim (the persisted
///     <c>sampling_prefs</c> payload — all twelve; the typed members are the
///     live-wire path, the map is the restore + prefill persistence), and the
///     serialized OpenRouter request settings
///     (null when the section is hidden or everything is default — the persisted
///     <c>provider_settings</c> payload). The shell persists the two text
///     payloads per workspace + provider; the typed values are already on the
///     session's preferences.</summary>
/// <param name="Preferences">The (already-mutated) live session preferences.</param>
/// <param name="KnobTexts">The dialog's knob text fields, trimmed, keyed by the
///     same constants as <see cref="ModelSettingsViewModel.Knobs"/>.</param>
/// <param name="ProviderSettingsJson">The serialized OpenRouter section, or null
///     when hidden or all-default.</param>
internal sealed record ModelSettingsSnapshot(
  SessionModelPreferences Preferences,
  IReadOnlyDictionary<string, string> KnobTexts,
  string? ProviderSettingsJson);

/// <summary>One editable sampling knob row: a stable name constant (the test and
///     AXAML key), the display label, the current text, and the editability the
///     provider dictates. The window binds <see cref="Text"/> two-way; the
///     view-model parses and range-checks on save — never per keystroke — so
///     invalid intermediate text is legal to type and blocks only the save.
///     Empty text means "unset": the live preference returns to null.</summary>
internal sealed partial class KnobEntry(string name, string label, string text, bool isEnabled) : ObservableObject
{
  [ObservableProperty]
  public partial string Text { get; set; } = text;

  public string Name { get; } = name;

  public string Label { get; } = label;

  /// <summary>False when this knob does not apply to the session's provider
  ///     (z.ai's seven N/A knobs): the field renders disabled/greyed and its
  ///     value never reaches the preferences.</summary>
  public bool IsEnabled { get; } = isEnabled;

}

/// <summary>The OpenRouter routing fields, edited as raw text (list fields are
///     comma-separated; the two policy booleans are editor tri-states) and
///     projected onto the ACL's typed <see cref="Routing"/> record at save time.
///     Plain CLR properties: initial values are set before the window binds and
///     user edits write straight through (the session-file-row pattern).</summary>
internal sealed class RoutingSection
{
  public string OrderText { get; set; } = string.Empty;
  public string OnlyText { get; set; } = string.Empty;
  public string IgnoreText { get; set; } = string.Empty;
  public bool? AllowFallbacks { get; set; }
  public string SortText { get; set; } = string.Empty;
  public string QuantizationsText { get; set; } = string.Empty;
  public bool? RequireParameters { get; set; }
  public string DataCollectionText { get; set; } = string.Empty;
  public string ModelsText { get; set; } = string.Empty;
  public string RouteText { get; set; } = string.Empty;

  /// <summary>Projects the fields onto the ACL record: blank text becomes null,
  ///     comma-separated text splits on commas and trims each entry.</summary>
  public Routing ToRouting() => new(
      Order: SplitList(OrderText),
      Only: SplitList(OnlyText),
      Ignore: SplitList(IgnoreText),
      AllowFallbacks: AllowFallbacks,
      Sort: NullIfBlank(SortText),
      Quantizations: SplitList(QuantizationsText),
      RequireParameters: RequireParameters,
      DataCollection: NullIfBlank(DataCollectionText),
      Models: SplitList(ModelsText),
      Route: NullIfBlank(RouteText));

  private static string[]? SplitList(string text)
  {
    string trimmed = text.Trim();
    return trimmed.Length == 0
        ? null
        : [.. trimmed.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0)];
  }

  private static string? NullIfBlank(string text)
  {
    string trimmed = text.Trim();
    return trimmed.Length == 0 ? null : trimmed;
  }

  internal static RoutingSection From(Routing routing) => new()
  {
    OrderText = JoinList(routing.Order),
    OnlyText = JoinList(routing.Only),
    IgnoreText = JoinList(routing.Ignore),
    AllowFallbacks = routing.AllowFallbacks,
    SortText = routing.Sort ?? string.Empty,
    QuantizationsText = JoinList(routing.Quantizations),
    RequireParameters = routing.RequireParameters,
    DataCollectionText = routing.DataCollection ?? string.Empty,
    ModelsText = JoinList(routing.Models),
    RouteText = routing.Route ?? string.Empty,
  };

  private static string JoinList(string[]? values) => values is null ? string.Empty : string.Join(", ", values);
}

/// <summary>The twelve OpenRouter server-tool toggles plus the server-tool
///     budget fields (max calls as text; stop conditions comma-separated),
///     projected onto the ACL's typed <see cref="ServerTools"/> record at save
///     time.</summary>
internal sealed class ServerToolsSection
{
  public bool WebSearch { get; set; }
  public bool WebFetch { get; set; }
  public bool Datetime { get; set; }
  public bool ImageGeneration { get; set; }
  public bool Shell { get; set; }
  public bool ApplyPatch { get; set; }
  public bool Bash { get; set; }
  public bool Fusion { get; set; }
  public bool Advisor { get; set; }
  public bool Subagent { get; set; }
  public bool SearchModels { get; set; }
  public bool ToolSearch { get; set; }
  public string MaxToolCallsText { get; set; } = string.Empty;
  public string StopServerToolsWhenText { get; set; } = string.Empty;

  public ServerTools ToServerTools() => new(
      WebSearch: WebSearch,
      WebFetch: WebFetch,
      Datetime: Datetime,
      ImageGeneration: ImageGeneration,
      Shell: Shell,
      ApplyPatch: ApplyPatch,
      Bash: Bash,
      Fusion: Fusion,
      Advisor: Advisor,
      Subagent: Subagent,
      SearchModels: SearchModels,
      ToolSearch: ToolSearch,
      MaxToolCalls: ParseIntOrNull(MaxToolCallsText),
      StopServerToolsWhen: SplitList(StopServerToolsWhenText));

  private static string[]? SplitList(string text)
  {
    string trimmed = text.Trim();
    return trimmed.Length == 0
        ? null
        : [.. trimmed.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0)];
  }

  private static int? ParseIntOrNull(string text)
  {
    string trimmed = text.Trim();
    return trimmed.Length == 0 ? null : int.Parse(trimmed, CultureInfo.InvariantCulture);
  }

  internal static ServerToolsSection From(ServerTools tools) => new()
  {
    WebSearch = tools.WebSearch,
    WebFetch = tools.WebFetch,
    Datetime = tools.Datetime,
    ImageGeneration = tools.ImageGeneration,
    Shell = tools.Shell,
    ApplyPatch = tools.ApplyPatch,
    Bash = tools.Bash,
    Fusion = tools.Fusion,
    Advisor = tools.Advisor,
    Subagent = tools.Subagent,
    SearchModels = tools.SearchModels,
    ToolSearch = tools.ToolSearch,
    MaxToolCallsText = tools.MaxToolCalls?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    StopServerToolsWhenText = tools.StopServerToolsWhen is null ? string.Empty : string.Join(", ", tools.StopServerToolsWhen),
  };
}

/// <summary>The OpenRouter plugin toggles (web grounding with optional engine
///     override; response healing), projected onto the ACL's typed
///     <see cref="Plugins"/> record at save time.</summary>
internal sealed class PluginsSection
{
  public bool WebGrounding { get; set; }
  public string WebGroundingEngineText { get; set; } = string.Empty;
  public bool ResponseHealing { get; set; }

  public Plugins ToPlugins() => new(
      WebGrounding: WebGrounding,
      ResponseHealing: ResponseHealing,
      WebGroundingEngine: NullIfBlank(WebGroundingEngineText));

  private static string? NullIfBlank(string text)
  {
    string trimmed = text.Trim();
    return trimmed.Length == 0 ? null : trimmed;
  }

  internal static PluginsSection From(Plugins plugins) => new()
  {
    WebGrounding = plugins.WebGrounding,
    WebGroundingEngineText = plugins.WebGroundingEngine ?? string.Empty,
    ResponseHealing = plugins.ResponseHealing,
  };
}

/// <summary>One bindable knob row for the window: wraps a <see cref="KnobEntry"/>
///     and exposes the label and not-applicable tooltip the XAML binds. XAML
///     compiled bindings need a concrete x:DataType; the dictionary is surfaced
///     as this row list in fixed display order.</summary>
internal sealed partial class KnobRow(KnobEntry entry, string tooltip) : ObservableObject
{
  private readonly KnobEntry _entry = entry;

  public string Label { get; } = entry.Label;

  public string Tooltip { get; } = tooltip;

  public bool IsEnabled => _entry.IsEnabled;

  public string Text
  {
    get => _entry.Text;
    set => _entry.Text = value;
  }
}

/// <summary>One bindable server-tool toggle row for the window's checkbox list.
///     </summary>
internal sealed class ToolToggleRow
{
  public string Label { get; }

  private bool _isChecked;

  public bool IsChecked
  {
    get => _isChecked;
    set
    {
      _isChecked = value;
      _onChanged(value);
    }
  }

  private readonly Action<bool> _onChanged;

  public ToolToggleRow(string label, bool isChecked, Action<bool> onChanged)
    => (Label, _isChecked, _onChanged) = (label, isChecked, onChanged);
}

/// <summary>View-model behind the Model Settings window: the effort choice, the
///     twelve sampling knobs, and — on OpenRouter sessions only — the routing,
///     server-tool, plugin, and budget section. Pure state and validation;
///     window mechanics and persistence belong to the caller. Constructed with
///     the session's live <see cref="SessionModelPreferences"/>, the provider id,
///     and a persistence delegate seam (tests inject a recorder; production
///     delegates into the shell's per-workspace+provider persistence). Save is
///     all-or-nothing: any invalid knob text blocks the save with a visible
///     message naming the failing field, and nothing is mutated or persisted.
///     The OpenRouter section's structured fields serialize through
///     <see cref="OpenRouterRequestSettings.Serialize"/> (translation seam — the
///     wire names live only in the ACL); an all-default or hidden section saves
///     null so stale settings never linger. The Model Domain's validation ranges
///     are the single source of truth, mirrored as small private validators
///     (per the brief, <c>ModelConfig.Create</c> is not referenced for per-knob
///     validation).</summary>
internal sealed partial class ModelSettingsViewModel : ObservableObject
{
  // Knob name constants — the single vocabulary shared by tests, the AXAML keys,
  // and the snapshot's knob-text map.
  public const string KnobTemperature = "temperature";
  public const string KnobMaxTokens = "maxTokens";
  public const string KnobTopP = "topP";
  public const string KnobTopK = "topK";
  public const string KnobFrequencyPenalty = "frequencyPenalty";
  public const string KnobPresencePenalty = "presencePenalty";
  public const string KnobRepetitionPenalty = "repetitionPenalty";
  public const string KnobMinP = "minP";
  public const string KnobTopA = "topA";
  public const string KnobSeed = "seed";
  public const string KnobVerbosity = "verbosity";
  public const string KnobParallelToolCalls = "parallelToolCalls";

  /// <summary>The effort pseudo-choice meaning "no runtime effort — the model
  ///     default applies"; any other value is a ReasoningEffort enum name.</summary>
  public const string EffortDefault = "default";

  public const string EffortHigh = nameof(ReasoningEffort.High);

  private static readonly Dictionary<string, string> KnobLabels = new()
  {
    [KnobTemperature] = "Temperature (0-2)",
    [KnobMaxTokens] = "Max tokens (>= 1)",
    [KnobTopP] = "Top P (0-1)",
    [KnobTopK] = "Top K (>= 0)",
    [KnobFrequencyPenalty] = "Frequency penalty (-2 to 2)",
    [KnobPresencePenalty] = "Presence penalty (-2 to 2)",
    [KnobRepetitionPenalty] = "Repetition penalty (0-2)",
    [KnobMinP] = "Min P (0-1)",
    [KnobTopA] = "Top A (0-1)",
    [KnobSeed] = "Seed",
    [KnobVerbosity] = "Verbosity (low/medium/high/xhigh/max)",
    [KnobParallelToolCalls] = "Parallel tool calls (true/false)",
  };

  /// <summary>The knobs z.ai does not accept — visible but disabled on z.ai
  ///     sessions (greyed, the tooltip names them not applicable), editable on
  ///     OpenRouter. Mirrors the z.ai ACL's wire contract (only top_p,
  ///     frequency_penalty, presence_penalty of the numeric knobs are sent).</summary>
  private static readonly HashSet<string> ZaiNotApplicable =
  [
    KnobTopK, KnobRepetitionPenalty, KnobMinP, KnobTopA, KnobSeed, KnobVerbosity, KnobParallelToolCalls,
  ];

  private readonly SessionModelPreferences _live;
  private readonly Action<ModelSettingsSnapshot> _persist;

  /// <summary>The twelve knob rows, keyed by name constant.</summary>
  public IReadOnlyDictionary<string, KnobEntry> Knobs { get; }

  public RoutingSection Routing { get; }

  public ServerToolsSection ServerTools { get; }

  public PluginsSection Plugins { get; }

  /// <summary>True when the OpenRouter-only section shows at all: OpenRouter
  ///     sessions only. The window collapses it entirely otherwise, and a save
  ///     then persists null provider settings (never stale values).</summary>
  public bool ShowOpenRouterSection => IsOpenRouter;

  private bool IsOpenRouter { get; }

  private bool IsZai { get; }

  /// <summary>The chosen reasoning effort: <see cref="EffortDefault"/> or a
  ///     <see cref="ReasoningEffort"/> name. The window binds this to its effort
  ///     selector; the save path parses it back onto the preferences.</summary>
  [ObservableProperty]
  public partial string EffortChoice { get; set; }

  public IRelayCommand SaveCommand { get; }

  /// <summary>Raised after a successful save; carries the snapshot. The window
  ///     closes with it. A cancelled dialog raises nothing and writes nothing.
  ///     </summary>
  public event EventHandler<ModelSettingsSnapshot>? SettingsSaved;

  /// <summary>One choosable effort row: the model-default pseudo-choice or a
  ///     concrete <see cref="ReasoningEffort"/> level (display name from the
  ///     domain's own naming).</summary>
  internal sealed record EffortOption(string DisplayName, string Choice)
  {
    public static readonly EffortOption Default = new("Model default", EffortDefault);

    public static EffortOption For(ReasoningEffort level)
        => new(EffortLevels.DisplayName(level), level.ToString());
  }

  /// <summary>The effort choices in display order: model default first, then the
  ///     seven levels in the picker's documented order.</summary>
  public IReadOnlyList<EffortOption> EffortOptions { get; } =
  [
    EffortOption.Default,
    EffortOption.For(ReasoningEffort.Max),
    EffortOption.For(ReasoningEffort.ExtraHigh),
    EffortOption.For(ReasoningEffort.High),
    EffortOption.For(ReasoningEffort.Medium),
    EffortOption.For(ReasoningEffort.Low),
    EffortOption.For(ReasoningEffort.Minimal),
    EffortOption.For(ReasoningEffort.None),
  ];

  /// <summary>The currently selected effort row (two-way bound). Mirrors
  ///     <see cref="EffortChoice"/> so the save path keeps one source of truth.
  ///     </summary>
  [ObservableProperty]
  public partial EffortOption SelectedEffortOption { get; set; }

  partial void OnSelectedEffortOptionChanged(EffortOption value) => EffortChoice = value.Choice;

  /// <summary>The knob rows in fixed display order (the window's ItemsSource).
  ///     </summary>
  public IReadOnlyList<KnobRow> KnobRows { get; }

  /// <summary>The server-tool toggles in wire order (the window's ItemsSource).
  ///     </summary>
  public IReadOnlyList<ToolToggleRow> ToolToggles { get; }

  /// <summary>The tooltip naming z.ai's not-applicable knobs (visible but
  ///     disabled there, per the provider-applicability rule).</summary>
  private const string NotApplicableTooltip = "Not applicable to z.ai";

  /// <summary>The not-applicable tooltip for a knob the provider does not take;
  ///     empty for applicable knobs.</summary>
  private string TooltipFor(string knob) => Knobs[knob].IsEnabled ? string.Empty : NotApplicableTooltip;
  public ModelSettingsViewModel(SessionModelPreferences current, string providerName,
      Action<ModelSettingsSnapshot> persist,
      IReadOnlyDictionary<string, string>? persistedKnobTexts = null)
  {
    ArgumentNullException.ThrowIfNull(current);
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
    ArgumentNullException.ThrowIfNull(persist);
    _live = current;
    _persist = persist;
    IsOpenRouter = string.Equals(providerName, ProvidersOpenRouter, StringComparison.Ordinal);
    IsZai = string.Equals(providerName, ProvidersZai, StringComparison.Ordinal);
    persistedKnobTexts ??= new Dictionary<string, string>();
    bool Editable(string knob) => !IsZai || !ZaiNotApplicable.Contains(knob);
    string Text(string knob, string? fromPreferences) =>
        persistedKnobTexts.TryGetValue(knob, out string? persisted) ? persisted : fromPreferences ?? string.Empty;
    Knobs = new Dictionary<string, KnobEntry>
    {
      [KnobTemperature] = new(KnobTemperature, KnobLabels[KnobTemperature], Text(KnobTemperature, Format(_live.Temperature)), Editable(KnobTemperature)),
      [KnobMaxTokens] = new(KnobMaxTokens, KnobLabels[KnobMaxTokens], Text(KnobMaxTokens, Format(_live.MaxTokens)), Editable(KnobMaxTokens)),
      [KnobTopP] = new(KnobTopP, KnobLabels[KnobTopP], Text(KnobTopP, Format(_live.TopP)), Editable(KnobTopP)),
      [KnobTopK] = new(KnobTopK, KnobLabels[KnobTopK], Text(KnobTopK, Format(_live.TopK)), Editable(KnobTopK)),
      [KnobFrequencyPenalty] = new(KnobFrequencyPenalty, KnobLabels[KnobFrequencyPenalty], Text(KnobFrequencyPenalty, Format(_live.FrequencyPenalty)), Editable(KnobFrequencyPenalty)),
      [KnobPresencePenalty] = new(KnobPresencePenalty, KnobLabels[KnobPresencePenalty], Text(KnobPresencePenalty, Format(_live.PresencePenalty)), Editable(KnobPresencePenalty)),
      [KnobRepetitionPenalty] = new(KnobRepetitionPenalty, KnobLabels[KnobRepetitionPenalty], Text(KnobRepetitionPenalty, Format(_live.RepetitionPenalty)), Editable(KnobRepetitionPenalty)),
      [KnobMinP] = new(KnobMinP, KnobLabels[KnobMinP], Text(KnobMinP, Format(_live.MinP)), Editable(KnobMinP)),
      [KnobTopA] = new(KnobTopA, KnobLabels[KnobTopA], Text(KnobTopA, Format(_live.TopA)), Editable(KnobTopA)),
      [KnobSeed] = new(KnobSeed, KnobLabels[KnobSeed], Text(KnobSeed, Format(_live.Seed)), Editable(KnobSeed)),
      [KnobVerbosity] = new(KnobVerbosity, KnobLabels[KnobVerbosity], Text(KnobVerbosity, Format(_live.Verbosity)), Editable(KnobVerbosity)),
      [KnobParallelToolCalls] = new(KnobParallelToolCalls, KnobLabels[KnobParallelToolCalls], Text(KnobParallelToolCalls, Format(_live.ParallelToolCalls)), Editable(KnobParallelToolCalls)),
    };
    (Routing, ServerTools, Plugins) = ParseProviderSettings(_live.ProviderSettings);
    KnobRows = [.. Knobs.Values.Select(entry => new KnobRow(entry, TooltipFor(entry.Name)))];
    ToolToggles =
    [
      new ToolToggleRow("Web search", ServerTools.WebSearch, value => ServerTools.WebSearch = value),
      new ToolToggleRow("Web fetch", ServerTools.WebFetch, value => ServerTools.WebFetch = value),
      new ToolToggleRow("Date and time", ServerTools.Datetime, value => ServerTools.Datetime = value),
      new ToolToggleRow("Image generation", ServerTools.ImageGeneration, value => ServerTools.ImageGeneration = value),
      new ToolToggleRow("Shell", ServerTools.Shell, value => ServerTools.Shell = value),
      new ToolToggleRow("Apply patch", ServerTools.ApplyPatch, value => ServerTools.ApplyPatch = value),
      new ToolToggleRow("Bash", ServerTools.Bash, value => ServerTools.Bash = value),
      new ToolToggleRow("Fusion retrieval", ServerTools.Fusion, value => ServerTools.Fusion = value),
      new ToolToggleRow("Advisor", ServerTools.Advisor, value => ServerTools.Advisor = value),
      new ToolToggleRow("Sub-agent", ServerTools.Subagent, value => ServerTools.Subagent = value),
      new ToolToggleRow("Search models", ServerTools.SearchModels, value => ServerTools.SearchModels = value),
      new ToolToggleRow("Tool search", ServerTools.ToolSearch, value => ServerTools.ToolSearch = value),
    ];
    EffortChoice = _live.ReasoningEffort?.ToString() ?? EffortDefault;
    SelectedEffortOption = EffortOptions.FirstOrDefault(option => option.Choice == EffortChoice) ?? EffortOption.Default;
    // The command exists before the observable property is set: setting it raises
    // the changed hook, which requeries save availability. The guard in the action
    // is load-bearing: ICommand.Execute does not consult CanExecute, and a disabled
    // button is only one of several ways this command can be invoked.
    SaveCommand = new RelayCommand(Save, () => CanSave);
  }

  /// <summary>Parses the persisted provider settings for the section prefill.
  ///     Null or blank yields the all-default sections; corrupt JSON degrades to
  ///     the defaults too — prefill is best effort, and the next save overwrites
  ///     the payload (the agent-turn path is where corrupt settings are a
  ///     structured error, not here).</summary>
  private static (RoutingSection, ServerToolsSection, PluginsSection) ParseProviderSettings(string? json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return (new RoutingSection(), new ServerToolsSection(), new PluginsSection());
    }

    // Named decision (CA1031): a corrupt preference must not crash the dialog.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      OpenRouterRequestSettings parsed = OpenRouterRequestSettings.Parse(json)!;
      return (RoutingSection.From(parsed.Routing), ServerToolsSection.From(parsed.ServerTools), PluginsSection.From(parsed.Plugins));
    }
    catch
    {
      return (new RoutingSection(), new ServerToolsSection(), new PluginsSection());
    }
#pragma warning restore CA1031
  }

  /// <summary>The first validation problem across all knobs, or null when clean.
  ///     Computed on demand: the window shows it after a blocked save, and the
  ///     Save command requeries availability on text edits.</summary>
  public string? ValidationError => ValidateKnobs();

  /// <summary>Save is only actionable when every populated knob validates.</summary>
  public bool CanSave => ValidationError is null;

  /// <summary>Programmatic setter mirroring two-way binding (tests): sets the
  ///     row's text and requeries save availability.</summary>
  public void SetKnob(string knob, string text)
  {
    Knobs[knob].Text = text;
    SaveCommand.NotifyCanExecuteChanged();
  }

  /// <summary>The all-or-nothing save: validate every knob, then apply to the
  ///     live preferences, serialize the OpenRouter section (null when hidden or
  ///     all-default), and hand the snapshot to the persistence seam. Returns
  ///     false with a message naming the failing field when any knob is invalid —
  ///     nothing is mutated, nothing persists.</summary>
  public bool TrySave(out string? error)
  {
    error = ValidateKnobs();
    if (error is not null)
    {
      return false;
    }

    ApplyKnobsAndEffort();
    string? serialized = ShowOpenRouterSection ? SerializeSectionOrNull() : null;
    _live.ProviderSettings = serialized;
    ModelSettingsSnapshot snapshot = new(_live, KnobTexts(), serialized);
    _persist(snapshot);
    SettingsSaved?.Invoke(this, snapshot);
    return true;
  }

  private string? SerializeSectionOrNull()
  {
    OpenRouterRequestSettings settings = new()
    {
      Routing = Routing.ToRouting(),
      ServerTools = ServerTools.ToServerTools(),
      Plugins = Plugins.ToPlugins(),
    };
    string? serialized = IsAllDefault(settings) ? null : OpenRouterRequestSettings.Serialize(settings);
    return serialized;
  }

  private static bool IsAllDefault(OpenRouterRequestSettings settings)
      => settings.Routing == new Routing() && settings.ServerTools == new ServerTools() && settings.Plugins == new Plugins();

  private Dictionary<string, string> KnobTexts()
      => Knobs.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim(), StringComparer.Ordinal);

  private void Save()
  {
    if (CanSave)
    {
      _ = TrySave(out _);
    }
  }

  /// <summary>Parses and applies every populated knob text plus the effort choice
  ///     onto the live preferences. Called only after validation succeeded. All
  ///     twelve knobs land on their typed preference members; the snapshot's
  ///     knob-text map persists them for the restore + window-prefill path.</summary>
  private void ApplyKnobsAndEffort()
  {
    _live.ReasoningEffort = EffortChoice == EffortDefault ? null : Enum.Parse<ReasoningEffort>(EffortChoice);
    ApplyFloat(KnobTemperature, value => _live.Temperature = value);
    ApplyInt(KnobMaxTokens, value => _live.MaxTokens = value);
    ApplyFloat(KnobTopP, value => _live.TopP = value);
    ApplyInt(KnobTopK, value => _live.TopK = value);
    ApplyFloat(KnobFrequencyPenalty, value => _live.FrequencyPenalty = value);
    ApplyFloat(KnobPresencePenalty, value => _live.PresencePenalty = value);
    ApplyFloat(KnobRepetitionPenalty, value => _live.RepetitionPenalty = value);
    ApplyFloat(KnobMinP, value => _live.MinP = value);
    ApplyFloat(KnobTopA, value => _live.TopA = value);
    ApplyInt(KnobSeed, value => _live.Seed = value);
    string verbosity = Knobs[KnobVerbosity].Text.Trim();
    _live.Verbosity = verbosity.Length == 0 ? null : Enum.Parse<VerbosityLevel>(verbosity, ignoreCase: true);
    string parallel = Knobs[KnobParallelToolCalls].Text.Trim();
    _live.ParallelToolCalls = parallel.Length == 0 ? null : bool.Parse(parallel);
  }

  private void ApplyFloat(string knob, Action<float?> assign)
  {
    string text = Knobs[knob].Text.Trim();
    assign(text.Length == 0
        ? null
        : float.Parse(text, CultureInfo.InvariantCulture));
  }

  private void ApplyInt(string knob, Action<int?> assign)
  {
    string text = Knobs[knob].Text.Trim();
    assign(text.Length == 0 ? null : int.Parse(text, CultureInfo.InvariantCulture));
  }

  /// <summary>Validates every populated knob: parse then range. Returns a message
  ///     naming the failing knob, or null when clean. Ranges mirror the Model
  ///     Domain's <c>ModelConfig.Create</c> rules exactly; unset/empty text is
  ///     always legal.</summary>
  private string? ValidateKnobs()
  {
    return ValidateFloat(KnobTemperature, 0f, 2f)
        ?? ValidateInt(KnobMaxTokens, minimum: 1)
        ?? ValidateFloat(KnobTopP, 0f, 1f)
        ?? ValidateInt(KnobTopK, minimum: 0)
        ?? ValidateFloat(KnobFrequencyPenalty, -2f, 2f)
        ?? ValidateFloat(KnobPresencePenalty, -2f, 2f)
        ?? ValidateFloat(KnobRepetitionPenalty, 0f, 2f)
        ?? ValidateFloat(KnobMinP, 0f, 1f)
        ?? ValidateFloat(KnobTopA, 0f, 1f)
        ?? ValidateInt(KnobSeed, minimum: int.MinValue)
        ?? ValidateVerbosity()
        ?? ValidateParallelToolCalls();
  }

  private string? ValidateFloat(string knob, float minimum, float maximum)
  {
    string text = Knobs[knob].Text.Trim();
    if (text.Length == 0)
    {
      return null;
    }

    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
    {
      return NotANumber(knob, text);
    }

    string? rangeError = value < minimum || value > maximum ? OutOfRange(knob, text) : null;
    return rangeError;
  }

  private string NotANumber(string knob, string text)
      => $"'{Knobs[knob].Label}' ({knob}) is not a number: '{text}'.";

  private string NotAWholeNumber(string knob, string text)
      => $"'{Knobs[knob].Label}' ({knob}) is not a whole number: '{text}'.";

  private string OutOfRange(string knob, string text)
      => $"'{Knobs[knob].Label}' ({knob}) is out of range: '{text}'. See the field labels for each knob's accepted interval.";

  private string? ValidateInt(string knob, int minimum)
  {
    string text = Knobs[knob].Text.Trim();
    if (text.Length == 0)
    {
      return null;
    }

    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
    {
      return NotAWholeNumber(knob, text);
    }

    string? rangeError = value < minimum ? OutOfRange(knob, text) : null;
    return rangeError;
  }

  private string? ValidateVerbosity()
  {
    string text = Knobs[KnobVerbosity].Text.Trim();
    bool unset = text.Length == 0;
    bool known = Enum.TryParse<VerbosityLevel>(text, ignoreCase: true, out _);
    return (unset, known) switch
    {
      (true, _) => null,
      (_, true) => null,
      _ => NotAVerbosity(text),
    };
  }

  private string NotAVerbosity(string text)
      => $"'{Knobs[KnobVerbosity].Label}' ({KnobVerbosity}) must be one of low/medium/high/xhigh/max: '{text}'.";

  private string? ValidateParallelToolCalls()
  {
    string text = Knobs[KnobParallelToolCalls].Text.Trim();
    bool unset = text.Length == 0;
    bool known = bool.TryParse(text, out _);
    return (unset, known) switch
    {
      (true, _) => null,
      (_, true) => null,
      _ => NotABool(text),
    };
  }

  private string NotABool(string text)
      => $"'{Knobs[KnobParallelToolCalls].Label}' ({KnobParallelToolCalls}) must be true or false: '{text}'.";

  private static string Format(float? value)
      => value?.ToString("0.0##", CultureInfo.InvariantCulture) ?? string.Empty;

  private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

  private static string Format(bool? value) => value switch
  {
    null => string.Empty,
    true => "true",
    false => "false",
  };

  private static string Format(VerbosityLevel? value) => value switch
  {
    null => string.Empty,
    VerbosityLevel.Low => "low",
    VerbosityLevel.Medium => "medium",
    VerbosityLevel.High => "high",
    VerbosityLevel.XHigh => "xhigh",
    VerbosityLevel.Max => "max",
    _ => value.Value.ToString(),
  };

  private const string ProvidersOpenRouter = "openrouter";

  private const string ProvidersZai = "zai";
}
