using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One choosable row of the model list: one model served by one vendor, summarised by
///     that vendor's cheapest endpoint for the model.
///     <para>THE VENDOR IS PART OF THE ROW, because two vendors may serve the same model id at
///     different prices, and the row states which one it quotes and which one an add would
///     commit.</para></summary>
/// <param name="ModelId">The model's base catalog id.</param>
/// <param name="Vendor">The vendor id that serves this row.</param>
/// <param name="DisplayName">The row's caption: the vendor's name and the model id.</param>
/// <param name="Detail">The price this vendor publishes, the context window, and how many
///     endpoints of the model the vendor lists.</param>
/// <param name="Endpoints">Every catalog row of this vendor for this model.</param>
/// <param name="RequiresPaidPlan">The vendor's paid-plan marker, or null when it stated
///     none.</param>
/// <param name="Refusal">Why this row cannot be added, or null when it can.</param>
internal sealed record ModelPickerRow(
    string? ModelId,
    string Vendor,
    string DisplayName,
    string Detail,
    IReadOnlyList<ModelProviderEntry> Endpoints,
    bool? RequiresPaidPlan,
    string? Refusal)
{
  /// <summary>True when the row can be added to the ordered list.</summary>
  public bool IsAddable => Refusal is null;

  /// <summary>True when the vendor states this model needs a paid plan. A vendor that said
  ///     nothing produces no marker.</summary>
  public bool ShowsPaidPlanMarker => RequiresPaidPlan is true;

  /// <summary>The marker's text, or null when there is none to show.</summary>
  public string? PaidPlanMarker => ShowsPaidPlanMarker ? "needs a paid plan" : null;
}

