using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Behavior of the model picker view-model: one model list, one routing-variant dropdown.
///     <para>WHAT THIS FILE NO LONGER COVERS, DELIBERATELY: the provider level, the ordering
///     presets, the per-pair cache-hit-rate resolution, and the evidence rows. Those tested a
///     surface the user asked to remove - comparing endpoints was a decision the harness could
///     rank from measurements, and the user's real choice is the tradeoff (speed or price),
///     which OpenRouter publishes as routing variants. The stats behind that surface are
///     untouched and still tested where they live (the usage ledger and the catalog ACLs).
///     </para></summary>
public class ModelPickerViewModelTests
{
  private const string Model = "alpha/model";

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
      string? currentModelId = null,
      ModelVariant? currentVariant = null)
      => new(_ => Task.FromResult(Result.Success(entries)), currentModelId, currentVariant);

  private static async Task<ModelPickerViewModel> LoadAsync(
      IReadOnlyList<ModelProviderEntry> entries,
      string? currentModelId = null,
      ModelVariant? currentVariant = null)
  {
    ModelPickerViewModel vm = CreateVm(entries, currentModelId, currentVariant);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  [Fact]
  public async Task Load_DedupesEndpointsIntoOneRowPerModel()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(), Entry(providerName: "ProviderB"), Entry(modelId: "beta/model")]).ConfigureAwait(true);

    Assert.Equal([Model, "beta/model"],
        vm.FilteredRows.Select(r => r.ModelId).Order(StringComparer.Ordinal));
  }

  [Fact]
  public async Task Load_SummarisesTheModelByItsCheapestEndpoint()
  {
    // The row states the figure a model choice is made on, and it must be the CHEAPEST endpoint -
    // picking arbitrarily (whichever the API listed first) would quote a price the user cannot
    // get under Floor routing.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(promptPrice: 0.00002m), Entry(providerName: "ProviderB", promptPrice: 0.000003m)])
        .ConfigureAwait(true);

    ModelPickerRow row = Assert.Single(vm.FilteredRows);
    Assert.Contains("0.000003", row.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("0.00002", row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Load_StatesAnUnknownPriceAsUnknown_NotAsZero()
  {
    // Three pricing states stay distinct: an advertised price, a price set at routing time, and
    // a price nobody advertised. Rendering unknown as $0 would tell the user a paid model is free.
    ModelPickerViewModel vm = await LoadAsync([Entry(promptPrice: null)]).ConfigureAwait(true);

    ModelPickerRow row = Assert.Single(vm.FilteredRows);
    Assert.Contains(ProviderEvidenceRow.PricingUnknown, row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Load_PreSelectsTheSessionsModel()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: "beta/model")], Model)
        .ConfigureAwait(true);

    Assert.Equal(Model, vm.SelectedRow?.ModelId);
  }

  [Fact]
  public void VariantOptions_OffersEveryVariantInPickerOrder()
  {
    ModelPickerViewModel vm = CreateVm([Entry()]);

    Assert.Equal(
        [ModelVariant.Standard, ModelVariant.Nitro, ModelVariant.Floor, ModelVariant.CacheFloor],
        vm.VariantOptions.Select(o => o.Variant));
  }

  [Fact]
  public void VariantOptions_DefaultToStandard()
    => Assert.Equal(ModelVariant.Standard, CreateVm([Entry()]).SelectedVariantOption.Variant);

  [Fact]
  public void VariantOptions_PreSelectTheSessionsCommittedVariant()
  {
    // Reopening the picker must show the tradeoff it committed: a dialog that silently reset a
    // nitro session to Standard on the next save would undo the user's choice without saying so.
    ModelPickerViewModel vm = CreateVm([Entry()], Model, ModelVariant.Nitro);

    Assert.Equal(ModelVariant.Nitro, vm.SelectedVariantOption.Variant);
  }

  [Fact]
  public async Task Confirm_CarriesTheModelAndTheSelectedVariant()
  {
    // THE VARIANT IS HALF THE DECISION. A confirm that carried only the model would leave the
    // turn routing on Standard while the dialog reported the tradeoff the user chose.
    ModelPickerViewModel vm = await LoadAsync([Entry()], Model).ConfigureAwait(true);
    vm.SelectedVariantOption = vm.VariantOptions.First(o => o.Variant == ModelVariant.Floor);
    ModelChoice? received = null;
    vm.ConfirmRequested += (_, choice) => received = choice;

    vm.ConfirmCommand.Execute(null);

    Assert.NotNull(received);
    Assert.Equal(Model, received.ModelId);
    Assert.Equal(ModelVariant.Floor, received.Variant);
  }

  [Fact]
  public void Confirm_IsUnavailableWithNothingSelected()
      => Assert.False(CreateVm([Entry()]).ConfirmCommand.CanExecute(null));

  [Fact]
  public async Task Search_FiltersCaseInsensitivelyOnTheModelId()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: "beta/other")]).ConfigureAwait(true);

    vm.SearchText = "BETA";

    Assert.Equal("beta/other", Assert.Single(vm.FilteredRows).ModelId);
  }

  [Fact]
  public async Task Search_PreservesTheSelectionByIdentity_SoTheListDoesNotDropIt()
  {
    // The bound list control drops its selection when ItemsSource is replaced and writes null
    // straight back into SelectedRow; the view-model restores it by id afterwards.
    ModelPickerViewModel vm = await LoadAsync([Entry(), Entry(modelId: "beta/model")], Model)
        .ConfigureAwait(true);

    vm.SearchText = "alpha";

    Assert.Equal(Model, vm.SelectedRow?.ModelId);
  }

  [Fact]
  public async Task ALoadFailure_LandsInTheErrorState_AndLeavesTheDialogUsable()
  {
    ModelPickerViewModel vm = new(
        _ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogDown", "boom"))), null);

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
        }, null);

    await vm.LoadAsync().ConfigureAwait(true);
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal(1, calls);
  }

  [Fact]
  public async Task ApplyRefresh_RebuildsTheListAndKeepsTheSelection()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()], Model).ConfigureAwait(true);

    vm.ApplyRefresh(Result.Success<IReadOnlyList<ModelProviderEntry>>(
        [Entry(), Entry(modelId: "beta/model")]));

    Assert.Equal(2, vm.FilteredRows.Count);
    Assert.Equal(Model, vm.SelectedRow?.ModelId);
  }

  [Fact]
  public async Task ApplyRefresh_Failure_KeepsCachedModelsAndStatesWhy()
  {
    ModelPickerViewModel vm = await LoadAsync([Entry()]).ConfigureAwait(true);

    vm.ApplyRefresh(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
        new DomainError("CatalogDown", "boom")));

    _ = Assert.Single(vm.FilteredRows);
    Assert.NotNull(vm.LoadError);
  }

  [Fact]
  public async Task AModelWithAVariantSuffixInTheCatalog_IsListedUnderItsBaseId()
  {
    // The catalog is keyed on real model ids; a model whose ID genuinely ends in ':free' is a
    // PRICING variant and must not be split or stripped by the routing-variant codec.
    ModelPickerViewModel vm = await LoadAsync([Entry(modelId: "vendor/model:free")]).ConfigureAwait(true);

    Assert.Equal("vendor/model:free", Assert.Single(vm.FilteredRows).ModelId);
  }
}
