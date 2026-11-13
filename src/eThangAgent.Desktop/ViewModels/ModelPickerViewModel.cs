using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;


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

/// <summary>One row of the ordered selection: the model, the place it runs in the list, and the
///     notice that its context window is narrower than the model that runs.
///     <para>THE POSITION IS DERIVED, NEVER STORED: the list IS the order, so a row's number is
///     its index plus one and a move renumbers every row after it.</para>
///     <para><see cref="Warning"/> IS NULL WHEN THERE IS NOTHING TO SAY. A window no catalog
///     entry reports is UNKNOWN, not small, so an unsized backup carries no notice.</para>
///     </summary>
/// <param name="Position">The row's 1-based place in the list; 1 is the model that runs.</param>
/// <param name="ModelId">The base catalog id this row stands for.</param>
/// <param name="DisplayName">The row's caption (the model id).</param>
/// <param name="Detail">The one-line summary (price, context window, endpoint count), or the
///     catalog-unknown text when the catalog lists no endpoint for the model.</param>
/// <param name="Warning">The smaller-window notice, or null.</param>
internal sealed record SelectedModelRow(
    int Position,
    string ModelId,
    string DisplayName,
    string Detail,
    string? Warning);

/// <summary>One row of the variant dropdown: the variant and its bare name. The tradeoff the
///     name stands for is stated ONCE in the caption beneath the dropdown
///     (<see cref="ModelPickerViewModel.VariantCaption"/>), not repeated in every row.
///     </summary>
internal sealed record VariantOption(string DisplayName, ModelVariant Variant);

/// <summary>One row of the retention dropdown: the tier and its short name. What the tier
///     actually promises is stated ONCE in the caption beneath the dropdown
///     (<see cref="ModelPickerViewModel.RetentionCaption"/>), not repeated in every row.
///     </summary>
internal sealed record RetentionOption(string DisplayName, RetentionPolicy Policy);

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
/// <param name="CurrentModelIds">The session's live model list, to pre-select in order.</param>
/// <param name="RefreshCatalog">Joins the catalog's source revalidation, or null to skip
///     it.</param>
/// <param name="CurrentVariant">The session's committed variant, to pre-select in the
///     dropdown.</param>
/// <param name="CurrentRetention">The session's live retention tier, to pre-select in the
///     dropdown. Defaults to the strict tier, which is what an unstated policy means.</param>
internal sealed record ModelSectionSeams(
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> LoadCatalog,
    IReadOnlyList<string>? CurrentModelIds = null,
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? RefreshCatalog = null,
    ModelVariant? CurrentVariant = null,
    RetentionPolicy CurrentRetention = RetentionPolicy.ZeroDataRetention);

/// <summary>View-model behind the model section: the ORDERED selection (entry 1 runs, the rest are
///     ordered backups), the catalog search that feeds additions, and the routing-variant and
///     retention dropdowns that cover the whole list.
///     <para>THE ORDER IS THE DECISION, so the list is single-select with explicit move buttons
///     rather than a multi-select whose tick order the user could neither see nor predict.</para>
///     <para>THERE IS NO PROVIDER LEVEL: an endpoint is a decision the harness measures and ranks,
///     not one the user is asked to make before a single turn runs. The variant dropdown states
///     the tradeoff instead, and the endpoint statistics are still computed and STORED per call
///     by the usage ledger; they simply do not gate a turn on a dropdown.</para>
///     <para>EVERY PRICE STILL COMES FROM THE DOMAIN: the model row is summarised through
///     <see cref="ProviderEvidenceRow"/>, so the three pricing states (advertised, set at
///     routing time, unknown) stay distinct and nothing here prints a zero where a measurement
///     is missing.</para></summary>
internal sealed partial class ModelPickerViewModel : ObservableObject
{
  private readonly Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> _loadCatalog;
  private readonly List<string> _modelIds;
  private IReadOnlyList<ModelPickerRow> ModelRows { get; set; } = [];
  private IReadOnlyList<ModelPickerRow> FilteredRowsList { get; set; } = [];
  private Dictionary<string, IReadOnlyList<ModelProviderEntry>> _endpointsByModel = [];
  private bool _loaded;


