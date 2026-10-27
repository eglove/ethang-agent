using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The pair confirmed in the picker: a concrete model id plus the provider that
///     should serve it (null when the user picked a model without committing a provider,
///     which leaves the provider unset rather than guessed). (A cancelled dialog closes with
///     no result at all.)</summary>
internal sealed record ModelChoice(string? ModelId, string? ProviderName = null);

/// <summary>One choosable row of the MODEL level (issue #296, D1): a concrete model, deduped
///     across the catalog's provider endpoints, summarised by its best provider under the
///     current preset.
///     <para>WHY A SUMMARY ROW AND NOT ALL PROVIDERS EXPANDED (the design pass's first open
///     question): a model with 35 endpoints expanded into a flat list is unreadable at any
///     window width, and the user's first decision is which MODEL - the provider choice is a
///     second, narrower decision that the detail level shows in full. The summary still names
///     the provider and the effective price it is summarising, so the model level is never a
///     price the user cannot trace.</para></summary>
/// <param name="ModelId">The model this row stands for.</param>
/// <param name="DisplayName">The row's caption (the model id).</param>
/// <param name="Detail">The one-line summary: best provider, its effective price, and how
///     many providers the model offers.</param>
/// <param name="Endpoints">Every catalog row for this model (the provider level's input).
///     </param>
internal sealed record ModelPickerRow(
    string? ModelId,
    string DisplayName,
    string Detail,
    IReadOnlyList<ModelProviderEntry> Endpoints);

/// <summary>One preset row of the picker's ordering control (issue #296, D4).</summary>
internal sealed record PresetOption(string DisplayName, ProviderPreset Preset);

/// <summary>Everything the embedded model + provider section needs from its host (issue
///     #296), bundled so the settings view-model's constructor stays a readable list of
///     concerns rather than a wall of delegates.
///     <para>EVERY MEMBER IS A SEAM THE HOST OWNS: the catalog load and its revalidation join,
///     the session's committed pair (to pre-select), and the ledger's observed performance.
///     A host that supplies no performance loader renders every measurement as unavailable
///     rather than failing the surface - a picker with no history is still a picker.</para>
///     </summary>
/// <param name="LoadCatalog">Loads the provider catalog. Required: without it there is no
///     section at all.</param>
/// <param name="CurrentModelId">The session's live model, to pre-select.</param>
/// <param name="RefreshCatalog">Joins the catalog's source revalidation, or null to skip
///     it.</param>
/// <param name="CurrentProviderName">The session's committed provider, to pre-select in the
///     provider level.</param>
/// <param name="LoadPerformance">Loads the ledger's per-provider performance, or null when
///     the host wired no read model.</param>
/// <param name="LoadPublished">Loads the rates providers publish for their endpoints, or null
///     when the host wired no source. This is the rung that answers for a pair the ledger has
///     never seen.</param>
internal sealed record ModelSectionSeams(
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> LoadCatalog,
    string? CurrentModelId = null,
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? RefreshCatalog = null,
    string? CurrentProviderName = null,
    Func<CancellationToken, Task<Result<ProviderPerformanceReport>>>? LoadPerformance = null,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, PublishedProviderStats>>>? LoadPublished = null);

