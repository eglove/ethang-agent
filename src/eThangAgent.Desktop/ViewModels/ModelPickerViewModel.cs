using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The choice a confirm carries: a concrete model id plus the routing VARIANT it should
///     be served under. (A cancelled dialog closes with no result at all.)</summary>
internal sealed record ModelChoice(string? ModelId, ModelVariant Variant = ModelVariant.Standard);

/// <summary>One choosable row of the model list: a concrete model, deduped across the catalog's
///     endpoint rows, summarised by its cheapest endpoint.
///     <para>WHY THE LIST IS THE MODEL AND NOT ONE ROW PER ENDPOINT: a model is served by dozens
///     of endpoints that differ in price, region, and tier. Comparing them was a decision the
///     harness had already measured and could rank, and it asked the user to make it before a
///     single turn ran. The one thing measurement cannot infer is the tradeoff they want — speed
///     or price — and OpenRouter publishes exactly that as three routing variants. So the list
///     names models, and the variant dropdown states the tradeoff.</para>
///     <para>THE DETAIL STILL NAMES THE PRICE, because it is the figure a model choice is made
///     on, and the row never hides which endpoint it is quoting.</para></summary>
/// <param name="ModelId">The model this row stands for (the base catalog id).</param>
/// <param name="DisplayName">The row's caption (the model id).</param>
/// <param name="Detail">The one-line summary: cheapest effective price, context window, and how
///     many endpoints the model offers.</param>
/// <param name="Endpoints">Every catalog row for this model (the price summary's input).</param>
internal sealed record ModelPickerRow(
    string? ModelId,
    string DisplayName,
    string Detail,
    IReadOnlyList<ModelProviderEntry> Endpoints);

/// <summary>One row of the variant dropdown: the variant, its label, and the tradeoff it states.
///     </summary>
internal sealed record VariantOption(string DisplayName, ModelVariant Variant);

/// <summary>Everything the embedded model section needs from its host, bundled so the settings
///     view-model's constructor stays a readable list of concerns rather than a wall of
///     delegates.
///     <para>EVERY MEMBER IS A SEAM THE HOST OWNS: the catalog load and its revalidation join,
///     and the session's committed choice (to pre-select). The ledger's observed-performance read
///     model is deliberately NOT here any more — with the provider level gone there is no
///     per-endpoint comparison left for it to feed, and a loader nothing renders is wiring that
///     only costs a database query.</para></summary>
/// <param name="LoadCatalog">Loads the provider catalog. Required: without it there is no
///     section at all.</param>
/// <param name="CurrentModelId">The session's live model, to pre-select.</param>
/// <param name="RefreshCatalog">Joins the catalog's source revalidation, or null to skip
///     it.</param>
/// <param name="CurrentVariant">The session's committed variant, to pre-select in the
///     dropdown.</param>
internal sealed record ModelSectionSeams(
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> LoadCatalog,
    string? CurrentModelId = null,
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? RefreshCatalog = null,
    ModelVariant? CurrentVariant = null);

/// <summary>View-model behind the model picker: a searchable model list and the routing-variant
///     dropdown. That is the whole decision.
///     <para>WHAT WAS DELETED AND WHY: the provider level (every endpoint of the selected model
///     with its price, cache discount, latency, throughput, uptime, and capability scores), the
///     four ordering presets, and the ordering caption. Those existed to help the user pick an
///     ENDPOINT, which the harness could rank better than a human could read, and none of it
///     changed the routing intent that actually matters. The stats themselves are untouched —
///     effective pricing and cache-hit rate are still computed and STORED per call by the usage
///     ledger; they simply no longer gate a turn on a dropdown.</para>
///     <para>EVERY PRICE STILL COMES FROM THE DOMAIN: the model row is summarised through
///     <see cref="ProviderEvidenceRow"/>, so the three pricing states (advertised, set at
///     routing time, unknown) stay distinct and nothing here prints a zero where a measurement
///     is missing.</para></summary>
internal sealed partial class ModelPickerViewModel : ObservableObject
{
  private readonly Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> _loadCatalog;
  private readonly string? _currentModelId;
  private IReadOnlyList<ModelPickerRow> ModelRows { get; set; } = [];
  private IReadOnlyList<ModelPickerRow> FilteredRowsList { get; set; } = [];
  private IReadOnlyList<ModelProviderEntry> CatalogEntries { get; set; } = [];
  private bool _loaded;