  /// <summary>Appends the catalog row under the search box to the ordered list.</summary>
  public IRelayCommand AddModelCommand { get; }

  /// <summary>Swaps <see cref="SelectedModel"/> with the row above it.</summary>
  public IRelayCommand MoveUpCommand { get; }

  /// <summary>Swaps <see cref="SelectedModel"/> with the row below it.</summary>
  public IRelayCommand MoveDownCommand { get; }

  /// <summary>Drops <see cref="SelectedModel"/> from the ordered list.</summary>
  public IRelayCommand RemoveModelCommand { get; }

  /// <summary>The ordered selection the save carries: entry 1 runs, the rest are the ordered
  ///     backups. The COLLECTION IDENTITY NEVER CHANGES - rows are replaced in place so a bound
  ///     list control keeps its selection across a refresh (the click-twice desync that
  ///     <see cref="FilteredRows"/> carries the fix for).</summary>
  public ObservableCollection<SelectedModelRow> SelectedModels { get; } = [];

  /// <summary>The ordered selection the section currently shows, as the value it is saved as:
  ///     the ids in display order under the chosen variant and tier. Two sections holding the
  ///     same ids in different orders state DIFFERENT selections, because entry 1 is the model
  ///     that runs.</summary>
  public ModelSelection CurrentSelection()
      => new([.. SelectedModels.Select(row => row.ModelId)], SelectedVariantOption.Variant, Retention);

  /// <summary>The ordered row a move or a remove acts on.</summary>
  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
  [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
  [NotifyCanExecuteChangedFor(nameof(RemoveModelCommand))]
  public partial SelectedModelRow? SelectedModel { get; set; }

  /// <summary>The reason the last add or remove was refused, or null when it was accepted.
  ///     <para>A REFUSAL IS SHOWN, NOT SWALLOWED: the list cannot be emptied and cannot hold the
  ///     same model twice, and both refusals name the reason so the button never looks dead.</para>
  ///     </summary>
  [ObservableProperty]
  public partial string? SelectionError { get; set; }

  [ObservableProperty]
  public partial string SearchText { get; set; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string? LoadError { get; set; }

  /// <summary>The filtered list is rebuilt ONLY here - selection changes must never swap
  ///     ItemsSource (see <see cref="FilteredRows"/>).</summary>
  partial void OnSearchTextChanged(string value) => RebuildFilteredRows();

  /// <summary>The catalog row under the search box: the SOURCE of additions, never itself the
  ///     decision. It keeps its own selection when a model is added, so several can be added in
  ///     a row.</summary>
  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(AddModelCommand))]
  public partial ModelPickerRow? SelectedRow { get; set; }

  /// <summary>The models matching the search text, case-insensitive on the model id. The list
  ///     identity is STABLE between rebuilds: <see cref="SelectedRow"/> deliberately does not
  ///     raise it - swapping ItemsSource on every selection change would reset the ListBox
  ///     mid-click, lose the row the user just picked, and write a stale selection back into this
  ///     property (the click-twice desync, headless-tested in ModelListSectionTests).</summary>
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

  /// <summary>What this session allows the provider to keep of its prompts.
  ///     <para>THE SECOND AXIS OF THE SAME DECISION, and independent of the variant: the
  ///     dropdown states which tradeoff to prefer, this states which endpoints are admissible
  ///     at all. It opens on the strict tier and only an explicit loosening moves it, so a
  ///     session that never visits this window cannot silently send its prompts to a retaining
  ///     provider.</para>
  ///     <para>THE FIRST TWO TIERS ARE HARD: when the catalog shows no eligible endpoint for
  ///     the chosen model, the turn fails and names the tier rather than falling back to a
  ///     provider that keeps the prompts. That is the one routing preference in this harness
  ///     that must not degrade silently.</para></summary>
  [ObservableProperty]
  public partial RetentionPolicy Retention { get; set; }