/// <summary>View-model behind the model + provider picker (issue #296): a searchable model
///     list, the selected model's provider rows with their evidence, the ordering preset, and
///     the cache-hit-rate assumption the prices are projected at.
///     <para>TWO LEVELS, NOT ONE FLAT LIST (D1). The previous surface deduped 35 provider rows
///     into one model row and priced it by whichever endpoint sorted first - for
///     <c>deepseek/deepseek-v4.1-flash-1017</c> that was Reka, the single worst cache ratio on
///     the model, while a provider charging double the prompt price cost 5.2x less at a real
///     hit rate. The user needs to see that a choice exists at BOTH levels, so this view-model
///     keeps them apart: <see cref="FilteredRows"/> is the model level and
///     <see cref="ProviderRows"/> is the provider level for the selected model.</para>
///     <para>EVERY FIGURE COMES FROM THE DOMAIN. The rows are built by
///     <see cref="ProviderComparison.Rows"/>, which ranks through
///     <see cref="ProviderRanking"/> and renders through <see cref="ProviderPerformanceText"/>,
///     so no view-model (and no view) can invent a second ordering or print a zero where a
///     measurement is missing (D5).</para>
///     <para>THE CATALOG AND THE LEDGER LOAD OFF THE UI THREAD and the list stays in its
///     loading state until they land: the first open per session can take seconds (OpenRouter
///     crawls per-model endpoints), and the ledger read is a database query.</para></summary>
internal sealed partial class ModelPickerViewModel : ObservableObject
{
  private readonly Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> _loadCatalog;
  private readonly Func<CancellationToken, Task<Result<ProviderPerformanceReport>>>? _loadPerformance;
  private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, PublishedProviderStats>>>? _loadPublished;
  private readonly string? _currentModelId;
  private readonly string? _currentProviderName;
  private IReadOnlyList<ModelPickerRow> ModelRows { get; set; } = [];
  private IReadOnlyList<ModelPickerRow> FilteredRowsList { get; set; } = [];
  private IReadOnlyList<ModelProviderEntry> CatalogEntries { get; set; } = [];
  private ProviderPerformanceReport? _report;
  private bool _loaded;

  /// <summary>Raised when the user confirms a row; carries the pair. The view closes the
  ///     dialog with it.</summary>
  public event EventHandler<ModelChoice>? ConfirmRequested;

  public IRelayCommand ConfirmCommand { get; }

  [ObservableProperty]
  public partial string SearchText { get; set; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string? LoadError { get; set; }

  /// <summary>The filtered list is rebuilt ONLY here - selection changes must never swap
  ///     ItemsSource (see <see cref="FilteredRows"/>).</summary>
  partial void OnSearchTextChanged(string value) => RebuildFilteredRows();

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
  public partial ModelPickerRow? SelectedRow { get; set; }

  /// <summary>The models matching the search text, case-insensitive on the model id. The
  ///     list identity is STABLE between rebuilds: <see cref="SelectedRow"/> deliberately does
  ///     not raise it - swapping ItemsSource on every selection change would reset the ListBox
  ///     mid-click, lose the row the user just picked, and write a stale selection back into
  ///     this property (the click-twice desync, headless-tested in ModelPickerWindowTests).
  ///     </summary>
  public IReadOnlyList<ModelPickerRow> FilteredRows => FilteredRowsList;

  /// <summary>The selected model's provider rows, best first under the current preset
  ///     (issue #296, D1/B1). Empty until a model is selected - the provider level exists
  ///     only for a model.</summary>
  public IReadOnlyList<ProviderEvidenceRow> ProviderRows
  {
    get;
    private set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      OnPropertyChanged();
      OnPropertyChanged(nameof(HasProviderRows));
      OnPropertyChanged(nameof(ProviderLevelCaption));
    }
  } = [];

  /// <summary>True when the selected model has provider rows to show.</summary>
  public bool HasProviderRows => ProviderRows.Count > 0;

  /// <summary>The provider level's caption: which model it belongs to and how many providers
  ///     it offers. Empty before a model is selected.</summary>
  public string ProviderLevelCaption => SelectedRow is null
      ? string.Empty
      : $"{ProviderRows.Count} endpoint(s) for {SelectedRow.ModelId}";

  /// <summary>The preset rows in display order (the ordering control's ItemsSource).</summary>
  public IReadOnlyList<PresetOption> PresetOptions { get; } =
  [
    .. Enum.GetValues<ProviderPreset>().Select(preset =>
        new PresetOption(ProviderPresets.DisplayName(preset), preset)),
  ];

  /// <summary>The selected ordering preset (two-way bound). Changing it reorders the provider
  ///     level and never removes a row (D4/B5).</summary>
  [ObservableProperty]
  public partial PresetOption SelectedPresetOption { get; set; }

  partial void OnSelectedPresetOptionChanged(PresetOption value) => RebuildProviderRows();

