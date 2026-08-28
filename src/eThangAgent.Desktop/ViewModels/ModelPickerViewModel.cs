using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The model confirmed in the model picker. Null <see cref="ModelId"/> means
///     "auto choice" — the session returns to its normal resolution (intelligent
///     selection on OpenRouter, the provider default on z.ai). (A cancelled dialog
///     closes with no result at all, so unchanged-vs-auto never collides.)</summary>
internal sealed record ModelChoice(string? ModelId);

/// <summary>One choosable row of the model picker: a concrete model (deduped across the
///     catalog's provider endpoints, represented by the cheapest pricing) or the pinned
///     auto pseudo-row (<see cref="ModelId"/> null, offered only where the provider has
///     an automatic resolution).</summary>
internal sealed record ModelPickerRow(string? ModelId, string DisplayName, string Detail);

/// <summary>View-model behind the model picker: a searchable, deduped list of the open
///     session's provider catalog plus the optional auto row. Pure state and commands;
///     window closing and applying the choice belong to the caller. The catalog load
///     runs off the UI thread and populates the rows when it lands — the first open per
///     session can take seconds (OpenRouter crawls per-model endpoints), so the list
///     stays in loading state until then.</summary>
internal sealed partial class ModelPickerViewModel : ObservableObject
{
  private static readonly ModelPickerRow AutoRow =
      new(null, "Auto (smart selection)", "Picks the best model for each prompt automatically");

  private readonly Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> _loadCatalog;
  private readonly bool _allowAuto;
  private readonly string? _currentModelId;
  private IReadOnlyList<ModelPickerRow> ModelRows { get; set; } = [];
  private bool _loaded;

  /// <summary>Raised when the user confirms a row; carries the choice. The view closes
  ///     the dialog with it.</summary>
  public event EventHandler<ModelChoice>? ConfirmRequested;

  public IRelayCommand ConfirmCommand { get; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FilteredRows))]
  public partial string SearchText { get; set; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string? LoadError { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(FilteredRows))]
  [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
  public partial ModelPickerRow? SelectedRow { get; set; }

  /// <summary>The auto row (pinned first, never filtered out) followed by the models
  ///     matching the search text, case-insensitive on the model id.</summary>
  public IReadOnlyList<ModelPickerRow> FilteredRows
  {
    get
    {
      string needle = SearchText.Trim();
      List<ModelPickerRow> rows = [];
      if (_allowAuto)
      {
        rows.Add(AutoRow);
      }

      rows.AddRange(needle.Length == 0
          ? ModelRows
          : ModelRows.Where(r => r.ModelId!.Contains(needle, StringComparison.OrdinalIgnoreCase)));
      return rows;
    }
  }

  /// <param name="loadCatalog">Loads the open session's provider catalog (UI thread
  ///     escape handled here — the delegate itself may block on HTTP).</param>
  /// <param name="allowAuto">Whether the auto pseudo-row is offered (OpenRouter only —
  ///     z.ai has no automatic resolution, so its list is just the static lineup).</param>
  /// <param name="currentModelId">The session's live model choice to pre-select (null
  ///     pre-selects the auto row when offered, nothing otherwise).</param>
  public ModelPickerViewModel(
      Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> loadCatalog,
      bool allowAuto,
      string? currentModelId)
  {
    _loadCatalog = loadCatalog ?? throw new ArgumentNullException(nameof(loadCatalog));
    _allowAuto = allowAuto;
    _currentModelId = currentModelId;

    // The command exists before the observable properties: setting those raises the
    // changed hooks, which requery command availability. The guard in the action is
    // load-bearing: ICommand.Execute does not consult CanExecute, and a disabled
    // button is only one of several ways this command can be invoked.
    ConfirmCommand = new RelayCommand(Confirm, () => SelectedRow is not null);
    SearchText = string.Empty;
  }

  /// <summary>Fetches the catalog off the UI thread and fills the rows. A failure lands
  ///     in <see cref="LoadError"/> (the dialog stays open; the user can cancel). Only
  ///     the first call loads.</summary>
  public async Task LoadAsync()
  {
    if (IsLoading || _loaded)
    {
      return;
    }

    _loaded = true;
    IsLoading = true;
    // Named decision (CA1031): a loader fault must land in the dialog's error state,
    // never escape — the fire-and-forget caller cannot observe it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      // HTTP plus (for OpenRouter) a per-model endpoint crawl — never on the UI
      // thread. Context flow is suppressed alongside the thread switch, mirroring
      // MainViewModel's session-open schedule.
      Task<Result<IReadOnlyList<ModelProviderEntry>>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _loadCatalog(CancellationToken.None));
      }

      Result<IReadOnlyList<ModelProviderEntry>> catalog = await scheduled;
      if (!catalog.IsSuccess)
      {
        LoadError = catalog.Error!.Message;
        return;
      }

      ModelRows = BuildRows(catalog.Value!);
      OnPropertyChanged(nameof(FilteredRows));
      // Pre-select the session's live choice; otherwise the auto row when offered.
      SelectedRow = FilteredRows.FirstOrDefault(r => r.ModelId == _currentModelId)
          ?? (_allowAuto ? AutoRow : null);
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

  private void Confirm()
  {
    if (SelectedRow is not null)
    {
      ConfirmRequested?.Invoke(this, new ModelChoice(SelectedRow.ModelId));
    }
  }

  private static IReadOnlyList<ModelPickerRow> BuildRows(IReadOnlyList<ModelProviderEntry> entries)
  {
    return [.. entries
        .GroupBy(e => e.ModelId, StringComparer.Ordinal)
        .Select(g => g.OrderBy(e => e.PromptPricePerToken)
            .ThenBy(e => e.CompletionPricePerToken)
            .First())
        .OrderBy(e => e.ModelId, StringComparer.OrdinalIgnoreCase)
        .Select(e => new ModelPickerRow(
            e.ModelId,
            e.ModelId,
            FormatDetail(e)))];
  }

  private static string FormatDetail(ModelProviderEntry entry)
      => $"{FormatPrice(entry.PromptPricePerToken * 1_000_000m)} in / " +
         $"{FormatPrice(entry.CompletionPricePerToken * 1_000_000m)} out per M tokens · " +
         $"{FormatContext(entry.ContextLength)} ctx";

  private static string FormatPrice(decimal perMillion)
      => perMillion.ToString("$0.##", CultureInfo.InvariantCulture);

  private static string FormatContext(int tokens)
      => tokens >= 1_000_000
          ? (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
          : (tokens / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K";
}