  /// <summary>The row the dropdown highlights, derived from <see cref="Retention"/>. The rows are
  ///     rebuilt on every catalog refresh, so the selection is carried as a VALUE and never as a
  ///     row identity - a stale row reference would show a tier the session is not on.</summary>
  public RetentionOption SelectedRetentionOption
  {
    get => RetentionOptions.First(option => option.Policy == Retention);
    set => Retention = value.Policy;
  }

  partial void OnRetentionChanged(RetentionPolicy value)
      => OnPropertyChanged(nameof(SelectedRetentionOption));

  /// <summary>The retention rows in the order the tiers are offered (the dropdown's
  ///     ItemsSource).</summary>
  public IReadOnlyList<RetentionOption> RetentionOptions { get; } =
  [
    .. RetentionPolicies.All.Select(policy => new RetentionOption(RetentionPolicies.DisplayName(policy), policy)),
  ];

  /// <summary>The caption beneath the dropdown: what each tier promises, and which of them the
  ///     harness enforces.</summary>
  public static string RetentionCaption => "ZDR keeps nothing after processing. Safety Retention "
      + "may keep prompts only to detect and prevent abuse, never for training. Retention and "
      + "Training places no restriction, so the provider's own policy applies and may include "
      + "training. The first two are enforced: a model with no eligible endpoint fails the turn "
      + "rather than routing to a provider that keeps your prompts. Whether Safety Retention "
      + "endpoints are admitted also depends on your OpenRouter account setting.";

  /// <summary>The explanation rendered UNDERNEATH the dropdown, and the reason the rows
  ///     themselves are bare names: it states what each variant actually trades and costs, so the
  ///     dropdown is never a mystery box without lengthening every row and the status bar.</summary>
  public static string VariantCaption => "Nitro admits priority-tier endpoints (billed at priority "
      + "rates) and sorts by throughput; Floor admits flex-tier endpoints (billed at their "
      + "discounted rate) and sorts by price. Cache Floor admits flex-tier endpoints too, but "
      + "orders them by advertised cache-read price, so a session whose prompts are cached "
      + "heavily prefers the cheapest cache reads. Billing always follows the tier that actually "
      + "served, and the usage ledger records the prices it served at.";

  /// <param name="loadCatalog">Loads the open session's provider catalog (UI thread escape
  ///     handled here - the delegate itself may block on HTTP).</param>
  /// <param name="currentModelIds">The session's live ordered model list, seeded into the
  ///     numbered list in order. Empty seeds an empty list.</param>
  /// <param name="currentVariant">The session's live routing variant to pre-select (null
  ///     pre-selects Standard).</param>
  /// <param name="currentRetention">The session's live retention tier to pre-select.
  ///     Defaults to the strict tier, which is what an unstated policy means.</param>
  public ModelPickerViewModel(
      Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> loadCatalog,
      IReadOnlyList<string> currentModelIds,
      ModelVariant? currentVariant = null,
      RetentionPolicy currentRetention = RetentionPolicy.ZeroDataRetention)
  {
    _loadCatalog = loadCatalog ?? throw new ArgumentNullException(nameof(loadCatalog));
    _modelIds = [.. (currentModelIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())];

    // The commands exist before the observable properties: setting those raises the changed
    // hooks, which requery command availability. The guard in the action is load-bearing:
    // ICommand.Execute does not consult CanExecute, and a disabled button is only one of several
    // ways this command can be invoked.
    AddModelCommand = new RelayCommand(AddModel, () => SelectedRow is not null);
    MoveUpCommand = new RelayCommand(MoveUp, () => SelectedModel is { Position: > 1 });
    MoveDownCommand = new RelayCommand(MoveDown, () => SelectedModel is { } row && row.Position < SelectedModels.Count);
    RemoveModelCommand = new RelayCommand(RemoveModel, () => SelectedModel is not null);
    SelectedVariantOption = VariantOptions.FirstOrDefault(o => o.Variant == currentVariant)
        ?? VariantOptions[0];
    Retention = currentRetention;
    RenderSelectedModels();
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

      _endpointsByModel = BuildEndpointIndex(catalog.Value);
      ModelRows = BuildRows(catalog.Value);
      RebuildFilteredRows();
      // Pre-select the head of the session's live list, when the catalog lists it.
      SelectedRow = FilteredRows.FirstOrDefault(r => r.ModelId == _modelIds.FirstOrDefault());
      RenderSelectedModels();
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
    _endpointsByModel = BuildEndpointIndex(result.Value);
    ModelRows = BuildRows(result.Value);
    RebuildFilteredRows();
    SelectedRow = selected is null ? null
        : FilteredRows.FirstOrDefault(row => row.ModelId == selected.ModelId) ?? selected;
    // Every selected row's details and warnings are re-read from the updated catalog; the ORDER
    // and the selected row survive because the rows are replaced in place.
    SelectedModelRow? keep = SelectedModel;
    RenderSelectedModels();
    SelectedModel = keep is null ? null : SelectedModels.FirstOrDefault(r => r.Position == keep.Position);
    LoadError = null;
  }