  /// <summary>Raised when the user confirms a row; carries the model + variant. The view closes
  ///     the dialog with it.</summary>
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

  /// <summary>The models matching the search text, case-insensitive on the model id. The list
  ///     identity is STABLE between rebuilds: <see cref="SelectedRow"/> deliberately does not
  ///     raise it - swapping ItemsSource on every selection change would reset the ListBox
  ///     mid-click, lose the row the user just picked, and write a stale selection back into this
  ///     property (the click-twice desync, headless-tested in ModelPickerWindowTests).</summary>
  public IReadOnlyList<ModelPickerRow> FilteredRows => FilteredRowsList;

  /// <summary>The variant rows in display order (the dropdown's ItemsSource).</summary>
  public IReadOnlyList<VariantOption> VariantOptions { get; } =
  [
    .. ModelVariants.All.Select(variant => new VariantOption(ModelVariants.Describe(variant), variant)),
  ];

  /// <summary>The selected routing variant (two-way bound). It is the SECOND half of the choice
  ///     and travels with the model on confirm.
  ///     <para>PRESELECTED FROM THE SESSION so reopening the picker shows the tradeoff it
  ///     committed, rather than silently resetting a nitro session to standard on the next
  ///     save.</para></summary>
  [ObservableProperty]
  public partial VariantOption SelectedVariantOption { get; set; }

  /// <summary>States what the variant actually costs, so the dropdown is never a mystery box:
  ///     the two non-default variants admit a different service tier, and that tier is billed
  ///     differently.</summary>
  public static string VariantCaption => "Nitro admits priority-tier endpoints (billed at priority "
      + "rates) and sorts by throughput; Floor admits flex-tier endpoints (billed at their "
      + "discounted rate) and sorts by price. Billing always follows the tier that actually "
      + "served, and the usage ledger records the prices it served at.";

  /// <param name="loadCatalog">Loads the open session's provider catalog (UI thread escape
  ///     handled here - the delegate itself may block on HTTP).</param>
  /// <param name="currentModelId">The session's live model choice to pre-select (null
  ///     pre-selects nothing).</param>
  /// <param name="currentVariant">The session's live routing variant to pre-select (null
  ///     pre-selects Standard).</param>
  public ModelPickerViewModel(
      Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> loadCatalog,
      string? currentModelId,
      ModelVariant? currentVariant = null)
  {
    _loadCatalog = loadCatalog ?? throw new ArgumentNullException(nameof(loadCatalog));
    _currentModelId = currentModelId;

    // The commands exist before the observable properties: setting those raises the changed
    // hooks, which requery command availability. The guard in the action is load-bearing:
    // ICommand.Execute does not consult CanExecute, and a disabled button is only one of several
    // ways this command can be invoked.
    ConfirmCommand = new RelayCommand(Confirm, () => SelectedRow is not null);
    SelectedVariantOption = VariantOptions.FirstOrDefault(o => o.Variant == currentVariant)
        ?? VariantOptions[0];
    SearchText = string.Empty; // raises OnSearchTextChanged → initial list
  }

  /// <summary>Fetches the catalog off the UI thread and fills the list. A failure lands in
  ///     <see cref="LoadError"/> (the dialog stays open; the user can cancel). Only the first call
  ///     loads.</summary>
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
      // HTTP plus (for OpenRouter) a per-model endpoint crawl - never on the UI thread. Context
      // flow is suppressed alongside the thread switch, mirroring MainViewModel's session-open
      // schedule.
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
      ModelRows = BuildRows(CatalogEntries);
      RebuildFilteredRows();
      // Pre-select the session's live choice, when it is in the list.
      SelectedRow = FilteredRows.FirstOrDefault(r => r.ModelId == _currentModelId);
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

  /// <summary>Applies a catalog refresh (the settings dialog's revalidation join): the rows are
  ///     rebuilt and the selection preserved, so a refresh never invents a user edit.</summary>
  internal void ApplyRefresh(Result<IReadOnlyList<ModelProviderEntry>> result)
  {
    if (!result.IsSuccess)
    {
      LoadError = $"Catalog refresh failed; showing cached models. {result.Error.Message}";
      return;
    }

    ModelPickerRow? selected = SelectedRow;
    CatalogEntries = result.Value;
    ModelRows = BuildRows(CatalogEntries);
    RebuildFilteredRows();
    SelectedRow = selected is null ? null
        : FilteredRows.FirstOrDefault(row => row.ModelId == selected.ModelId) ?? selected;
    LoadError = null;
  }