  /// <summary>The dimension the current preset orders by, spelled out so the user can see
  ///     WHY the list is in this order (D4/B5: 'the dimension that drove the order is
  ///     visible').</summary>
  public string OrderingCaption => SelectedPresetOption is null
      ? string.Empty
      : $"ordered by {DimensionName(ProviderPresets.Highlighted(SelectedPresetOption.Preset))}";

  /// <summary>The rate every row with no evidence of its own is projected at, and the one
  ///     the caption states. Rows with their own evidence carry theirs beside their price.
  ///     <para>THERE IS NO EDITABLE RATE. A hand-typed assumption was removed: the rate is a
  ///     property of the model, the provider, and the workload, so the harness resolves it
  ///     from evidence - our ledger for the pair, then the rate the provider publishes, then
  ///     this documented default - and each row says which rung answered for it.</para></summary>
  public HitRateAssumption FallbackRate { get; private set; } = HitRateAssumption.Fallback;

  /// <summary>Explains where a row's cache-hit rate comes from, since it is no longer a
  ///     setting the user can see or change. States the fallback in force so a row showing
  ///     the default is recognisable rather than mysterious.</summary>
  public string RateCaption => $"Cache-hit rate is resolved per provider: our own measured rate "
      + $"when this pair has run, else the rate the provider publishes, else {FallbackRate.Describe()}.";

  /// <summary>The per-pair rates the rows are projected at, rebuilt whenever the catalog or
  ///     the ledger changes.</summary>
  private HitRateLookup _rates = HitRateLookup.Uniform(HitRateAssumption.Fallback);

  /// <summary>The published rates fetched alongside the catalog, keyed by pair.</summary>
  private IReadOnlyDictionary<string, PublishedProviderStats> _published =
      new Dictionary<string, PublishedProviderStats>(StringComparer.Ordinal);

  /// <param name="loadCatalog">Loads the open session's provider catalog (UI thread escape
  ///     handled here - the delegate itself may block on HTTP).</param>
  /// <param name="currentModelId">The session's live model choice to pre-select (null
  ///     pre-selects nothing).</param>
  /// <param name="currentProviderName">The session's live provider choice to pre-select in
  ///     the provider level (null pre-selects nothing).</param>
  /// <param name="loadPerformance">Loads the ledger's observed per-provider performance
  ///     (issue #295). Null - a headless host or a host with no read model - renders every
  ///     measurement as unavailable rather than failing the surface: a picker with no history
  ///     is still a picker.</param>
  /// <param name="loadPublished">Loads the rates providers publish for their endpoints
  ///     (issue #296 follow-up). Null - a headless host, or a provider that publishes none -
  ///     leaves every pair without a measurement on the documented default, which is exactly
  ///     what such a session rendered before.</param>
  public ModelPickerViewModel(
      Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> loadCatalog,
      string? currentModelId,
      string? currentProviderName = null,
      Func<CancellationToken, Task<Result<ProviderPerformanceReport>>>? loadPerformance = null,
      Func<CancellationToken, Task<IReadOnlyDictionary<string, PublishedProviderStats>>>? loadPublished = null)
  {
    _loadCatalog = loadCatalog ?? throw new ArgumentNullException(nameof(loadCatalog));
    _loadPerformance = loadPerformance;
    _loadPublished = loadPublished;
    _currentModelId = currentModelId;
    _currentProviderName = currentProviderName;

    // The commands exist before the observable properties: setting those raises the
    // changed hooks, which requery command availability. The guard in the action is
    // load-bearing: ICommand.Execute does not consult CanExecute, and a disabled button is
    // only one of several ways this command can be invoked.
    ConfirmCommand = new RelayCommand(Confirm, () => SelectedRow is not null);
    SelectedPresetOption = PresetOptions[0];
    SearchText = string.Empty; // raises OnSearchTextChanged → initial list
  }