  /// <summary>Appends the catalog row under the search box as the LAST entry of the ordered list.
  ///     <para>A REPEATED MODEL IS REFUSED AND NAMED rather than appended twice: the second
  ///     occurrence can never serve, because the first already covered it.</para>
  ///     <para>NOTHING SELECTED IN THE CATALOG IS NOT AN ERROR - it is the state the section opens
  ///     in, and reporting it as a failure would put a message under a list the user has not
  ///     tried to change.</para></summary>
  private void AddModel()
  {
    if (SelectedRow is not { ModelId: { } modelId })
    {
      return;
    }

    if (_modelIds.Contains(modelId, StringComparer.Ordinal))
    {
      SelectionError = $"{modelId} is already in the list; a model that appears twice can never be "
          + "the backup that runs.";
      return;
    }

    SelectionError = null;
    _modelIds.Add(modelId);
    SelectedModels.Add(BuildSelectedRow(_modelIds.Count - 1));
    SelectedModel = SelectedModels[^1];
  }

  /// <summary>Swaps the selected row with the one above it. The first row has nowhere to go, so the
  ///     command is unavailable there and the action returns without raising an error.</summary>
  private void MoveUp()
  {
    if (SelectedModel is not { } row || row.Position <= 1)
    {
      return;
    }

    Swap(row.Position - 1, row.Position - 2);
  }

  /// <summary>Swaps the selected row with the one below it. The last row has nowhere to go.</summary>
  private void MoveDown()
  {
    if (SelectedModel is not { } row || row.Position >= _modelIds.Count)
    {
      return;
    }

    Swap(row.Position - 1, row.Position);
  }

  /// <summary>Drops the selected row from the ordered list.
  ///     <para>THE LAST ROW IS REFUSED WITH ITS REASON: a session with no model has nothing to
  ///     serve, so there is no empty state to return to and the refusal names the rule instead of
  ///     leaving the button looking dead.</para></summary>
  private void RemoveModel()
  {
    if (SelectedModel is not { } row)
    {
      return;
    }

    if (_modelIds.Count <= 1)
    {
      SelectionError = "At least one model is required; add another model before removing this one.";
      return;
    }

    SelectionError = null;
    _modelIds.RemoveAt(row.Position - 1);
    int index = row.Position - 1;
    SelectedModels.RemoveAt(index);
    for (int i = index; i < _modelIds.Count; i++)
    {
      SelectedModels[i] = BuildSelectedRow(i);
    }

    SelectedModel = SelectedModels.Count == 0 ? null : SelectedModels[Math.Min(index, SelectedModels.Count - 1)];
  }

  /// <summary>Moves entry <paramref name="from"/> to <paramref name="to"/> and re-renders every row
  ///     whose number changed, so the list never shows a gap or a repeated number.</summary>
  private void Swap(int from, int to)
  {
    SelectionError = null;
    (_modelIds[from], _modelIds[to]) = (_modelIds[to], _modelIds[from]);
    SelectedModels[to] = BuildSelectedRow(to);
    SelectedModels[from] = BuildSelectedRow(from);
    SelectedModel = SelectedModels[to];
  }

