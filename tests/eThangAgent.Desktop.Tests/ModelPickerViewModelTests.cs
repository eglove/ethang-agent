using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Behavior of the model section: the ordered selection (entry 1 runs, the rest are ordered
///     backups), the catalog search that feeds additions, and the two dropdowns that cover the whole
///     list.
///     <para>WHAT THIS FILE NO LONGER COVERS, DELIBERATELY: the provider level, the ordering presets,
///     the per-pair cache-hit-rate resolution, and the evidence rows. Those tested a surface the user
///     asked to remove - comparing endpoints was a decision the harness could rank from measurements,
///     and the user's real choice is the tradeoff (speed or price), which OpenRouter publishes as
///     routing variants. The stats behind that surface are untouched and still tested where they live
///     (the usage ledger and the catalog ACLs).</para></summary>
public class ModelPickerViewModelTests
{
  private const string Model = "alpha/model";
  private const string Beta = "beta/model";

  private static ModelProviderEntry Entry(
      string modelId = Model,
      string providerName = "ProviderA",
      decimal? promptPrice = 0.000003m,
      decimal? completionPrice = 0.000015m,
      int context = 200_000)
      => new(modelId, providerName, promptPrice, null, completionPrice, context,
          8192, SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null);

  private static ModelPickerViewModel CreateVm(
      IReadOnlyList<ModelProviderEntry> entries,
      IReadOnlyList<string>? currentModelIds = null,
      ModelVariant? currentVariant = null)
      => new(_ => Task.FromResult(Result.Success(entries)), currentModelIds ?? [], currentVariant);

  private static async Task<ModelPickerViewModel> LoadAsync(
      IReadOnlyList<ModelProviderEntry> entries,
      IReadOnlyList<string>? currentModelIds = null,
      ModelVariant? currentVariant = null)
  {
    ModelPickerViewModel vm = CreateVm(entries, currentModelIds, currentVariant);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  /// <summary>Selects a catalog row the way the bound list control does: by row identity, after the
  ///     search has been narrowed to it.</summary>
  private static void SelectCatalogRow(ModelPickerViewModel vm, string modelId)
  {
    vm.SearchText = modelId;
    vm.SelectedRow = vm.FilteredRows.First(row => row.ModelId == modelId);
  }

  /// <summary>Selects a numbered row by its model id.</summary>
  private static void SelectRow(ModelPickerViewModel vm, string modelId)
      => vm.SelectedModel = vm.SelectedModels.First(row => row.ModelId == modelId);

  // ── behaviour 1: the selection opens as a numbered list in order ───────────

  [Fact]
  public async Task Opening_WithTwoEntries_ShowsTwoNumberedRowsInThatOrder()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta)], [Model, Beta]).ConfigureAwait(true);

    Assert.Equal([1, 2], vm.SelectedModels.Select(row => row.Position));
    Assert.Equal([Model, Beta], vm.SelectedModels.Select(row => row.ModelId));
  }

  [Fact]
  public async Task TheFirstRow_IsPresentedAsTheModelThatRuns()
  {
    // Entry 1 governs the session's context budget, so the row says so rather than leaving the
    // user to infer it from the number.
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)], [Model, Beta])
        .ConfigureAwait(true);

    Assert.Contains("runs first", vm.SelectedModels[0].Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("runs first", vm.SelectedModels[1].Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ASelectedRow_ShowsTheCatalogSummaryOfItsCheapestEndpoint()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(promptPrice: 0.00002m), Entry(providerName: "ProviderB", promptPrice: 0.000003m)],
        [Model]).ConfigureAwait(true);

    SelectedModelRow row = Assert.Single(vm.SelectedModels);
    Assert.Contains("0.000003", row.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("0.00002", row.Detail, StringComparison.Ordinal);
    Assert.Equal(Model, row.DisplayName);
  }

  // ── behaviour 2: add appends last; a duplicate is refused and named ────────

  [Fact]
  public async Task Adding_ACatalogModel_AppendsItLast()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)], [Model]).ConfigureAwait(true);

    SelectCatalogRow(vm, Beta);
    vm.AddModelCommand.Execute(null);

    Assert.Equal([Model, Beta], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2], vm.SelectedModels.Select(row => row.Position));
    Assert.Null(vm.SelectionError);
  }

  [Fact]
  public async Task Adding_AModelAlreadyInTheList_IsRefused_AndTheDuplicateIsNamed()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)], [Beta]).ConfigureAwait(true);

    SelectCatalogRow(vm, Beta);
    vm.AddModelCommand.Execute(null);

    _ = Assert.Single(vm.SelectedModels); // unchanged
    Assert.Contains(Beta, vm.SelectionError!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AddingAFourthModel_IsAccepted_WithNoCapMessage()
  {
    // A turn walks the list one model at a time, so a fourth entry is a fourth attempt rather
    // than a request the provider would refuse. Nothing about the list is capped.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta), Entry(modelId: "gamma/model"), Entry(modelId: "delta/model")],
        [Model, Beta, "gamma/model"]).ConfigureAwait(true);

    SelectCatalogRow(vm, "delta/model");
    vm.AddModelCommand.Execute(null);

    Assert.Equal([Model, Beta, "gamma/model", "delta/model"],
        vm.SelectedModels.Select(row => row.ModelId));
    Assert.Null(vm.SelectionError);
  }

  [Fact]
  public async Task AddingANinthModel_IsAccepted_AndTheListKeepsEveryEntryInOrder()
  {
    string[] catalog =
    [
      Model, Beta, "gamma/model", "delta/model", "epsilon/model",
      "zeta/model", "eta/model", "theta/model", "iota/model",
    ];
    ModelPickerViewModel vm = await LoadAsync(
        [.. catalog.Select(id => Entry(modelId: id))], [.. catalog.Take(8)]).ConfigureAwait(true);

    SelectCatalogRow(vm, "iota/model");
    vm.AddModelCommand.Execute(null);

    Assert.Equal(catalog, vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], vm.SelectedModels.Select(row => row.Position));
    Assert.Null(vm.SelectionError);
  }

  [Fact]
  public async Task Opening_WithMoreModelsThanTheOldCapAllowed_ShowsEveryOneInOrder()
  {
    // A list written under an older build restores whole: trimming it would silently drop models
    // the user chose every time the window opened.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta), Entry(modelId: "gamma/model"), Entry(modelId: "delta/model")],
        [Model, Beta, "gamma/model", "delta/model"]).ConfigureAwait(true);

    Assert.Equal([Model, Beta, "gamma/model", "delta/model"],
        vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2, 3, 4], vm.SelectedModels.Select(row => row.Position));
  }

  // ── behaviour 3: adding with nothing selected does nothing, silently ───────

  [Fact]
  public async Task Adding_WithNothingSelectedInTheCatalog_DoesNothing_AndRaisesNoError()
  {
    // A saved model the catalog does not list leaves the search box with nothing selected, which
    // is the state this behaviour is about.
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)], ["gone/model"])
        .ConfigureAwait(true);
    Assert.Null(vm.SelectedRow);

    vm.AddModelCommand.Execute(null);

    _ = Assert.Single(vm.SelectedModels);
    Assert.Null(vm.SelectionError);
    Assert.False(vm.AddModelCommand.CanExecute(null));
  }

  // ── behaviour 4: move swaps neighbours and renumbers; the ends are no-ops ──

  [Fact]
  public async Task MovingTheSecondRowUp_SwapsItWithTheFirst_AndRenumbers()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta)], [Model, Beta]).ConfigureAwait(true);

    SelectRow(vm, Beta);
    vm.MoveUpCommand.Execute(null);

    Assert.Equal([Beta, Model], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2], vm.SelectedModels.Select(row => row.Position));
    Assert.Equal(Beta, vm.SelectedModel?.ModelId); // the moved row stays selected
    Assert.Null(vm.SelectionError);
  }

  [Fact]
  public async Task MovingTheFirstRowUp_DoesNothing_AndRaisesNoError()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta)], [Model, Beta]).ConfigureAwait(true);

    SelectRow(vm, Model);
    Assert.False(vm.MoveUpCommand.CanExecute(null));
    vm.MoveUpCommand.Execute(null);

    Assert.Equal([Model, Beta], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Null(vm.SelectionError);
  }

  [Fact]
  public async Task MovingTheLastRowDown_DoesNothing_AndRaisesNoError()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta)], [Model, Beta]).ConfigureAwait(true);

    SelectRow(vm, Beta);
    Assert.False(vm.MoveDownCommand.CanExecute(null));
    vm.MoveDownCommand.Execute(null);

    Assert.Equal([Model, Beta], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Null(vm.SelectionError);
  }

  [Fact]
  public async Task MovingDown_RenumbersTheList_WithNoGaps()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta), Entry(modelId: "gamma/model")],
        [Model, Beta, "gamma/model"]).ConfigureAwait(true);

    SelectRow(vm, Model);
    vm.MoveDownCommand.Execute(null);

    Assert.Equal([Beta, Model, "gamma/model"], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2, 3], vm.SelectedModels.Select(row => row.Position));
  }

  // ── behaviour 5: remove keeps the remaining order, renumbered from 1 ───────

  [Fact]
  public async Task RemovingAMiddleRow_LeavesTheRestNumberedFromOne_InOrder()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta), Entry(modelId: "gamma/model")],
        [Model, Beta, "gamma/model"]).ConfigureAwait(true);

    SelectRow(vm, Beta);
    vm.RemoveModelCommand.Execute(null);

    Assert.Equal([Model, "gamma/model"], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2], vm.SelectedModels.Select(row => row.Position));
    Assert.DoesNotContain(vm.SelectedModels, row => row.ModelId == Beta);
    Assert.Null(vm.SelectionError);
  }

  // ── behaviour 6: the last row cannot be removed ────────────────────────────

  [Fact]
  public async Task RemovingTheOnlyRow_IsRefused_AndSaysWhy()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], [Model]).ConfigureAwait(true);

    SelectRow(vm, Model);
    vm.RemoveModelCommand.Execute(null);

    _ = Assert.Single(vm.SelectedModels);
    Assert.Equal(Model, vm.SelectedModels[0].ModelId);
    Assert.Contains("At least one model", vm.SelectionError!, StringComparison.Ordinal);
  }

  // ── behaviour 7: the smaller-window notice ────────────────────────────────

  [Fact]
  public async Task ABackupWithASmallerWindow_CarriesAWarningNamingBothWindows()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(context: 200_000), Entry(modelId: Beta, context: 128_000)],
        [Model, Beta]).ConfigureAwait(true);

    Assert.Null(vm.SelectedModels[0].Warning);
    string warning = vm.SelectedModels[1].Warning!;
    Assert.Contains("128K", warning, StringComparison.Ordinal);
    Assert.Contains("200K", warning, StringComparison.Ordinal);
    Assert.Contains(Model, warning, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(200_000)] // equal
  [InlineData(400_000)] // larger
  [InlineData(0)] // unknown: the catalog reported no window at all
  public async Task ABackupWithAnEqualLargerOrUnknownWindow_CarriesNoWarning(int backupWindow)
  {
    // UNKNOWN IS NOT SMALL: a window nobody reported cannot be compared, and a notice claiming a
    // limit the catalog never stated would be a fabricated fact.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(context: 200_000), Entry(modelId: Beta, context: backupWindow)],
        [Model, Beta]).ConfigureAwait(true);

    Assert.Null(vm.SelectedModels[1].Warning);
  }

  [Fact]
  public async Task TheHead_CarriesNoWarning_EvenWhenItIsTheSmallerOne()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(context: 100_000), Entry(modelId: Beta, context: 200_000)],
        [Model, Beta]).ConfigureAwait(true);

    Assert.Null(vm.SelectedModels[0].Warning);
    Assert.Null(vm.SelectedModels[1].Warning);
  }

  // ── behaviour 8: a refresh rebuilds every row without moving the list ─────

  [Fact]
  public async Task ARefresh_RebuildsEveryRow_AndPreservesBothTheOrderAndTheSelection()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(context: 200_000), Entry(modelId: Beta, context: 100_000)],
        [Model, Beta]).ConfigureAwait(true);
    SelectRow(vm, Beta);
    Assert.NotNull(vm.SelectedModels[1].Warning);

    // The refreshed catalog reports a narrower window for entry 1 and a WIDER one for the backup,
    // so the warning's presence must be re-derived rather than carried over.
    vm.ApplyRefresh(Result.Success<IReadOnlyList<ModelProviderEntry>>(
        [Entry(context: 200_000), Entry(modelId: Beta, context: 400_000)]));

    Assert.Equal([Model, Beta], vm.SelectedModels.Select(row => row.ModelId));
    Assert.Equal([1, 2], vm.SelectedModels.Select(row => row.Position));
    Assert.Null(vm.SelectedModels[1].Warning);
    Assert.Equal(Beta, vm.SelectedModel?.ModelId);
  }

  [Fact]
  public async Task ARefresh_DoesNotReplaceTheListsIdentity()
  {
    // Swapping ItemsSource mid-click is the click-twice desync the catalog list already carries the
    // fix for; the numbered list must keep one collection for the window's whole life.
    ModelPickerViewModel vm = await LoadAsync([Entry()], [Model]).ConfigureAwait(true);
    object identity = vm.SelectedModels;

    vm.ApplyRefresh(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry(), Entry(modelId: Beta)]));

    Assert.Same(identity, vm.SelectedModels);
  }

  [Fact]
  public async Task ARefreshFailure_KeepsTheRows_AndStatesWhy()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], [Model]).ConfigureAwait(true);

    vm.ApplyRefresh(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
        new DomainError("CatalogDown", "boom")));

    _ = Assert.Single(vm.SelectedModels);
    Assert.NotNull(vm.LoadError);
  }

  // ── behaviour 9: a saved model the catalog does not list is kept ───────────

  [Fact]
  public async Task ASelectedIdAbsentFromTheCatalog_StaysInTheList_WithTheUnknownPricingText()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], [Model, "gone/model"]).ConfigureAwait(true);

    SelectedModelRow row = vm.SelectedModels[1];
    Assert.Equal("gone/model", row.ModelId);
    Assert.Contains(ProviderEvidenceRow.PricingUnknown, row.Detail, StringComparison.Ordinal);
    Assert.Null(row.Warning); // unknown on both sides is never 'smaller'
  }

  [Fact]
  public async Task ASelectedIdCarryingAVariantSuffix_FindsItsBaseModelsEndpoints()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)],
        [Model, $"{Beta}:nitro"])
        .ConfigureAwait(true);

    SelectedModelRow row = vm.SelectedModels[1];
    Assert.Equal($"{Beta}:nitro", row.ModelId);
    Assert.Contains("endpoint(s)", row.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain(ProviderEvidenceRow.PricingUnknown, row.Detail, StringComparison.Ordinal);
  }

  // ── behaviour 10: the selection is a VALUE, order included ────────────────

  [Fact]
  public void TheSelection_IsAValue_SoOrderIsPartOfIt()
  {
    ModelPickerViewModel forward = CreateVm([Entry(), Entry(modelId: Beta)], [Model, Beta], ModelVariant.Nitro);
    ModelPickerViewModel reversed = CreateVm([Entry(), Entry(modelId: Beta)], [Beta, Model], ModelVariant.Nitro);

    Assert.NotEqual(forward.CurrentSelection(), reversed.CurrentSelection());
    Assert.Equal([Model, Beta], forward.CurrentSelection().ModelIds);
    Assert.Equal(ModelVariant.Nitro, forward.CurrentSelection().Variant);
    Assert.Equal(ModelVariant.Nitro, reversed.CurrentSelection().Variant);
  }

  // ── the catalog list keeps its own selection and its other behavior ───────

  [Fact]
  public async Task Adding_DoesNotClearTheCatalogListsSelection_SoSeveralCanBeAddedInARow()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(modelId: Beta), Entry(modelId: "gamma/model")], [Model]).ConfigureAwait(true);

    SelectCatalogRow(vm, Beta);
    vm.AddModelCommand.Execute(null);
    Assert.Equal(Beta, vm.SelectedRow?.ModelId);
    vm.AddModelCommand.Execute(null); // the same row is still selected, and now refused as a duplicate

    Assert.Contains(Beta, vm.SelectionError!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Load_DedupesEndpointsIntoOneCatalogRowPerModel()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(providerName: "ProviderB"), Entry(modelId: Beta)]).ConfigureAwait(true);

    Assert.Equal([Model, Beta],
        vm.FilteredRows.Select(r => r.ModelId).Order(StringComparer.Ordinal));
  }

  [Fact]
  public async Task Load_PreSelectsTheHeadOfTheSessionsList()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)], [Beta, Model])
        .ConfigureAwait(true);

    Assert.Equal(Beta, vm.SelectedRow?.ModelId);
  }

  [Fact]
  public void VariantOptions_OffersEveryVariantInPickerOrder()
  {
    ModelPickerViewModel vm = CreateVm([Entry()]);

    Assert.Equal(
        [ModelVariant.Standard, ModelVariant.Nitro, ModelVariant.Floor, ModelVariant.CacheFloor,
            ModelVariant.Cheapest],
        vm.VariantOptions.Select(o => o.Variant));
  }

  [Fact]
  public void VariantCaption_NamesCacheFloorAndItsMechanism()
  {
    string caption = ModelPickerViewModel.VariantCaption;

    Assert.Contains("Cache Floor", caption, StringComparison.Ordinal);
    Assert.Contains("cache-read", caption, StringComparison.Ordinal);
  }

  [Fact]
  public void VariantOptions_DefaultToStandard()
      => Assert.Equal(ModelVariant.Standard, CreateVm([Entry()]).SelectedVariantOption.Variant);

  [Fact]
  public void VariantOptions_PreSelectTheSessionsCommittedVariant()
  {
    // Reopening the section must show the tradeoff it committed: a dialog that silently reset a
    // nitro session to Standard on the next save would undo the user's choice without saying so.
    ModelPickerViewModel vm = CreateVm([Entry()], [Model], ModelVariant.Nitro);

    Assert.Equal(ModelVariant.Nitro, vm.SelectedVariantOption.Variant);
  }

  [Fact]
  public async Task Search_FiltersCaseInsensitivelyOnTheModelId()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: "beta/other")]).ConfigureAwait(true);

    vm.SearchText = "BETA";

    Assert.Equal("beta/other", Assert.Single(vm.FilteredRows).ModelId);
  }

  [Fact]
  public async Task Search_PreservesTheCatalogSelectionByIdentity_SoTheListDoesNotDropIt()
  {
    // The bound list control drops its selection when ItemsSource is replaced and writes null
    // straight back into SelectedRow; the view-model restores it by id afterwards.
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: Beta)], [Model]).ConfigureAwait(true);

    vm.SearchText = "alpha";

    Assert.Equal(Model, vm.SelectedRow?.ModelId);
  }

  [Fact]
  public async Task ALoadFailure_LandsInTheErrorState_AndLeavesTheSectionUsable()
  {
    ModelPickerViewModel vm = new(
        _ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogDown", "boom"))), []);

    await vm.LoadAsync().ConfigureAwait(true);

    Assert.NotNull(vm.LoadError);
    Assert.Empty(vm.FilteredRows);
    Assert.False(vm.IsLoading);
  }

  [Fact]
  public async Task Load_OnlyRunsOnce_SoAReopenDoesNotRefetch()
  {
    int calls = 0;
    ModelPickerViewModel vm = new(
        _ =>
        {
          calls++;
          return Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry()]));
        }, []);

    await vm.LoadAsync().ConfigureAwait(true);
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal(1, calls);
  }

  [Fact]
  public async Task ApplyRefresh_RebuildsTheCatalogListAndKeepsItsSelection()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], [Model]).ConfigureAwait(true);

    vm.ApplyRefresh(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry(), Entry(modelId: Beta)]));

    Assert.Equal(2, vm.FilteredRows.Count);
    Assert.Equal(Model, vm.SelectedRow?.ModelId);
  }

  [Fact]
  public async Task AModelWithAVariantSuffixInTheCatalog_IsListedUnderItsBaseId()
  {
    // The catalog is keyed on real model ids; a model whose ID genuinely ends in ':free' is a
    // PRICING variant and must not be split or stripped by the routing-variant codec.
    ModelPickerViewModel vm = await LoadAsync([Entry(modelId: "vendor/model:free")]).ConfigureAwait(true);

    Assert.Equal("vendor/model:free", Assert.Single(vm.FilteredRows).ModelId);
  }

  [Fact]
  public void ABlankSeedEntry_IsNotMadeARow()
      => Assert.Empty(CreateVm([Entry()], ["  "]).SelectedModels);

  // ── the Cheapest variant: what it trades, and what its order can justify ──

  private const string CheapestModel = "deepseek/deepseek-v4.1-flash";

  /// <summary>One catalog row for a NAMED endpoint, priced per token as the catalog advertises.
  ///     </summary>
  private static ModelProviderEntry Endpoint(string slug, decimal input, decimal cacheRead, decimal output)
      => new(CheapestModel, slug.Split('/')[0], input, cacheRead, output, 200_000, 8192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
          ThroughputTokensPerSec: null, Description: null)
      {
        Variant = new EndpointVariant(slug),
      };

  /// <summary>The two endpoints of the model this feature was measured on: Wafer is cheaper at a
  ///     low cache-hit rate and River at a high one, so the choice between them is exactly the
  ///     fragile case the sentence exists to state.</summary>
  private static ModelProviderEntry[] WaferAndRiver() =>
  [
    Endpoint("wafer/fp8", 5.1e-8m, 1.8e-8m, 5.67e-7m),
    Endpoint("river/fp8", 9.6e-8m, 1.92e-9m, 3.84e-7m),
  ];

  /// <summary>A ledger report for the model that clears the evidence floor at the workspace
  ///     median shape: 90.13% cache hits and 0.007284 output per input token.</summary>
  private static ProviderPerformanceReport ModelRungReport() => ModelRungReport(0.9013m, 0.007284m);

  /// <summary>The same report at a STATED shape, so a test can put the model's own measurement
  ///     on either side of a crossing.</summary>
  private static ProviderPerformanceReport ModelRungReport(decimal hitRate, decimal ratio) => new(
      UsageScope.Workspace, hitRate, 1_000_000_000L, (long)(1_000_000_000m * hitRate), 40, 0,
      [
        new ProviderPerformance(CheapestModel, "wafer", null, null, null, null, 0, 0, 0, 0, 40,
            1_000_000_000L, (long)(1_000_000_000m * hitRate))
        {
          OutputTokens = (long)(1_000_000_000m * ratio),
        },
      ]);

  /// <summary>A model picker on Cheapest with the given evidence, loaded against the given
  ///     catalog.</summary>
  private static async Task<ModelPickerViewModel> CheapestVmAsync(
      IReadOnlyList<ModelProviderEntry> entries, CheapestEvidence? evidence)
  {
    ModelPickerViewModel vm = new(_ => Task.FromResult(Result.Success(entries)), [CheapestModel],
        ModelVariant.Cheapest,
        loadCheapestEvidence: _ => Task.FromResult(evidence ?? new CheapestEvidence(null, null)));
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  [Fact]
  public void VariantCaption_NamesCheapestAndWhatItTrades()
  {
    // The caption is ONE static string stating what every option trades, so Cheapest's sentence
    // is appended to it rather than replacing what Cache Floor's says.
    string caption = ModelPickerViewModel.VariantCaption;

    Assert.Contains("Cheapest orders them by total cost at this workspace's measured usage", caption,
        StringComparison.Ordinal);
    Assert.Contains("input, output and cache-read rates together", caption, StringComparison.Ordinal);
    Assert.Contains("depends on a high cache-hit rate", caption, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(ModelVariant.Standard)]
  [InlineData(ModelVariant.Nitro)]
  [InlineData(ModelVariant.Floor)]
  [InlineData(ModelVariant.CacheFloor)]
  public void CheapestSentence_IsAbsentOnTheOtherFourVariants(ModelVariant variant)
  {
    // The other four carry no cost projection to explain, and an empty line under the caption
    // would read as a missing measurement.
    ModelPickerViewModel vm = CreateVm([Entry()], [Model], variant);

    Assert.Null(vm.CheapestSentence);
  }

  [Fact]
  public async Task CheapestSentence_NamesTheWinnerAndTheRunnerUpAtTheBreakEvenRate()
  {
    // E2: at the model's measured shape River wins (0.014003 against Wafer's 0.025387 USD per 1M
    // input tokens) but does not dominate - Wafer is cheaper on the input rate - so the choice
    // turns over at a 71.5% cache-hit rate and the sentence names both endpoints and that rate.
    ModelPickerViewModel vm = await CheapestVmAsync(WaferAndRiver(),
        new CheapestEvidence(ModelRungReport(), new CostShape(0.9013m, 0.007284m))).ConfigureAwait(true);

    Assert.Equal("Cheapest is river/fp8, but only above 71.5% cache-hit rate. Below that, "
        + "wafer/fp8 is cheaper.", vm.CheapestSentence);
  }

  [Fact]
  public async Task CheapestSentence_StatesTheCrossingInTheDirectionItHolds_WhenTheWinnerLeadsLow()
  {
    // THE MIRROR OF E2, and the case the sentence used to state backwards: an endpoint with the
    // cheapest input and cache-read rates leads at a LOW hit rate and is overtaken ABOVE the
    // crossing. Asserting only that a crossing exists would let the sentence claim the winner
    // holds above a rate it actually loses above.
    ModelProviderEntry[] overtaken =
    [
      Endpoint("cheap/in", 4.37e-8m, 2.4e-9m, 7.0e-7m),
      Endpoint("cheap/out", 7.75e-8m, 2.325e-9m, 2.325e-7m),
    ];
    ModelPickerViewModel vm = await CheapestVmAsync(overtaken,
        new CheapestEvidence(ModelRungReport(0.8928m, 0.007462m),
            new CostShape(0.9013m, 0.007284m))).ConfigureAwait(true);

    string sentence = vm.CheapestSentence!;
    Assert.StartsWith("Cheapest is cheap/in, but only below ", sentence, StringComparison.Ordinal);
    Assert.Contains("Above that, cheap/out is cheaper.", sentence, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CheapestSentence_RendersTheRateWithOneDecimalPlace_InvariantCulture()
  {
    // 0.7149 must read 71.5%, never 71,5% (a comma locale) and never 0.715 (the raw rate).
    ModelPickerViewModel vm = await CheapestVmAsync(WaferAndRiver(),
        new CheapestEvidence(ModelRungReport(), new CostShape(0.9013m, 0.007284m))).ConfigureAwait(true);

    string sentence = vm.CheapestSentence!;
    Assert.Contains("71.5%", sentence, StringComparison.Ordinal);
    Assert.DoesNotContain("71,5", sentence, StringComparison.Ordinal);
    Assert.DoesNotContain("0.715", sentence, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CheapestSentence_NamesTheEndpointAndItsMeasurement_WhenTheEndpointRungAnswered()
  {
    // E3: a winner that DOMINATES the runner-up is cheapest at every hit rate, so no estimate is
    // needed - and the sentence still names the rung that answered, because a projection built on
    // an unstated assumption is the failure this feature removes.
    ModelProviderEntry[] dominant =
    [
      Endpoint("river/fp8", 1e-7m, 1e-8m, 2e-7m),
      Endpoint("wafer/fp8", 2e-7m, 3e-8m, 4e-7m),
    ];
    // The row is stamped with the ENDPOINT slug, which is what BindEndpoints keys on: a row
    // stamped with a bare provider name binds to nothing and falls to the model rung.
    ProviderPerformanceReport report = new(
        UsageScope.Workspace, 0.9013m, 1_000_000_000L, 901_300_000L, 40, 0,
        [
          new ProviderPerformance(CheapestModel, "river/fp8", null, null, null, null, 0, 0, 0, 0, 40,
              1_000_000_000L, 901_300_000L)
          {
            OutputTokens = 7_463_000L,
          },
        ]);
    ModelPickerViewModel vm = await CheapestVmAsync(dominant, new CheapestEvidence(report, null))
        .ConfigureAwait(true);

    Assert.Equal("Cheapest is river/fp8. This endpoint measured 90.1% cache hits and 0.007463 "
        + "output per input token, and river/fp8 is cheapest at every hit rate.", vm.CheapestSentence);
  }

  [Fact]
  public async Task CheapestSentence_StatesTheDefaultRung_WhenNothingWasMeasured()
  {
    // A rung that did not answer says so in its own words rather than quoting a measurement it
    // does not have - the rule ProviderPerformanceText already applies to an unmeasured figure.
    ModelProviderEntry[] dominant =
    [
      Endpoint("river/fp8", 1e-7m, 1e-8m, 2e-7m),
      Endpoint("wafer/fp8", 2e-7m, 3e-8m, 4e-7m),
    ];
    ModelPickerViewModel vm = await CheapestVmAsync(dominant, new CheapestEvidence(null, null))
        .ConfigureAwait(true);

    Assert.Equal("Cheapest is river/fp8. Nothing has been measured yet, so this uses the default "
        + "50% cache-hit rate; river/fp8 is cheapest at every hit rate.", vm.CheapestSentence);
  }

  [Fact]
  public async Task CheapestSentence_SaysTheOrderCouldNotBeResolved_WhenTheCatalogReadFailed()
  {
    // E4: no catalog row means no endpoint to name, so the sentence states that the provider's
    // own routing applies instead of inventing a winner.
    ModelPickerViewModel vm = new(
        _ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogDown", "boom"))),
        [CheapestModel], ModelVariant.Cheapest,
        loadCheapestEvidence: _ => Task.FromResult(new CheapestEvidence(null, null)));
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal("Cheapest could not be resolved for this model, so the provider's own routing "
        + "applies.", vm.CheapestSentence);
  }

  [Fact]
  public async Task CheapestSentence_IsAbsentAgain_WhenTheUserMovesOffCheapest()
  {
    ModelPickerViewModel vm = await CheapestVmAsync(WaferAndRiver(),
        new CheapestEvidence(ModelRungReport(), null)).ConfigureAwait(true);
    Assert.NotNull(vm.CheapestSentence);

    vm.SelectedVariantOption = vm.VariantOptions.First(option => option.Variant == ModelVariant.Standard);

    Assert.Null(vm.CheapestSentence);
  }
};