  /// <summary>Fetches the catalog and the ledger's performance report off the UI thread and
  ///     fills both levels. A failure lands in <see cref="LoadError"/> (the dialog stays open;
  ///     the user can cancel). Only the first call loads.</summary>
  public async Task LoadAsync()
  {
    if (IsLoading || _loaded)
    {
      return;
    }

    _loaded = true;
    IsLoading = true;
    // Named decision (CA1031): a loader fault must land in the dialog's error state, never
    // escape - the fire-and-forget caller cannot observe it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      // HTTP plus (for OpenRouter) a per-model endpoint crawl - never on the UI thread.
      // Context flow is suppressed alongside the thread switch, mirroring MainViewModel's
      // session-open schedule.
      Task<Result<IReadOnlyList<ModelProviderEntry>>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _loadCatalog(CancellationToken.None));
      }

      Result<IReadOnlyList<ModelProviderEntry>> catalog = await scheduled;
      if (!catalog.IsSuccess)
      {
        LoadError = catalog.Error.Message;
        return;
      }

      CatalogEntries = catalog.Value;
      _published = await LoadPublishedAsync();
      ResolveRates();
      // Pre-select the session's live choice, when it is in the list. The provider level
      // then pre-selects the session's provider, so a reopened picker shows the pair it
      // committed.
      SelectedRow = FilteredRows.FirstOrDefault(r => r.ModelId == _currentModelId);
      await LoadPerformanceAsync();
    }
    catch (Exception ex)
    {
      LoadError = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
#pragma warning restore CA1031
  }

  /// <summary>Reads the performance report and adopts the measured hit rate it supports.
  ///     Best effort: a failed read leaves the report null (every measurement unavailable) and
  ///     the documented default assumption in force, which is exactly what a session with no
  ///     history renders anyway.</summary>
  private async Task LoadPerformanceAsync()
  {
    if (_loadPerformance is null)
    {
      return;
    }

    // Named decision (CA1031): a read-model fault must not fail the picker.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Task<Result<ProviderPerformanceReport>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _loadPerformance(CancellationToken.None));
      }

      Result<ProviderPerformanceReport> report = await scheduled;
      if (report.IsSuccess)
      {
        _report = report.Value;
        // The ledger is now known, so every row's rate can be resolved: measured for pairs it
        // covers, published for pairs it does not, the documented default for the rest.
        ResolveRates();
      }
      else
      {
        // A read-model failure is SURFACED, never swallowed: the prices still render (they
        // come from the catalog) but every measurement says it is unavailable, and the
        // dialog states why rather than letting the user read silence as "nothing measured".
        LoadError = $"Provider history unavailable ({report.Error.Code}); measurements show as "
            + $"{ProviderPerformanceText.NotMeasured}. {report.Error.Message}";
      }
    }
    catch (Exception ex)
    {
      LoadError ??= $"Provider history unavailable; measurements show as {ProviderPerformanceText.NotMeasured}. {ex.Message}";
    }
