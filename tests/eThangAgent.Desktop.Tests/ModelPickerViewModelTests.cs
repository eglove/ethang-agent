using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Behavior of the model + provider picker view-model (issue #296): two levels,
///     deduping, search, pre-selection, the confirm payload, the ordering presets, and the
///     cache-hit-rate assumption. Catalog loading and the ledger read are faked at their
///     delegate seams — the real catalog client and the real usage queries are covered in
///     their own ACL tests.</summary>
public class ModelPickerViewModelTests
{
  private const string Model = "alpha/model";

  private static ModelProviderEntry Entry(
      string modelId = Model,
      string providerName = "ProviderA",
      decimal? promptPrice = 0.000003m,
      decimal? completionPrice = 0.000015m,
      int context = 200_000,
      double? uptime = null)
      => new(modelId, providerName, promptPrice, null, completionPrice, context,
          8192, SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
          UptimeLast30m: uptime);

  /// <summary>A provider row that states its cache prices and whether it caches implicitly -
  ///     the shape the effective-price cases need.</summary>
  private static ModelProviderEntry Caching(
      string providerName, decimal promptPrice, decimal cacheReadPrice, bool? caches = true)
      => new(Model, providerName, promptPrice, cacheReadPrice, 0.0000005m, 163_840, 8192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
          SupportsImplicitCaching: caches);

  private static ModelPickerViewModel CreateVm(
      IReadOnlyList<ModelProviderEntry> entries,
      string? currentModelId = null,
      string? currentProviderName = null,
      ProviderPerformanceReport? report = null)
      => new(_ => Task.FromResult(Result.Success(entries)), currentModelId, currentProviderName,
          report is null
              ? null
              : _ => Task.FromResult(Result.Success(report)));

  private static async Task<ModelPickerViewModel> LoadAsync(
      IReadOnlyList<ModelProviderEntry> entries,
      string? currentModelId = null,
      string? currentProviderName = null,
      ProviderPerformanceReport? report = null)
  {
    ModelPickerViewModel vm = CreateVm(entries, currentModelId, currentProviderName, report);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  private static ModelPickerRow Row(ModelPickerViewModel vm, string modelId)
      => vm.FilteredRows.First(r => r.ModelId == modelId);

  // ---- The model level ----

  [Fact]
  public async Task LoadAsync_Populates_Rows_Deduped_By_Model_Sorted_ByName()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
      Entry("zeta/model"),
      Entry("alpha/model", providerName: "ProviderB"),
      Entry("alpha/model", providerName: "ProviderC"),
    ]);

    // One row per model id — the catalog's per-provider endpoints collapse at this level.
    Assert.Equal(["alpha/model", "zeta/model"], vm.FilteredRows.Select(r => r.ModelId));
  }

  [Fact]
  public async Task TheModelRow_SummarisesItsBestProvider_NotTheCheapestPromptPrice()
  {
    // The defect this surface replaces: the row was priced by the endpoint with the cheapest
    // PROMPT price, which for a cache-heavy workload is the most expensive choice. The
    // summary now names the provider the current assumption actually favours.
    ModelPickerViewModel vm = await LoadAsync(
    [
      Entry(providerName: "Expensive", promptPrice: 0.000009m, completionPrice: 0.000045m),
      Entry(providerName: "Cheap", promptPrice: 0.000001m, completionPrice: 0.000002m),
    ]);

    ModelPickerRow row = Assert.Single(vm.FilteredRows);
    Assert.Contains("Cheap", row.Detail, StringComparison.Ordinal);
    Assert.Contains("2 provider(s)", row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheModelRow_Includes_Context_Size()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(context: 1_048_576)]);

    Assert.Contains("1M", Assert.Single(vm.FilteredRows).Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Negative_Pricing_Renders_PricingVaries()
  {
    // OpenRouter reports -1 per-token prices on routing pseudo-models such as
    // openrouter/auto: the raw number must never render as a dollar figure.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry("openrouter/auto", promptPrice: -1, completionPrice: -1, context: 2_000_000)]);

    string detail = Assert.Single(vm.FilteredRows).Detail;
    Assert.Contains("pricing varies", detail, StringComparison.Ordinal);
    Assert.Contains("2M", detail, StringComparison.Ordinal);
    Assert.DoesNotContain("-", detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AbsentPricing_Renders_PricingUnknown_NotZero()
  {
    // Issue #289: the catalog says "unknown" (null) where it used to say zero, so an
    // unpriced model must render as unknown. Rendering "$0" would tell the user a paid model
    // is free - the exact lie nullable prices exist to prevent.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry("mystery/model", promptPrice: null, completionPrice: null)]);

    string detail = Assert.Single(vm.FilteredRows).Detail;
    Assert.Contains("pricing unknown", detail, StringComparison.Ordinal);
    Assert.DoesNotContain("$0", detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Search_Filters_Model_Rows_CaseInsensitive()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry("anthropic/claude"), Entry("google/gemini")]);

    vm.SearchText = "CLAUDE";

    ModelPickerRow row = Assert.Single(vm.FilteredRows);
    Assert.Equal("anthropic/claude", row.ModelId);
  }

  [Fact]
  public async Task LoadAsync_Preselects_Current_Model_Else_Nothing()
  {
    ModelPickerViewModel current = await LoadAsync(
        [Entry("alpha/model"), Entry("beta/model")], currentModelId: "beta/model");
    ModelPickerViewModel nothing = await LoadAsync([Entry("alpha/model")], currentModelId: null);

    Assert.Equal("beta/model", current.SelectedRow!.ModelId);
    Assert.Null(nothing.SelectedRow);
  }

  // ---- The provider level (B1/D1) ----

  [Fact]
  public async Task SelectingAModel_RevealsItsProviderRows()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
      Entry(providerName: "ProviderA"),
      Entry(providerName: "ProviderB"),
      Entry("other/model", providerName: "ProviderC"),
    ]);

    Assert.Empty(vm.ProviderRows); // nothing selected yet
    Assert.False(vm.HasProviderRows);

    vm.SelectedRow = Row(vm, Model);

    Assert.True(vm.HasProviderRows);
    Assert.Equal(["ProviderA", "ProviderB"],
        vm.ProviderRows.Select(r => r.ProviderName).Order(StringComparer.Ordinal));
    Assert.Contains(Model, vm.ProviderLevelCaption, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheProviderLevel_PreSelectsTheCommittedPair()
  {
    // Reopening the picker shows the pair the session committed, so the user can see what is
    // serving and change one half of it.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(providerName: "ProviderA"), Entry(providerName: "ProviderB")],
        currentModelId: Model, currentProviderName: "ProviderB");

    Assert.Equal("ProviderB", vm.SelectedProviderRow!.ProviderName);
  }

  [Fact]
  public async Task AProviderRowCarriesItsEvidence_WithUnavailableValuesMarked()
  {
    // D5: every dimension D3 names renders, and an absent one says so. This workspace's
    // ledger currently has no timed rows, so 'not measured' is the production state.
    ModelPickerViewModel vm = await LoadAsync([Entry(providerName: "ProviderA")],
        currentModelId: Model);

    ProviderEvidenceRow row = Assert.Single(vm.ProviderRows);
    Assert.Equal("ProviderA", row.ProviderName);
    Assert.Contains("effective", row.EffectivePriceText, StringComparison.Ordinal);
    Assert.Equal(ProviderPerformanceText.NotMeasured, row.LatencyText);
    Assert.Equal(ProviderPerformanceText.NotMeasured, row.ThroughputText);
    Assert.Equal(ProviderPerformanceText.NotMeasured, row.UptimeText);
    Assert.Contains("200K ctx", row.ContextText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnAllUnmeasuredReport_StillRendersEveryProviderRow()
  {
    // The measured state of this workspace's ledger: 11,435 usage rows, zero with a
    // DurationMs. The picker must render that as an ordinary picker, not as empty.
    ProviderPerformanceReport report = new(UsageScope.Session, null, 0, 0, 0, 0, []);
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(providerName: "ProviderA"), Entry(providerName: "ProviderB")],
        currentModelId: Model, report: report);

    Assert.Equal(2, vm.ProviderRows.Count);
    Assert.All(vm.ProviderRows, row =>
        Assert.Equal(ProviderPerformanceText.NotMeasured, row.LatencyText));
    // Nothing measured means the documented default is in force, and the surface says so.
    Assert.Equal(HitRateSource.Default, vm.Assumption.Source);
    Assert.Contains("default", vm.HitRateSourceText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AMeasuredLatency_ReachesTheRow()
  {
    ProviderPerformanceReport report = new(UsageScope.Session, 0.97m, 1000, 970, 2, 0,
    [
      new ProviderPerformance(Model, "ProviderA", 412d, 900d, 120d, 88d, 2, 2, 2, 0, 2, 1000, 970),
    ]);
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(providerName: "ProviderA"), Entry(providerName: "ProviderB")],
        currentModelId: Model, report: report);

    ProviderEvidenceRow measured = vm.ProviderRows.First(r => r.ProviderName == "ProviderA");
    Assert.Equal("412 ms", measured.LatencyText);
    Assert.Equal("88 tok/s", measured.ThroughputText);
    // The pair with no rows keeps the honest absence.
    Assert.Equal(ProviderPerformanceText.NotMeasured,
        vm.ProviderRows.First(r => r.ProviderName == "ProviderB").LatencyText);
  }

  // ---- The hit-rate assumption (B4/D6) ----

  [Fact]
  public async Task TheMeasuredRate_IsAdopted_AndItsSourceIsNamed()
  {
    ProviderPerformanceReport report = new(UsageScope.Workspace, 0.88m, 1000, 880, 3, 0, []);
    ModelPickerViewModel vm = await LoadAsync([Entry()], currentModelId: Model, report: report);

    Assert.Equal(0.88m, vm.Assumption.Rate);
    Assert.Equal(HitRateSource.Workspace, vm.Assumption.Source);
    Assert.Contains("workspace measured 88%", vm.HitRateSourceText, StringComparison.Ordinal);
    Assert.Equal("88", vm.HitRateText);
  }

  [Fact]
  public async Task AChangedRate_ReprojectsThePrices_AndSaysItIsManual()
  {
    // The single most instructive interaction this surface offers: the same catalog, ranked
    // at a rate the user chose, with the surface stating that the rate is theirs.
    ModelPickerViewModel vm = await LoadAsync(
    [
      Caching("LowCache", promptPrice: 0.00000004m, cacheReadPrice: 0.00000003m),
      Caching("HighCache", promptPrice: 0.00000008m, cacheReadPrice: 0.000000008m),
    ], currentModelId: Model);
    string before = vm.ProviderRows[0].EffectivePriceText;

    vm.HitRateText = "0";
    vm.ApplyHitRateCommand.Execute(null);

    Assert.Equal(HitRateSource.Manual, vm.Assumption.Source);
    Assert.Contains("manual", vm.HitRateSourceText, StringComparison.Ordinal);
    Assert.NotEqual(before, vm.ProviderRows[0].EffectivePriceText);
    // At a zero hit rate the cheap prompt price wins; the ranking follows the assumption.
    Assert.Equal("LowCache", vm.ProviderRows[0].ProviderName);
  }

  [Theory]
  [InlineData("abc")]
  [InlineData("101")]
  [InlineData("-5")]
  [InlineData("")]
  public async Task AMalformedRate_IsRefusedVisibly_AndChangesNothing(string text)
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], currentModelId: Model);
    HitRateAssumption before = vm.Assumption;

    vm.HitRateText = text;
    vm.ApplyHitRateCommand.Execute(null);

    Assert.NotNull(vm.HitRateError);
    Assert.Same(before, vm.Assumption); // never a silent clamp
  }

  [Fact]
  public async Task ATrailingPercentSign_IsAccepted()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], currentModelId: Model);

    vm.HitRateText = "75%";
    vm.ApplyHitRateCommand.Execute(null);

    Assert.Null(vm.HitRateError);
    Assert.Equal(0.75m, vm.Assumption.Rate);
  }

  // ---- Presets reorder, never hide (B5/D4) ----

  [Fact]
  public async Task EveryPreset_KeepsEveryProviderRow()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
      Entry(providerName: "ProviderA", uptime: 99.9),
      Entry(providerName: "ProviderB", uptime: 87.0),
      Entry(providerName: "ProviderC"),
    ], currentModelId: Model);

    foreach (PresetOption option in vm.PresetOptions)
    {
      vm.SelectedPresetOption = option;
      Assert.Equal(3, vm.ProviderRows.Count);
    }
  }

  [Fact]
  public async Task ChangingThePreset_Reorders_AndStatesTheDrivingDimension()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
      Entry(providerName: "Reliable", promptPrice: 0.000009m, uptime: 99.9),
      Entry(providerName: "Cheap", promptPrice: 0.000001m, uptime: 87.0),
    ], currentModelId: Model);

    Assert.Equal("Cheap", vm.ProviderRows[0].ProviderName);
    Assert.Contains("effective price", vm.OrderingCaption, StringComparison.Ordinal);

    vm.SelectedPresetOption = vm.PresetOptions.First(o => o.Preset == ProviderPreset.MostReliable);

    Assert.Equal("Reliable", vm.ProviderRows[0].ProviderName);
    Assert.Contains("reliability", vm.OrderingCaption, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ChangingThePreset_DoesNotLoseTheSelectedProvider()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
      Entry(providerName: "ProviderA", promptPrice: 0.000001m, uptime: 87.0),
      Entry(providerName: "ProviderB", promptPrice: 0.000009m, uptime: 99.9),
    ], currentModelId: Model);
    vm.SelectedProviderRow = vm.ProviderRows.First(r => r.ProviderName == "ProviderA");

    vm.SelectedPresetOption = vm.PresetOptions.First(o => o.Preset == ProviderPreset.MostReliable);

    ProviderEvidenceRow? kept = vm.SelectedProviderRow;
    Assert.NotNull(kept);
    Assert.Equal("ProviderA", kept.ProviderName);
  }

  // ---- The confirm payload (B6/D7) ----

  [Fact]
  public async Task Confirm_EmitsTheSelectedPair()
  {
    ModelChoice? received = null;
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(providerName: "ProviderA"), Entry(providerName: "ProviderB")]);
    vm.ConfirmRequested += (_, choice) => received = choice;

    vm.SelectedRow = Row(vm, Model);
    vm.SelectedProviderRow = vm.ProviderRows.First(r => r.ProviderName == "ProviderB");
    vm.ConfirmCommand.Execute(null);

    Assert.NotNull(received);
    Assert.Equal(Model, received.ModelId);
    Assert.Equal("ProviderB", received.ProviderName);
  }

  [Fact]
  public async Task Confirm_Without_Selection_Does_Not_Emit()
  {
    ModelChoice? received = null;
    ModelPickerViewModel vm = await LoadAsync([Entry()]);
    vm.ConfirmRequested += (_, choice) => received = choice;

    Assert.Null(vm.SelectedRow);
    vm.ConfirmCommand.Execute(null);

    Assert.Null(received);
  }

  [Fact]
  public async Task Confirm_WithoutAProviderSelection_LeavesTheProviderUnset()
  {
    // A model pinned without a provider is half a decision, and the provider stays unset
    // rather than guessed.
    ModelChoice? received = null;
    ModelPickerViewModel vm = await LoadAsync([Entry()]);
    vm.ConfirmRequested += (_, choice) => received = choice;

    vm.SelectedRow = Row(vm, Model);
    vm.ConfirmCommand.Execute(null);

    Assert.NotNull(received);
    Assert.Equal(Model, received.ModelId);
    Assert.Null(received.ProviderName);
  }

  // ---- Loading and failure ----

  [Fact]
  public async Task LoadAsync_Catalog_Failure_Lands_In_LoadError_And_No_Model_Rows()
  {
    ModelPickerViewModel vm = new(
        _ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogDown", "catalog unreachable"))),
        currentModelId: null);

    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal("catalog unreachable", vm.LoadError);
    Assert.Null(vm.SelectedRow);
    Assert.False(vm.IsLoading);
  }

  [Fact]
  public async Task APerformanceReadFailure_KeepsThePickerUsable()
  {
    // A read-model fault must not fail the surface: the prices still render and the
    // measurements say they are unavailable.
    ModelPickerViewModel vm = new(
        _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry()])),
        Model,
        loadPerformance: _ => Task.FromResult(Result.Failure<ProviderPerformanceReport>(
            new DomainError("LedgerDown", "ledger unreachable"))));

    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Contains("ledger unreachable", vm.LoadError!, StringComparison.Ordinal);
    Assert.Equal(HitRateSource.Default, vm.Assumption.Source);
    Assert.Equal(ProviderPerformanceText.NotMeasured, Assert.Single(vm.ProviderRows).LatencyText);
  }

  [Fact]
  public async Task LoadAsync_Is_Idempotent_Second_Call_Does_Not_Reload()
  {
    int loads = 0;
    ModelPickerViewModel vm = new(_ =>
    {
      loads++;
      return Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry()]));
    }, currentModelId: null);

    await vm.LoadAsync().ConfigureAwait(true);
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal(1, loads);
  }
}