/// <summary>One row of the ordered selection: the model, the vendor that serves it, the place it
///     runs in the list, and the notice that its context window is narrower than the model that
///     runs.
///     <para>THE POSITION IS DERIVED, NEVER STORED: the list IS the order, so a row's number is
///     its index plus one and a move renumbers every row after it.</para>
///     <para><see cref="Warning"/> IS NULL WHEN THERE IS NOTHING TO SAY. A window no catalog
///     entry reports is UNKNOWN, not small, so an unsized backup carries no notice.</para>
///     </summary>
/// <param name="Position">The row's 1-based place in the list; 1 is the model that runs.</param>
/// <param name="ModelId">The base catalog id this row stands for.</param>
/// <param name="Vendor">The vendor this entry names, or null when it names none.</param>
/// <param name="DisplayName">The row's caption: the vendor's name and the model id.</param>
/// <param name="Detail">The one-line summary (price, context window, endpoint count), or the
///     catalog-unknown text when no catalog lists the model.</param>
/// <param name="Warning">The smaller-window notice, or null.</param>
internal sealed record SelectedModelRow(
    int Position,
    string ModelId,
    string? Vendor,
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
///     <para>EVERY MEMBER IS A SEAM THE HOST OWNS: the per-vendor catalog load and its
///     revalidation join, and the session's committed choice (to pre-select).</para></summary>
/// <param name="LoadCatalog">Loads one vendor's catalog. Required: without it there is no
///     section at all.</param>
/// <param name="CurrentEntries">The session's live ordered list, to pre-select in order.
/// </param>
/// <param name="RefreshCatalog">Joins one vendor's source revalidation, or null to skip it.
/// </param>
/// <param name="CurrentVariant">The session's committed variant, to pre-select in the
///     dropdown.</param>
/// <param name="CurrentRetention">The session's live retention tier, to pre-select in the
///     dropdown. Defaults to the strict tier, which is what an unstated policy means.</param>
/// <param name="LoadCheapestEvidence">Reads the usage evidence the Cheapest sentence is built
///     from, or null when no ledger is reachable. Null is an answer, not a fault: the sentence
///     then states the documented default rung rather than inventing a measurement.</param>
/// <param name="Vendors">The vendors whose catalogs this section offers, in display order.
///     Defaults to every vendor this build knows.</param>
/// <param name="SessionVendor">The provider id the session recorded, used as the vendor of an
///     entry that names none.</param>
internal sealed record ModelSectionSeams(
    Func<string, CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> LoadCatalog,
    IReadOnlyList<ModelSelectionEntry>? CurrentEntries = null,
    Func<string, CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? RefreshCatalog = null,
    ModelVariant? CurrentVariant = null,
    RetentionPolicy CurrentRetention = RetentionPolicy.ZeroDataRetention,
    Func<CancellationToken, Task<CheapestEvidence>>? LoadCheapestEvidence = null,
    IReadOnlyList<string>? Vendors = null,
    string? SessionVendor = null);

/// <summary>The usage evidence a Cheapest projection prices at: the ledger's per-provider
///     report and the workspace's median shape, either of which may be absent.
///     <para>EVIDENCE, NOT A VERDICT: the rung that answers and the order that follows are
///     resolved from these by the section itself.</para></summary>
/// <param name="Report">The ledger's per-provider report, or null when none could be read.
///     It is bound to the catalog by the consumer, which is what makes the endpoint rung
///     answerable.</param>
/// <param name="WorkspaceShape">The ledger's median shape, or null when no model in it
///     qualifies.</param>
internal sealed record CheapestEvidence(
    ProviderPerformanceReport? Report,
    CostShape? WorkspaceShape);

/// <summary>View-model behind the model section: the ORDERED selection (entry 1 runs, the rest
///     are ordered backups), the merged search list that feeds additions, and the
///     routing-variant and retention dropdowns that cover the whole list.
///     <para>THE ORDER IS THE DECISION, so the list is single-select with explicit move buttons
///     rather than a multi-select whose tick order the user could neither see nor predict.</para>
///     <para>ONE LIST SPANS EVERY VENDOR: a row is a (vendor, model) pair, so the same model id
///     served by two vendors is two rows that differ in price, and a vendor whose catalog could
///     not be read leaves the other vendor's rows present.</para>
///     <para>EVERY PRICE STILL COMES FROM THE DOMAIN: the model row is summarised through
///     <see cref="ProviderEvidenceRow"/>, so the three pricing states (advertised, set at
///     routing time, unknown) stay distinct and nothing here prints a zero where a measurement
///     is missing.</para></summary>
internal sealed partial class ModelPickerViewModel : ObservableObject
{
  private readonly Func<string, CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> _loadCatalog;
  private readonly Func<CancellationToken, Task<CheapestEvidence>>? _loadCheapestEvidence;
  private readonly string? _sessionVendor;
  private readonly List<ModelSelectionEntry> _entries;
  private readonly Dictionary<string, IReadOnlyList<ModelProviderEntry>> _entriesByVendor = [];
  private IReadOnlyList<ModelPickerRow> ModelRows { get; set; } = [];
  private IReadOnlyList<ModelPickerRow> FilteredRowsList { get; set; } = [];
  private Dictionary<(string Vendor, string ModelId), IReadOnlyList<ModelProviderEntry>> _endpointsByModel = [];
  private CheapestEvidence? _cheapestEvidence;
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
  ///     list control keeps its selection across a refresh.</summary>
  public ObservableCollection<SelectedModelRow> SelectedModels { get; } = [];

  /// <summary>The ordered selection the section currently shows, as the value it is saved as:
  ///     the (model, vendor) entries in display order under the chosen variant and tier. Two
  ///     sections holding the same entries in different orders state DIFFERENT selections,
  ///     because entry 1 is the model that runs.</summary>
  public ModelSelection CurrentSelection()
      => new([.. _entries.Select(entry => new ModelSelectionEntry(entry.ModelId, entry.Vendor))],
          SelectedVariantOption.Variant, Retention);

  /// <summary>The ordered row a move or a remove acts on.</summary>
  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
  [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
  [NotifyCanExecuteChangedFor(nameof(RemoveModelCommand))]
  public partial SelectedModelRow? SelectedModel { get; set; }

  /// <summary>The reason the last add or remove was refused, or null when it was accepted.
  ///     <para>A REFUSAL IS SHOWN, NOT SWALLOWED: the list cannot be emptied, cannot hold the
  ///     same model under the same vendor twice, and cannot hold a model the vendor says cannot
  ///     call tools, and each refusal names the reason so the button never looks dead.</para>
  ///     </summary>
  [ObservableProperty]
  public partial string? SelectionError { get; set; }

  [ObservableProperty]
  public partial string SearchText { get; set; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  /// <summary>The stated failure of a catalog that could not be read, or null when every
  ///     catalog loaded. Rows from the vendors that did load are still shown.</summary>
  [ObservableProperty]
  public partial string? LoadError { get; set; }

  /// <summary>One stated failure per vendor whose catalog could not be read, in vendor order.
  ///     Empty when every catalog loaded.</summary>
  public IReadOnlyList<string> CatalogErrors { get; private set; } = [];

  /// <summary>The vendors whose catalogs this section offers, in display order.</summary>
  public IReadOnlyList<string> Vendors { get; }

  /// <summary>The filtered list is rebuilt ONLY here - selection changes must never swap
  ///     ItemsSource (see <see cref="FilteredRows"/>).</summary>
  partial void OnSearchTextChanged(string value) => RebuildFilteredRows();

  /// <summary>What the Cheapest order is and what it can justify, for the model that runs - or
  ///     null when the section is not on Cheapest, because the other four variants carry no
  ///     projection to explain.
  ///     <para>EVERY FORM NAMES THE RUNG THAT ANSWERED, including the fragile one, because the
  ///     break-even rate only means something at the shape it was solved at.</para>
  ///     <para>THE RANKING COMES FROM THE SAME TWO DOMAIN CALLS THE REQUEST PATH MAKES
  ///     (<see cref="CheapestShapeSource.Resolve"/> then <see cref="CheapestOrder.For"/>), so
  ///     the sentence describes the order the turn sends.</para>
  ///     <para>THE VARIANT IS AN OPENROUTER CONCEPT, so a head entry another vendor serves
  ///     carries no projection and this is null.</para></summary>
  public string? CheapestSentence
  {
    get
    {
      if (SelectedVariantOption.Variant != ModelVariant.Cheapest || HeadEntry is not { } head
          || !ServesOpenRouter(head))
      {
        return null;
      }

      IReadOnlyList<ModelProviderEntry> rows = PricedEndpointsOf(head);
      if (rows.Count == 0)
      {
        return CheapestCouldNotResolve;
      }

      // The endpoint rung is asked for the model's OWN cheapest endpoint, because that is the
      // endpoint an order would name first - so the sentence describes the figure the choice
      // actually rests on rather than a model-wide blend of it.
      CostShapeAssumption assumption = CheapestShapeSource.Resolve(
          _cheapestEvidence?.Report?.BindEndpoints(rows), _cheapestEvidence?.WorkspaceShape, head.ModelId,
          CheapestOrder.For(rows, CostShapeAssumption.Fallback.Shape).WinnerSlug);
      CheapestRanking ranking = CheapestOrder.For(rows, assumption.Shape);
      return ranking.WinnerSlug is { } winner
          ? Sentence(ranking, winner, assumption)
          : CheapestCouldNotResolve;
    }
  }

  /// <summary>The entry that runs: the head of the ordered list, or null when it is empty.
  ///     </summary>
  private ModelSelectionEntry? HeadEntry => _entries.Count > 0 ? _entries[0] : null;

  /// <summary>True when the entry is served by OpenRouter, which is the only vendor that has a
  ///     routing variant to apply.</summary>
  private bool ServesOpenRouter(ModelSelectionEntry entry)
      => string.Equals(VendorOf(entry), ModelVendors.OpenRouter, StringComparison.Ordinal);

  /// <summary>The sentence for a model whose order could not be computed at all: no catalog row
  ///     for it, or none advertising the prices an order needs.</summary>
  private const string CheapestCouldNotResolve =
      "Cheapest could not be resolved for this model, so the provider's own routing applies.";

  /// <summary>The sentence for an order that was computed: the winner, the evidence it rests
  ///     on, and - when the winner does not dominate the runner-up - the hit rate at which the
  ///     choice turns over.</summary>
  private static string Sentence(CheapestRanking ranking, string winner, CostShapeAssumption assumption)
      => (ranking, winner) switch
      {
        ({ IsFragile: false }, _) => $"Cheapest is {winner}. {RungClause(assumption)} {winner} is cheapest at every hit rate.",
        ({ BreakEvenHitRate: { } breakEven, RunnerUpSlug: { } runnerUp, WinnerCheaperBelowBreakEven: true }, _) =>
            $"Cheapest is {winner}, but only below {PercentText(breakEven)} cache-hit rate. "
                + $"Above that, {runnerUp} is cheaper.",
        ({ BreakEvenHitRate: { } breakEven, RunnerUpSlug: { } runnerUp }, _) =>
            $"Cheapest is {winner}, but only above {PercentText(breakEven)} cache-hit rate. "
                + $"Below that, {runnerUp} is cheaper.",
        _ => $"Cheapest is {winner}. {RungClause(assumption)} Whether it stays cheapest depends on "
            + "this workspace's cache-hit rate.",
      };

  /// <summary>The evidence clause: the rung that answered and its figures, or - for the
  ///     documented default - the fact that nothing has been measured.</summary>
  private static string RungClause(CostShapeAssumption assumption)
      => assumption.Source == ShapeSource.Default
          ? $"Nothing has been measured yet, so this uses the default {PercentText(assumption.Shape.HitRate)} "
              + "cache-hit rate;"
          : $"This {RungClauseSubject(assumption.Source)} measured {PercentText(assumption.Shape.HitRate)} "
              + $"cache hits and {RatioText(assumption.Shape.OutputPerInputRatio)} output per input token, and";

  /// <summary>The noun a rung's clause is about: the endpoint, the model, or the workspace whose
  ///     median answered.</summary>
  private static string RungClauseSubject(ShapeSource source) => source switch
  {
    ShapeSource.Endpoint => "endpoint",
    ShapeSource.Model => "model",
    ShapeSource.Workspace => "workspace",
    ShapeSource.Default => "workspace",
    _ => "workspace",
  };

  /// <summary>A rate as a percentage with at most one decimal place, invariant culture, so
  ///     0.715 renders as 71.5% and a documented 0.5 renders as 50%.</summary>
  private static string PercentText(decimal rate)
      => (rate * 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%";

  /// <summary>The output/input ratio at up to six decimal places, invariant culture.</summary>
  private static string RatioText(decimal ratio)
      => ratio.ToString("0.######", CultureInfo.InvariantCulture);

  /// <summary>The catalog row under the search box: the SOURCE of additions, never itself the
  ///     decision. It keeps its own selection when a model is added, so several can be added in
  ///     a row.</summary>
  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(AddModelCommand))]
  public partial ModelPickerRow? SelectedRow { get; set; }

  /// <summary>The rows matching the search text, case-insensitive on the model id and on the
  ///     vendor's name. The list identity is STABLE between rebuilds: <see cref="SelectedRow"/>
  ///     deliberately does not raise it - swapping ItemsSource on every selection change would
  ///     reset the ListBox mid-click, lose the row the user just picked, and write a stale
  ///     selection back into this property (the click-twice desync, headless-tested in
  ///     ModelListSectionTests).</summary>
  public IReadOnlyList<ModelPickerRow> FilteredRows => FilteredRowsList;

  /// <summary>The variant rows in display order (the dropdown's ItemsSource).</summary>
  public IReadOnlyList<VariantOption> VariantOptions { get; } =
  [
    .. ModelVariants.All.Select(variant => new VariantOption(ModelVariants.Describe(variant), variant)),
  ];

  /// <summary>The selected routing variant (two-way bound). It is the SECOND half of the choice
  ///     and travels with the model on confirm.
  ///     <para>IT APPLIES TO OPENROUTER ENTRIES ONLY, which is what the caption beneath the
  ///     dropdown states.</para></summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CheapestSentence))]
  public partial VariantOption SelectedVariantOption { get; set; }

  /// <summary>What this session allows the provider to keep of its prompts.
  ///     <para>THE SECOND AXIS OF THE SAME DECISION, and independent of the variant: the
  ///     dropdown states which tradeoff to prefer, this states which endpoints are admissible
  ///     at all. It opens on the strict tier and only an explicit loosening moves it.</para>
  ///     <para>THE FIRST TWO TIERS ARE HARD: when the catalog shows no eligible endpoint for
  ///     the chosen model, the turn fails and names the tier rather than falling back to a
  ///     provider that keeps the prompts.</para></summary>
  [ObservableProperty]
  public partial RetentionPolicy Retention { get; set; }

  /// <summary>The row the dropdown highlights, derived from <see cref="Retention"/>. The rows
  ///     are rebuilt on every catalog refresh, so the selection is carried as a VALUE and never
  ///     as a row identity.</summary>
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
  ///     themselves are bare names: it states what each variant actually trades and costs.
  ///     <para>ONE STATIC STRING FOR EVERY OPTION: it does not change with the selection.</para>
  ///     <para>IT STATES THAT THE VARIANT COVERS OPENROUTER ENTRIES ONLY, because a Cloudflare
  ///     entry has no routing variant to apply.</para></summary>
  public static string VariantCaption => "OpenRouter entries only. Nitro admits priority-tier "
      + "endpoints (billed at priority rates) and sorts by throughput; Floor admits flex-tier "
      + "endpoints (billed at their discounted rate) and sorts by price. Cache Floor admits "
      + "flex-tier endpoints too, but orders them by advertised cache-read price, so a session "
      + "whose prompts are cached heavily prefers the cheapest cache reads. Cheapest orders them "
      + "by total cost at this workspace's measured usage — input, output and cache-read rates "
      + "together — and says so when the choice depends on a high cache-hit rate. Billing always "
      + "follows the tier that actually served, and the usage ledger records the prices it served "
      + "at.";

  /// <param name="loadCatalog">Loads one vendor's catalog (UI thread escape handled here - the
  ///     delegate itself may block on HTTP).</param>
  /// <param name="currentEntries">The session's live ordered list, seeded into the numbered
  ///     list in order. Empty seeds an empty list.</param>
  /// <param name="currentVariant">The session's live routing variant to pre-select (null
  ///     pre-selects Standard).</param>
  /// <param name="currentRetention">The session's live retention tier to pre-select.</param>
  /// <param name="loadCheapestEvidence">Reads the usage evidence the Cheapest sentence is
  ///     built from, or null when no ledger is reachable.</param>
  /// <param name="vendors">The vendors whose catalogs are offered, in display order.</param>
  /// <param name="sessionVendor">The provider id the session recorded, used as the vendor of
  ///     an entry that names none.</param>
  public ModelPickerViewModel(
      Func<string, CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> loadCatalog,
      IReadOnlyList<ModelSelectionEntry> currentEntries,
      ModelVariant? currentVariant = null,
      RetentionPolicy currentRetention = RetentionPolicy.ZeroDataRetention,
      Func<CancellationToken, Task<CheapestEvidence>>? loadCheapestEvidence = null,
      IReadOnlyList<string>? vendors = null,
      string? sessionVendor = null)
  {
    _loadCatalog = loadCatalog ?? throw new ArgumentNullException(nameof(loadCatalog));
    _loadCheapestEvidence = loadCheapestEvidence;
    // AN UNSTATED VENDOR LIST MEANS THE SESSION'S OWN VENDOR, which is the single catalog this
    // section offered before a second vendor existed. Offering every vendor by default would load
    // one catalog under two vendor ids and list each model twice.
    Vendors = [.. (vendors ?? [sessionVendor ?? ModelVendors.OpenRouter])
        .Where(vendor => !string.IsNullOrWhiteSpace(vendor))];
    _sessionVendor = string.IsNullOrWhiteSpace(sessionVendor) ? null : sessionVendor;
    _entries =
    [
      .. (currentEntries ?? [])
        .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.ModelId))
        .Select(entry => new ModelSelectionEntry(entry.ModelId.Trim(), entry.Vendor?.Trim())),
    ];

    // The commands exist before the observable properties: setting those raises the changed
    // hooks, which requery command availability. The guard in the action is load-bearing:
    // ICommand.Execute does not consult CanExecute, and a disabled button is only one of several
    // ways this command can be invoked.
    AddModelCommand = new RelayCommand(AddModel, () => SelectedRow is not null);
    MoveUpCommand = new RelayCommand(MoveUp, () => SelectedModel is { Position: > 1 });
    MoveDownCommand = new RelayCommand(MoveDown, () => SelectedModel is { } row && row.Position < _entries.Count);
    RemoveModelCommand = new RelayCommand(RemoveModel, () => SelectedModel is not null);
    SelectedVariantOption = VariantOptions.FirstOrDefault(o => o.Variant == currentVariant)
        ?? VariantOptions[0];
    Retention = currentRetention;
    RenderSelectedModels();
    SearchText = string.Empty; // raises OnSearchTextChanged → initial list
  }

  /// <summary>Fetches every vendor's catalog off the UI thread and fills the list. A vendor
  ///     whose read fails is stated in <see cref="LoadError"/> and contributes no rows; the
  ///     vendors that answered keep theirs. Only the first call loads.</summary>
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
      List<string> failures = [];
      foreach (string vendor in Vendors)
      {
        // HTTP plus (for OpenRouter) a per-model endpoint crawl - never on the UI thread.
        // Context flow is suppressed alongside the thread switch, mirroring MainViewModel's
        // session-open schedule.
        Task<Result<IReadOnlyList<ModelProviderEntry>>> scheduled;
        using (ExecutionContext.SuppressFlow())
        {
          scheduled = Task.Run(() => _loadCatalog(vendor, CancellationToken.None));
        }

        Result<IReadOnlyList<ModelProviderEntry>> catalog = await scheduled;
        if (catalog.IsSuccess)
        {
          _entriesByVendor[vendor] = catalog.Value;
        }
        else
        {
          failures.Add($"{ModelVendors.DisplayName(vendor)}: {catalog.Error.Message}");
        }
      }

      CatalogErrors = failures;
      LoadError = failures.Count == 0 ? null : string.Join(" ", failures);
      RebuildCatalog();
      // Pre-select the head of the session's live list, when a catalog lists it.
      SelectedRow = HeadEntry is { } head ? RowOf(head) : null;
      RenderSelectedModels();
      await LoadCheapestEvidenceAsync();
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

  /// <summary>Reads the usage evidence the Cheapest sentence prices at, and announces it.
  ///     <para>A LEDGER THAT CANNOT BE READ LEAVES THE EVIDENCE ABSENT, and the sentence then
  ///     states the documented default rung.</para>
  ///     <para>THE EVIDENCE IS READ AFTER THE CATALOG, because the endpoint rung answers only
  ///     against bound catalog rows.</para></summary>
  private async Task LoadCheapestEvidenceAsync()
  {
    if (_loadCheapestEvidence is null)
    {
      return;
    }

    // Named decision (CA1031): a ledger fault must land as absent evidence, never escape - the
    // fire-and-forget caller cannot observe it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Task<CheapestEvidence> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _loadCheapestEvidence(CancellationToken.None));
      }

      _cheapestEvidence = await scheduled;
    }
    catch (Exception)
    {
      _cheapestEvidence = null;
    }