  private void Confirm()
  {
    if (SelectedRow is not { } row)
    {
      return;
    }

    // The variant is the SECOND half of the decision and travels with the model. Committing only
    // the model would leave the turn on standard routing while the dialog reported a tradeoff the
    // user chose - the same class of lie as a status line naming a routing the request never
    // carried.
    ConfirmRequested?.Invoke(this, new ModelChoice(row.ModelId, SelectedVariantOption.Variant));
  }

  private void RebuildFilteredRows()
  {
    string needle = SearchText.Trim();
    List<ModelPickerRow> rows = [];
    rows.AddRange(needle.Length == 0
        ? ModelRows
        : ModelRows.Where(r => r.ModelId!.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    // The selection is captured BEFORE the list identity changes: the bound list control drops
    // its selection when ItemsSource is replaced and writes null straight back into SelectedRow
    // (synchronously, inside the notification below).
    ModelPickerRow? keep = SelectedRow;
    FilteredRowsList = rows;
    OnPropertyChanged(nameof(FilteredRows));
    // ...so it is re-resolved AFTER, by MODEL ID (the row objects are new: their details were
    // re-rendered from the prices just resolved). Without this the window opened with nothing
    // selected.
    SelectedRow = keep is null
        ? null
        : rows.FirstOrDefault(row => row.ModelId == keep.ModelId);
  }

  /// <summary>The model list, deduped by model id. Each row is summarised by its cheapest
  ///     endpoint, so the list answers 'which model, and what would it cost me' without a
  ///     drill-in.</summary>
  private static IReadOnlyList<ModelPickerRow> BuildRows(IReadOnlyList<ModelProviderEntry> entries)
  {
    return
    [
      .. entries
        .GroupBy(e => e.ModelId, StringComparer.Ordinal)
        .Select(group =>
        {
          IReadOnlyList<ModelProviderEntry> endpoints = [.. group];
          return new ModelPickerRow(group.Key, group.Key,
              FormatDetail(endpoints), endpoints);
        })
        .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
    ];
  }

  /// <summary>The model row's one-line summary: the cheapest endpoint's price, the context
  ///     window, and how many endpoints the model offers.
  ///     <para>THREE PRICING STATES STAY DISTINCT (issue #289): a price the catalog advertises, a
  ///     price set at routing time (negative), and a price nobody advertised (null). Rendering an
  ///     unknown price as $0 would tell the user a paid model is free, and rendering a negative
  ///     one as dollars would invent a price the router decides.</para></summary>
  private static string FormatDetail(IReadOnlyList<ModelProviderEntry> endpoints)
  {
    if (endpoints.Count == 0)
    {
      return $"{ProviderEvidenceRow.PricingUnknown}";
    }

    // Cheapest advertised endpoint first; an absent price sorts last because 'unknown' is never
    // 'cheapest'.
    ModelProviderEntry? priced = endpoints
        .Where(e => e.PromptPricePerToken is >= 0m)
        .OrderBy(e => e.PromptPricePerToken ?? decimal.MaxValue)
        .FirstOrDefault();
    ModelProviderEntry cheapest = priced ?? endpoints[0];
    string pricing = (cheapest.PromptPricePerToken, cheapest.CompletionPricePerToken) switch
    {
      (null, _) or (_, null) => ProviderEvidenceRow.PricingUnknown,
      ( < 0m, _) or (_, < 0m) => "pricing varies",
      _ => FormatPrice(cheapest),
    };
    return $"{pricing} · {FormatContext(cheapest.ContextLength)} · {endpoints.Count} endpoint(s)";
  }

  /// <summary>The cheapest endpoint's blended input price, rendered at the documented cache
  ///     assumption. A price nobody advertised is never rendered as money.</summary>
  private static string FormatPrice(ModelProviderEntry entry)
  {
    EffectiveInputPrice? effective = EffectivePrice.For(entry, HitRateAssumption.Fallback.Rate, 0);
    return effective is { } value
        ? value.ReadPerInputToken!.Value.ToString("$0.######", CultureInfo.InvariantCulture) + "/tok in"
        : ProviderEvidenceRow.PricingUnknown;
  }

  private static string FormatContext(int tokens)
      => tokens >= 1_000_000
          ? (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
          : (tokens / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K";
}