#pragma warning restore CA1031
  }

  /// <summary>Applies a catalog refresh (the settings dialog's revalidation join): the rows
  ///     are rebuilt and the selection preserved, so a refresh never invents a user edit.
  ///     </summary>
  internal void ApplyRefresh(Result<IReadOnlyList<ModelProviderEntry>> result)
  {
    if (!result.IsSuccess)
    {
      LoadError = $"Catalog refresh failed; showing cached models. {result.Error.Message}";
      return;
    }

    ModelPickerRow? selected = SelectedRow;
    CatalogEntries = result.Value;
    ResolveRates();
    SelectedRow = selected is null ? null
        : FilteredRows.FirstOrDefault(row => row.ModelId == selected.ModelId) ?? selected;
    LoadError = null;
  }

  /// <summary>Re-resolves every row's rate from the evidence now in hand and re-renders both
  ///     levels. Called whenever the catalog or the ledger lands: the rates depend on both, so
  ///     neither can be adopted alone.</summary>
  private void ResolveRates()
  {
    // THE MEASUREMENTS ARE BOUND TO ENDPOINTS BEFORE ANY RATE IS RESOLVED (issue #296
    // follow-up). A usage row carries the session's PIN as its provider stamp, so a slug pin
    // already identifies one endpoint while a bare name pin identifies none of the provider's
    // several. Binding against the catalog turns the first into per-endpoint evidence and
    // leaves the second honestly ambiguous, instead of blending every variant into one figure
    // and then pricing each variant at its own different price.
    ProviderPerformanceReport? measured = _report?.BindEndpoints(CatalogEntries);
    _rates = HitRateResolution.Resolve(CatalogEntries, measured, _published, FallbackRate);
    ModelRows = BuildRows(CatalogEntries);
    RebuildFilteredRows();
    RebuildProviderRows();
  }

  private void Confirm()
  {
    if (SelectedRow is not { } row)
    {
      return;
    }

    // The committed provider is the one the user selected in the provider level, carried as
    // its ROUTING SLUG (issue #296 follow-up). A provider name does not identify an endpoint -
    // five grok-4.7 endpoints are all "xAI" and differ in region, ZDR, tier, and price - so
    // committing the name would pin whichever variant the router preferred, which is the
    // silent substitution the pin exists to prevent. When the model has no provider rows at
    // all (an empty catalog slice) the provider stays unset rather than guessed - the same
    // rule the model choice follows.
    ConfirmRequested?.Invoke(this, new ModelChoice(row.ModelId, SelectedProviderRow?.Ranked.Entry.RoutingSlug));
  }

  [ObservableProperty]
  public partial ProviderEvidenceRow? SelectedProviderRow { get; set; }

  partial void OnSelectedRowChanged(ModelPickerRow? value)
  {
    RebuildProviderRows();
    // Pre-select the committed provider when the model is the one it was committed for.
    // Pre-select by routing slug first, then by provider name, so a pin committed before
    // variants were addressable still restores to its row.
    SelectedProviderRow = value?.ModelId == _currentModelId
        ? ProviderRows.FirstOrDefault(row => row.Ranked.Entry.RoutingSlug == _currentProviderName)
            ?? ProviderRows.FirstOrDefault(row => row.Ranked.Entry.ProviderName == _currentProviderName)
        : null;
  }

  /// <summary>Re-ranks the selected model's provider rows at the current assumption and
  ///     preset. Nothing is filtered (D4): every endpoint of the model stays in the list.
  ///     </summary>
  private void RebuildProviderRows()
  {
    if (SelectedRow is not { } row || SelectedPresetOption is null)
    {
      ProviderRows = [];
      return;
    }

    ProviderEvidenceRow? keep = SelectedProviderRow;
    ProviderRows = ProviderComparison.Rows(row.Endpoints, _report, _rates,
        SelectedPresetOption.Preset);
    OnPropertyChanged(nameof(OrderingCaption));
    // A preset or rate change reorders; it must not lose the row the user was looking at.
    // Identity is the ENDPOINT, so the row is restored by its key rather than its provider
    // name - several rows share one name, and a name match would move the selection to
    // whichever variant happened to sort first.
    SelectedProviderRow = keep is null
        ? null
        : ProviderRows.FirstOrDefault(candidate => candidate.Ranked.Entry.Key == keep.Ranked.Entry.Key);
  }

  private void RebuildFilteredRows()
  {
    string needle = SearchText.Trim();
    List<ModelPickerRow> rows = [];
    rows.AddRange(needle.Length == 0
        ? ModelRows
        : ModelRows.Where(r => r.ModelId!.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    // The selection is captured BEFORE the list identity changes: the bound list control
    // drops its selection when ItemsSource is replaced and writes null straight back into
    // SelectedRow (synchronously, inside the notification below).
    ModelPickerRow? keep = SelectedRow;
    FilteredRowsList = rows;
    OnPropertyChanged(nameof(FilteredRows));
    // ...so it is re-resolved AFTER, by MODEL ID (the row objects are new: their details
    // were re-rendered from the rates just resolved). Without this the window opened with
    // nothing selected - the catalog load pre-selected the served model, the ledger read
    // then re-rendered the level and silently dropped it, so the provider level never
    // appeared and a save reported Unchanged for a pair the user could not see. This is
    // the same restore-by-key rule RebuildProviderRows already follows for its level.
    SelectedRow = keep is null
        ? null
        : rows.FirstOrDefault(row => row.ModelId == keep.ModelId);
  }

  /// <summary>The model level, deduped by model id. Each row is summarised by its best
  ///     provider under the current preset and assumption, so the model level answers 'which
  ///     model, and what would it cost me' without a drill-in.</summary>
  private IReadOnlyList<ModelPickerRow> BuildRows(IReadOnlyList<ModelProviderEntry> entries)
  {
    ProviderPreset preset = SelectedPresetOption?.Preset ?? ProviderPreset.Cheapest;
    return
    [
      .. entries
        .GroupBy(e => e.ModelId, StringComparer.Ordinal)
        .Select(group =>
        {
          IReadOnlyList<ModelProviderEntry> endpoints = [.. group];
          IReadOnlyList<ProviderEvidenceRow> ranked = ProviderComparison.Rows(
              endpoints, _report, _rates, preset);
          return new ModelPickerRow(group.Key, group.Key,
              FormatDetail(ranked, endpoints.Count), endpoints);
        })
        .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
    ];
  }

  /// <summary>The model row's one-line summary: the best provider under the current preset,
  ///     its effective price, and how many providers the model offers.
  ///     <para>THREE PRICING STATES STAY DISTINCT (issue #289): a price the catalog
  ///     advertises, a price set at routing time (negative), and a price nobody advertised
  ///     (null). Rendering an unknown price as $0 would tell the user a paid model is free,
  ///     and rendering a negative one as dollars would invent a price the router decides.
  ///     </para></summary>
  private static string FormatDetail(IReadOnlyList<ProviderEvidenceRow> ranked, int providerCount)
  {
    if (ranked.Count == 0)
    {
      return $"{ProviderEvidenceRow.PricingUnknown} · {providerCount} provider(s)";
    }

    ProviderEvidenceRow best = ranked[0];
    ModelProviderEntry entry = best.Ranked.Entry;
    string pricing = (entry.PromptPricePerToken, entry.CompletionPricePerToken) switch
    {
      (null, _) or (_, null) => ProviderEvidenceRow.PricingUnknown,
      ( < 0m, _) or (_, < 0m) => "pricing varies",
      _ => best.EffectivePriceText,
    };
    string provider = entry.PromptPricePerToken is null or < 0m
        ? ""
        : best.ProviderName + " · ";
    return $"{provider}{pricing} · {FormatContext(entry.ContextLength)} · {providerCount} provider(s)";
  }

  private static string FormatContext(int tokens)
      => tokens >= 1_000_000
          ? (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
          : (tokens / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K";

  /// <summary>Loads the rates providers publish for their endpoints. Best effort: an empty
  ///     result is a normal state (a host that wired no source, or a source that published
  ///     nothing), and the resolution chain falls through to the documented default.
  ///     <para>OFF THE UI THREAD for the same reason the catalog is: this is HTTP.</para>
  ///     </summary>
  private async Task<IReadOnlyDictionary<string, PublishedProviderStats>> LoadPublishedAsync()
  {
    if (_loadPublished is null)
    {
      return new Dictionary<string, PublishedProviderStats>(StringComparer.Ordinal);
    }

    // Named decision (CA1031): published rates are an enrichment - a fault must leave the
    // picker working with listed prices and the documented default, never fail the dialog.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Task<IReadOnlyDictionary<string, PublishedProviderStats>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _loadPublished(CancellationToken.None));
      }

      return await scheduled;
    }
    catch (Exception)
    {
      return new Dictionary<string, PublishedProviderStats>(StringComparer.Ordinal);
    }
#pragma warning restore CA1031
  }

  /// <summary>The display name of a sort dimension, for the ordering caption.</summary>
  private static string DimensionName(ProviderDimension dimension) => dimension switch
  {
    ProviderDimension.EffectiveInputPrice => "effective price",
    ProviderDimension.Latency => "latency",
    ProviderDimension.Throughput => "throughput",
    ProviderDimension.Reliability => "reliability",
    ProviderDimension.Intelligence => "intelligence",
    ProviderDimension.Coding => "coding score",
    ProviderDimension.Agentic => "agentic score",
    _ => dimension.ToString(),
  };
}