#pragma warning restore CA1031
    OnPropertyChanged(nameof(CheapestSentence));
  }

  /// <summary>Applies one vendor's catalog refresh (the settings dialog's revalidation join):
  ///     that vendor's rows are replaced and the selection preserved, so a refresh never invents
  ///     a user edit.</summary>
  /// <param name="vendor">The vendor whose catalog was revalidated.</param>
  /// <param name="result">The refreshed catalog, or the failure that ended the read.</param>
  internal void ApplyRefresh(string vendor, Result<IReadOnlyList<ModelProviderEntry>> result)
  {
    if (!result.IsSuccess)
    {
      LoadError = $"{ModelVendors.DisplayName(vendor)}: catalog refresh failed; showing cached "
          + $"models. {result.Error.Message}";
      return;
    }

    ModelPickerRow? selected = SelectedRow;
    _entriesByVendor[vendor] = result.Value;
    RebuildCatalog();
    SelectedRow = selected is null
        ? null
        : FilteredRows.FirstOrDefault(row => row.ModelId == selected.ModelId
            && string.Equals(row.Vendor, selected.Vendor, StringComparison.Ordinal)) ?? selected;
    // Every selected row's details and warnings are re-read from the updated catalog; the ORDER
    // and the selected row survive because the rows are replaced in place.
    SelectedModelRow? keep = SelectedModel;
    RenderSelectedModels();
    SelectedModel = keep is null ? null : SelectedModels.FirstOrDefault(r => r.Position == keep.Position);
    LoadError = null;
  }

  /// <summary>Appends the catalog row under the search box as the LAST entry of the ordered
  ///     list.
  ///     <para>A ROW THE VENDOR CANNOT SERVE IS REFUSED WITH ITS REASON: a model whose catalog
  ///     entry says it cannot call tools cannot run a turn, so the refusal names that instead of
  ///     adding a model that would fail on its first attempt.</para>
  ///     <para>A REPEATED MODEL IS REFUSED AND NAMED rather than appended twice: the second
  ///     occurrence under the SAME vendor can never serve, because the first already covered it.
  ///     The same model under a different vendor is a different entry.</para>
  ///     <para>THE LIST HAS NO UPPER BOUND: a turn walks it one model at a time, so any number
  ///     of entries can be added and reordered.</para>
  ///     <para>NOTHING SELECTED IN THE CATALOG IS NOT AN ERROR - it is the state the section
  ///     opens in, and reporting it as a failure would put a message under a list the user has
  ///     not tried to change.</para></summary>
  private void AddModel()
  {
    if (SelectedRow is not { ModelId: { } modelId } row)
    {
      return;
    }

    if (row.Refusal is { } refusal)
    {
      SelectionError = refusal;
      return;
    }

    if (_entries.Any(entry => string.Equals(entry.ModelId, modelId, StringComparison.Ordinal)
        && string.Equals(VendorOf(entry), row.Vendor, StringComparison.Ordinal)))
    {
      SelectionError = $"{row.DisplayName} is already in the list; a model that appears twice can "
          + "never be the backup that runs.";
      return;
    }

    SelectionError = null;
    _entries.Add(new ModelSelectionEntry(modelId, row.Vendor));
    SelectedModels.Add(BuildSelectedRow(_entries.Count - 1));
    SelectedModel = SelectedModels[^1];
  }

  /// <summary>Swaps the selected row with the one above it. The first row has nowhere to go, so
  ///     the command is unavailable there and the action returns without raising an error.
  ///     </summary>
  private void MoveUp()
  {
    if (SelectedModel is not { } row || row.Position <= 1)
    {
      return;
    }

    Swap(row.Position - 1, row.Position - 2);
  }

  /// <summary>Swaps the selected row with the one below it. The last row has nowhere to go.
  ///     </summary>
  private void MoveDown()
  {
    if (SelectedModel is not { } row || row.Position >= _entries.Count)
    {
      return;
    }

    Swap(row.Position - 1, row.Position);
  }

  /// <summary>Drops the selected row from the ordered list.
  ///     <para>THE LAST ROW IS REFUSED WITH ITS REASON: a session with no model has nothing to
  ///     serve, so there is no empty state to return to and the refusal names the rule instead
  ///     of leaving the button looking dead.</para></summary>
  private void RemoveModel()
  {
    if (SelectedModel is not { } row)
    {
      return;
    }

    if (_entries.Count <= 1)
    {
      SelectionError = "At least one model is required; add another model before removing this one.";
      return;
    }

    SelectionError = null;
    int index = row.Position - 1;
    _entries.RemoveAt(index);
    SelectedModels.RemoveAt(index);
    for (int i = index; i < _entries.Count; i++)
    {
      SelectedModels[i] = BuildSelectedRow(i);
    }

    SelectedModel = SelectedModels.Count == 0 ? null : SelectedModels[Math.Min(index, SelectedModels.Count - 1)];
  }

  /// <summary>Moves entry <paramref name="from"/> to <paramref name="to"/> and re-renders every
  ///     row whose number changed, so the list never shows a gap or a repeated number.</summary>
  private void Swap(int from, int to)
  {
    SelectionError = null;
    (_entries[from], _entries[to]) = (_entries[to], _entries[from]);
    SelectedModels[to] = BuildSelectedRow(to);
    SelectedModels[from] = BuildSelectedRow(from);
    SelectedModel = SelectedModels[to];
  }

  /// <summary>Replaces every row's VALUE in place from the current catalog, keeping the
  ///     collection identity and the row count. Prices, windows, and warnings are re-read here
  ///     and nowhere else.</summary>
  private void RenderSelectedModels()
  {
    for (int i = 0; i < _entries.Count; i++)
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

  /// <summary>One numbered row built from the current catalog: the vendor's cheapest endpoint
  ///     for the model, and the smaller-window notice when this model's window is narrower than
  ///     the first entry's.
  ///     <para>AN UNKNOWN WINDOW IS NOT A SMALL ONE. Only two POSITIVE windows can be compared,
  ///     so a model no catalog sizes carries no notice and neither does the head.</para>
  ///     </summary>
  private SelectedModelRow BuildSelectedRow(int index)
  {
    ModelSelectionEntry entry = _entries[index];
    string? vendor = VendorOf(entry);
    IReadOnlyList<ModelProviderEntry> endpoints = EndpointsOf(entry);
    string detail = FormatDetail(endpoints);
    return new SelectedModelRow(
        index + 1,
        entry.ModelId,
        vendor,
        Caption(vendor, entry.ModelId),
        index == 0 ? $"{detail} · runs first" : detail,
        WindowWarning(index));
  }

  /// <summary>The smaller-window notice for one row, or null. The comparison is against the
  ///     FIRST entry, because entry 1's window is what governs the session's budget.</summary>
  private string? WindowWarning(int index)
  {
    if (index == 0 || _entries.Count == 0)
    {
      return null;
    }

    int head = CheapestWindowOf(_entries[0]);
    int thisWindow = CheapestWindowOf(_entries[index]);
    return head > 0 && thisWindow > 0 && thisWindow < head
        ? $"Context window {FormatContext(thisWindow)} is smaller than {_entries[0].ModelId}'s "
            + $"{FormatContext(head)}; this backup cannot carry a conversation that fills entry 1's "
            + "window."
        : null;
  }

  /// <summary>The context window the model's cheapest endpoint reports, or 0 when no catalog
  ///     lists an endpoint for it or reports none.</summary>
  private int CheapestWindowOf(ModelSelectionEntry entry)
  {
    IReadOnlyList<ModelProviderEntry> endpoints = EndpointsOf(entry);
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

  /// <summary>The model's endpoints an order can name and price: a routable slug and a known
  ///     input and output rate. A row whose cost is unknown is left out rather than ranked
  ///     last, because unknown was never shown to be cheaper.
  ///     <para>EMPTY MEANS THE ORDER COULD NOT BE RESOLVED, and the sentence says the provider's
  ///     own routing applies.</para></summary>
  private IReadOnlyList<ModelProviderEntry> PricedEndpointsOf(ModelSelectionEntry entry)
      => [.. EndpointsOf(entry).Where(model => model.Variant.Slug is { Length: > 0 }
          && CheapestCost.Unit(model, CostShapeAssumption.Fallback.Shape) is not null)];

  /// <summary>Every catalog row the entry's vendor lists for its model, keyed on the BASE id so
  ///     a stored id carrying a routing-variant suffix finds the same endpoints its base model
  ///     does. Empty when no catalog lists one - a saved model the catalog does not currently
  ///     offer is shown, not dropped.</summary>
  private IReadOnlyList<ModelProviderEntry> EndpointsOf(ModelSelectionEntry entry)
      => VendorOf(entry) is { } vendor
          && _endpointsByModel.TryGetValue((vendor, ModelVariants.BaseOf(entry.ModelId)),
              out IReadOnlyList<ModelProviderEntry>? endpoints)
              ? endpoints
              : [];

  /// <summary>The vendor that serves an entry: the one it names, else the session's recorded
  ///     binding, which is what an entry written before vendors existed means.
  ///     <para>A SECTION OFFERING ONE VENDOR SERVES EVERY UNNAMED ENTRY FROM IT: with a single
  ///     catalog there is no other vendor the entry could mean, and that is what every list
  ///     written before vendors existed meant. With two vendors offered the session's binding is
  ///     the only honest answer, and null when it recorded none.</para></summary>
  /// <param name="entry">The entry whose vendor is wanted.</param>
  private string? VendorOf(ModelSelectionEntry entry)
      => entry.Vendor
          ?? _sessionVendor
          ?? (Vendors.Count == 1 ? Vendors[0] : null);

  /// <summary>The catalog rows indexed by (vendor, base model id), built once per catalog
  ///     rebuild so a row's summary costs a lookup instead of a scan.</summary>
  private static Dictionary<(string Vendor, string ModelId), IReadOnlyList<ModelProviderEntry>>
      BuildEndpointIndex(Dictionary<string, IReadOnlyList<ModelProviderEntry>> byVendor)
  {
    Dictionary<(string, string), IReadOnlyList<ModelProviderEntry>> index = [];
    foreach ((string vendor, IReadOnlyList<ModelProviderEntry> entries) in byVendor)
    {
      foreach (IGrouping<string, ModelProviderEntry> group in entries
          .GroupBy(entry => ModelVariants.BaseOf(entry.ModelId), StringComparer.Ordinal))
      {
        index[(vendor, group.Key)] = [.. group];
      }
    }

    return index;
  }

  /// <summary>Rebuilds the endpoint index and the search rows from every vendor's loaded
  ///     catalog.</summary>
  private void RebuildCatalog()
  {
    _endpointsByModel = BuildEndpointIndex(_entriesByVendor);
    ModelRows = BuildRows(_entriesByVendor);
    RebuildFilteredRows();
  }

  private void RebuildFilteredRows()
  {
    string needle = SearchText.Trim();
    List<ModelPickerRow> rows = [];
    rows.AddRange(needle.Length == 0
        ? ModelRows
        : ModelRows.Where(r => r.ModelId!.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    // The selection is captured BEFORE the list identity changes: the bound list control drops
    // its selection when ItemsSource is replaced and writes null straight back into SelectedRow
    // (synchronously, inside the notification below).
    ModelPickerRow? keep = SelectedRow;
    FilteredRowsList = rows;
    OnPropertyChanged(nameof(FilteredRows));
    // ...so it is re-resolved AFTER, by model id and vendor (the row objects are new: their
    // details were re-rendered from the prices just resolved).
    SelectedRow = keep is null ? null : RowOf(keep.ModelId, keep.Vendor);
  }

  /// <summary>The row for one entry, or null when no catalog lists it.</summary>
  /// <param name="entry">The entry whose row is wanted.</param>
  private ModelPickerRow? RowOf(ModelSelectionEntry entry)
      => RowOf(entry.ModelId, VendorOf(entry));

  /// <summary>The row for one (model, vendor) pair, or null when no catalog lists it.</summary>
  /// <param name="modelId">The model id.</param>
  /// <param name="vendor">The vendor id, or null for none.</param>
  private ModelPickerRow? RowOf(string? modelId, string? vendor)
      => FilteredRows.FirstOrDefault(row => row.ModelId == modelId
          && string.Equals(row.Vendor, vendor, StringComparison.Ordinal));

  /// <summary>The search rows: one per (vendor, model), so the same model id served by two
  ///     vendors is two rows and one vendor's many endpoints for a model are one row.</summary>
  private static IReadOnlyList<ModelPickerRow> BuildRows(
      Dictionary<string, IReadOnlyList<ModelProviderEntry>> byVendor)
  {
    List<ModelPickerRow> rows = [];
    foreach ((string vendor, IReadOnlyList<ModelProviderEntry> entries) in byVendor)
    {
      foreach (IGrouping<string, ModelProviderEntry> group in entries
          .GroupBy(entry => ModelVariants.BaseOf(entry.ModelId), StringComparer.Ordinal))
      {
        rows.Add(BuildRow(vendor, group.Key, [.. group]));
      }
    }

    return
    [
      .. rows.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
          .ThenBy(row => row.Vendor, StringComparer.Ordinal),
    ];
  }

  /// <summary>One search row: the vendor's caption, the vendor's own price summary, its
  ///     paid-plan marker, and the reason it cannot be added when it cannot.
  ///     <para>THE PAID-PLAN MARKER COMES FROM THE CATALOG AND IS TRI-STATE: true is shown,
  ///     false and null both carry no marker, because a vendor that said nothing has not said
  ///     the model is free.</para></summary>
  private static ModelPickerRow BuildRow(string vendor, string modelId,
      IReadOnlyList<ModelProviderEntry> endpoints)
      => new(modelId, vendor, Caption(vendor, modelId), FormatDetail(endpoints), endpoints,
          endpoints.Select(entry => entry.RequiresPaidPlan).FirstOrDefault(marker => marker is not null),
          RefusalFor(vendor, modelId, endpoints));

  /// <summary>The reason a row cannot be added, or null when it can.
  ///     <para>A MODEL THAT CANNOT CALL TOOLS IS REFUSED: the harness drives every turn through
  ///     tools, so such a model cannot serve one. The refusal names tool support as the
  ///     reason.</para></summary>
  private static string? RefusalFor(string vendor, string modelId,
      IReadOnlyList<ModelProviderEntry> endpoints)
      => endpoints.Any(entry => entry.SupportsToolUse)
          ? null
          : $"{ModelVendors.DisplayName(vendor)} lists {modelId} without tool-call support, and a "
              + "session model must be able to call tools.";

  /// <summary>A row's caption: the vendor's name and the model id, so no row can be read
  ///     without knowing which vendor would serve it.</summary>
  private static string Caption(string? vendor, string modelId)
      => vendor is null ? modelId : $"{ModelVendors.DisplayName(vendor)} · {modelId}";

  /// <summary>The model row's one-line summary: the cheapest endpoint's price, the context
  ///     window, and how many endpoints the vendor lists for the model.
  ///     <para>THREE PRICING STATES STAY DISTINCT (issue #289): a price the catalog advertises,
  ///     a price set at routing time (negative), and a price nobody advertised (null).
  ///     Rendering an unknown price as $0 would tell the user a paid model is free, and
  ///     rendering a negative one as dollars would invent a price the router decides.</para>
  ///     </summary>
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