  /// <summary>Replaces every row's VALUE in place from the current catalog, keeping the collection
  ///     identity and the row count. Prices, windows, and warnings are re-read here and nowhere
  ///     else.</summary>
  private void RenderSelectedModels()
  {
    for (int i = 0; i < _modelIds.Count; i++)
    {
      SelectedModelRow rendered = BuildSelectedRow(i);
      if (i < SelectedModels.Count)
      {
        SelectedModels[i] = rendered;
      }
      else
      {
        SelectedModels.Add(rendered);
      }
    }
  }


  /// <summary>One numbered row built from the current catalog: the cheapest endpoint's summary, and
  ///     the smaller-window notice when this model's window is narrower than the first entry's.
  ///     <para>AN UNKNOWN WINDOW IS NOT A SMALL ONE. Only two POSITIVE windows can be compared, so
  ///     a model the catalog sizes nowhere carries no notice and neither does the head.</para>
  ///     </summary>
  private SelectedModelRow BuildSelectedRow(int index)
  {
    string modelId = _modelIds[index];
    IReadOnlyList<ModelProviderEntry> endpoints = EndpointsOf(modelId);
    string detail = FormatDetail(endpoints);
    return new SelectedModelRow(
        index + 1,
        modelId,
        modelId,
        index == 0 ? $"{detail} · runs first" : detail,
        WindowWarning(index));
  }

  /// <summary>The smaller-window notice for one row, or null. The comparison is against the FIRST
  ///     entry, because entry 1's window is what governs the session's budget.</summary>
  private string? WindowWarning(int index)
  {
    if (index == 0 || _modelIds.Count == 0)
    {
      return null;
    }

    int head = CheapestWindowOf(_modelIds[0]);
    int thisWindow = CheapestWindowOf(_modelIds[index]);
    return head > 0 && thisWindow > 0 && thisWindow < head
        ? $"Context window {FormatContext(thisWindow)} is smaller than {_modelIds[0]}'s "
            + $"{FormatContext(head)}; this backup cannot carry a conversation that fills entry 1's "
            + "window."
        : null;
  }

  /// <summary>The context window the model's cheapest endpoint reports, or 0 when the catalog
  ///     lists no endpoint for it or reports none.</summary>
  private int CheapestWindowOf(string modelId)
  {
    IReadOnlyList<ModelProviderEntry> endpoints = EndpointsOf(modelId);
    if (endpoints.Count == 0)
    {
      return 0;
    }

    ModelProviderEntry? priced = endpoints
        .Where(e => e.PromptPricePerToken is >= 0m)
        .OrderBy(e => e.PromptPricePerToken ?? decimal.MaxValue)
        .FirstOrDefault();
    return (priced ?? endpoints[0]).ContextLength;
  }

  /// <summary>Every catalog row for one model id, keyed on the BASE id so a stored id carrying a
  ///     routing-variant suffix finds the same endpoints its base model does. Empty when the
  ///     catalog lists none - a saved model the catalog does not currently offer is shown, not
  ///     dropped.</summary>
  private IReadOnlyList<ModelProviderEntry> EndpointsOf(string modelId)
    => _endpointsByModel.TryGetValue(ModelVariants.BaseOf(modelId), out IReadOnlyList<ModelProviderEntry>? endpoints)
        ? endpoints
        : [];

  /// <summary>The catalog indexed by base model id, built once per catalog load so a row's summary
  ///     costs a lookup instead of a scan.</summary>
  private static Dictionary<string, IReadOnlyList<ModelProviderEntry>> BuildEndpointIndex(
      IReadOnlyList<ModelProviderEntry> entries)
    => entries
        .GroupBy(e => ModelVariants.BaseOf(e.ModelId), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => (IReadOnlyList<ModelProviderEntry>)[.. group],
            StringComparer.Ordinal);

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
