using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

public class ModelPickerVendorTests
{
  private const string Llama = "meta/llama-3.1-8b-instruct";
  private const string Chat = "deepseek/deepseek-chat";

  private static ModelProviderEntry Entry(
      string modelId,
      string vendor,
      decimal? promptPrice = 0.000003m,
      decimal? completionPrice = 0.000015m,
      bool supportsToolUse = true,
      bool? requiresPaidPlan = null,
      int context = 200_000)
      => new(modelId, vendor, promptPrice, null, completionPrice, context, 8192,
          SupportsToolUse: supportsToolUse, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
          ThroughputTokensPerSec: null, Description: null)
      {
        RequiresPaidPlan = requiresPaidPlan,
      };

  private static ModelPickerViewModel CreateVm(
      IReadOnlyList<ModelProviderEntry> openRouter,
      IReadOnlyList<ModelProviderEntry>? cloudflare = null,
      IReadOnlyList<ModelSelectionEntry>? current = null,
      DomainError? cloudflareFailure = null)
  {
    Dictionary<string, Result<IReadOnlyList<ModelProviderEntry>>> catalogs =
        new(StringComparer.Ordinal)
        {
          [ModelVendors.OpenRouter] = Result.Success(openRouter),
        };
    if (cloudflare is not null || cloudflareFailure is not null)
    {
      catalogs[ModelVendors.Cloudflare] = cloudflareFailure is { } failure
          ? Result.Failure<IReadOnlyList<ModelProviderEntry>>(failure)
          : Result.Success(cloudflare!);
    }

    return new ModelPickerViewModel(
        (vendor, _) => Task.FromResult(catalogs.TryGetValue(vendor, out Result<IReadOnlyList<ModelProviderEntry>>? answer)
            ? answer
            : Result.Failure<IReadOnlyList<ModelProviderEntry>>(
                new DomainError("UnknownProvider", vendor))),
        current ?? [], vendors: [.. catalogs.Keys]);
  }

  private static async Task<ModelPickerViewModel> LoadAsync(
      IReadOnlyList<ModelProviderEntry> openRouter,
      IReadOnlyList<ModelProviderEntry>? cloudflare = null,
      IReadOnlyList<ModelSelectionEntry>? current = null,
      DomainError? cloudflareFailure = null)
  {
    ModelPickerViewModel vm = CreateVm(openRouter, cloudflare, current, cloudflareFailure);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  private static ModelPickerRow Select(ModelPickerViewModel vm, string modelId, string vendor)
  {
    vm.SearchText = modelId;
    ModelPickerRow row = vm.FilteredRows.First(candidate => candidate.ModelId == modelId
        && string.Equals(candidate.Vendor, vendor, StringComparison.Ordinal));
    vm.SelectedRow = row;
    return row;
  }

  // behaviour 1: every credentialed vendor contributes rows, and each names its vendor

  [Fact]
  public async Task Rows_FromEveryOfferedVendor_Appear_AndEachNamesItsVendor()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare)]).ConfigureAwait(true);

    Assert.Equal([ModelVendors.Cloudflare, ModelVendors.OpenRouter],
        vm.FilteredRows.Select(row => row.Vendor).Order(StringComparer.Ordinal));
    Assert.All(vm.FilteredRows, row => Assert.Contains(ModelVendors.DisplayName(row.Vendor),
        row.DisplayName, StringComparison.Ordinal));
  }

  // behaviour 2: one model id under two vendors is two rows, priced by each vendor

  [Fact]
  public async Task TheSameModelFromTwoVendors_IsTwoRows_EachWithItsOwnPrice()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Llama, ModelVendors.OpenRouter, promptPrice: 0.000001m)],
        [Entry(Llama, ModelVendors.Cloudflare, promptPrice: 0.000004m)]).ConfigureAwait(true);

    ModelPickerRow[] rows = [.. vm.FilteredRows.Where(row => row.ModelId == Llama)];
    Assert.Equal(2, rows.Length);
    Assert.Contains("0.000001", rows.Single(row => row.Vendor == ModelVendors.OpenRouter).Detail,
        StringComparison.Ordinal);
    Assert.Contains("0.000004", rows.Single(row => row.Vendor == ModelVendors.Cloudflare).Detail,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheSameModelFromTwoVendors_CanBothBeAdded()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Llama, ModelVendors.OpenRouter)], [Entry(Llama, ModelVendors.Cloudflare)]);

    _ = Select(vm, Llama, ModelVendors.OpenRouter);
    vm.AddModelCommand.Execute(null);
    _ = Select(vm, Llama, ModelVendors.Cloudflare);
    vm.AddModelCommand.Execute(null);

    Assert.Null(vm.SelectionError);
    Assert.Equal([ModelVendors.OpenRouter, ModelVendors.Cloudflare],
        vm.SelectedModels.Select(row => row.Vendor));
  }

  // behaviour 3: no Cloudflare credential means no Cloudflare row

  [Fact]
  public async Task WithoutACloudflareCredential_NoCloudflareRowAppears()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)], [Entry(Llama, ModelVendors.Cloudflare)]);
    Assert.Contains(vm.FilteredRows, row => row.Vendor == ModelVendors.Cloudflare);

    ModelPickerViewModel withoutCredential = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)]).ConfigureAwait(true);

    Assert.Equal([ModelVendors.OpenRouter], withoutCredential.FilteredRows.Select(r => r.Vendor));
  }

  // behaviour 4: a model that cannot call tools is shown and refused on add

  [Fact]
  public async Task AModelWithoutToolSupport_IsShown_AndRefusedOnAdd_NamingToolSupport()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare, supportsToolUse: false)]).ConfigureAwait(true);

    ModelPickerRow row = Select(vm, Llama, ModelVendors.Cloudflare);
    Assert.False(row.IsAddable);
    Assert.Contains("tool", row.Refusal!, StringComparison.OrdinalIgnoreCase);

    vm.AddModelCommand.Execute(null);

    Assert.Empty(vm.SelectedModels);
    Assert.Contains("tool", vm.SelectionError!, StringComparison.OrdinalIgnoreCase);
  }

  // behaviour 5: the paid-plan marker is tri-state

  [Fact]
  public async Task AModelNeedingAPaidPlan_IsShownMarkedAndAddable()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare, requiresPaidPlan: true)]).ConfigureAwait(true);

    ModelPickerRow row = Select(vm, Llama, ModelVendors.Cloudflare);
    Assert.True(row.ShowsPaidPlanMarker);
    Assert.Contains("paid plan", row.PaidPlanMarker!, StringComparison.Ordinal);
    Assert.True(row.IsAddable);

    vm.AddModelCommand.Execute(null);

    Assert.Null(vm.SelectionError);
    Assert.Equal(ModelVendors.Cloudflare, Assert.Single(vm.SelectedModels).Vendor);
  }

  [Fact]
  public async Task AModelWhoseVendorSaidNothingAboutAPlan_CarriesNoMarker()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare, requiresPaidPlan: null)]).ConfigureAwait(true);

    ModelPickerRow row = Select(vm, Llama, ModelVendors.Cloudflare);

    Assert.False(row.ShowsPaidPlanMarker);
    Assert.Null(row.PaidPlanMarker);
    Assert.True(row.IsAddable);
  }

  // behaviour 6: the price is the vendor's own, and unknown is stated

  [Fact]
  public async Task ARowWithNoPublishedPrice_StatesThePriceIsUnknown_RatherThanZero()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare, promptPrice: null, completionPrice: null)])
        .ConfigureAwait(true);

    ModelPickerRow row = Select(vm, Llama, ModelVendors.Cloudflare);

    Assert.Contains(ProviderEvidenceRow.PricingUnknown, row.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("$0", row.Detail, StringComparison.Ordinal);
  }

  // behaviour 7: the selected list names each entry's vendor and position

  [Fact]
  public async Task TheSelectedList_ShowsEachEntriesVendorAndItsPosition()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare)],
        [
          new ModelSelectionEntry(Llama, ModelVendors.Cloudflare),
          new ModelSelectionEntry(Chat, ModelVendors.OpenRouter),
        ]).ConfigureAwait(true);

    Assert.Equal([1, 2], vm.SelectedModels.Select(row => row.Position));
    Assert.Equal([ModelVendors.Cloudflare, ModelVendors.OpenRouter],
        vm.SelectedModels.Select(row => row.Vendor));
    Assert.Contains(ModelVendors.DisplayName(ModelVendors.Cloudflare), vm.SelectedModels[0].DisplayName,
        StringComparison.Ordinal);
    Assert.Contains("runs first", vm.SelectedModels[0].Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("runs first", vm.SelectedModels[1].Detail, StringComparison.Ordinal);
  }

  // behaviour 8: no cap, and the order stays editable

  [Fact]
  public async Task ASelectionOfSevenEntries_IsShownWhole_AddStaysEnabled_AndReorderingWorks()
  {
    string[] ids = [Chat, Llama, "gamma/model", "delta/model", "epsilon/model", "zeta/model",
      "eta/model"];
    ModelPickerViewModel vm = await LoadAsync(
        [.. ids.Select(id => Entry(id, ModelVendors.OpenRouter))],
        [Entry(Llama, ModelVendors.Cloudflare)],
        [.. ids.Select(id => new ModelSelectionEntry(id, ModelVendors.OpenRouter))])
        .ConfigureAwait(true);

    Assert.Equal(7, vm.SelectedModels.Count);
    Assert.Equal([1, 2, 3, 4, 5, 6, 7], vm.SelectedModels.Select(row => row.Position));

    _ = Select(vm, Llama, ModelVendors.Cloudflare);
    Assert.True(vm.AddModelCommand.CanExecute(null));
    vm.AddModelCommand.Execute(null);
    Assert.Equal(8, vm.SelectedModels.Count);
    Assert.Null(vm.SelectionError);

    vm.SelectedModel = vm.SelectedModels[7];
    vm.MoveUpCommand.Execute(null);
    Assert.Equal(ModelVendors.Cloudflare, vm.SelectedModels[6].Vendor);
    Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], vm.SelectedModels.Select(row => row.Position));
  }

  // behaviour 9: the routing variant covers OpenRouter entries, and says so

  [Fact]
  public void TheVariantCaption_StatesThatItCoversOpenRouterEntriesOnly()
      => Assert.StartsWith("OpenRouter entries only.", ModelPickerViewModel.VariantCaption,
          StringComparison.Ordinal);

  [Fact]
  public async Task ACloudflareEntry_CarriesNoRoutingVariant()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare)],
        [new ModelSelectionEntry(Llama, ModelVendors.Cloudflare)]).ConfigureAwait(true);
    vm.SelectedVariantOption = vm.VariantOptions.First(option => option.Variant == ModelVariant.Cheapest);

    Assert.Null(vm.CheapestSentence);
    Assert.Equal(ModelVendors.Cloudflare, Assert.Single(vm.CurrentSelection().Entries).Vendor);
  }

  [Fact]
  public async Task AnOpenRouterEntry_KeepsTheRoutingVariantAndItsProjection()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare)],
        [new ModelSelectionEntry(Chat, ModelVendors.OpenRouter)]).ConfigureAwait(true);
    vm.SelectedVariantOption = vm.VariantOptions.First(option => option.Variant == ModelVariant.Cheapest);

    Assert.NotNull(vm.CheapestSentence);
  }

  // behaviour 10: one vendor's failure is stated and the other's rows stay

  [Fact]
  public async Task ACloudflareCatalogFailure_IsStatedForCloudflare_AndLeavesTheOpenRouterRows()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        cloudflareFailure: new DomainError("CatalogUnavailable", "Cloudflare refused the read"))
        .ConfigureAwait(true);

    Assert.Equal([ModelVendors.OpenRouter], vm.FilteredRows.Select(row => row.Vendor));
    string failure = Assert.Single(vm.CatalogErrors);
    Assert.Contains(ModelVendors.DisplayName(ModelVendors.Cloudflare), failure, StringComparison.Ordinal);
    Assert.Contains("Cloudflare refused the read", failure, StringComparison.Ordinal);
    Assert.Contains("Cloudflare", vm.LoadError!, StringComparison.Ordinal);
  }

  // behaviour 11: the vendor survives into the preferences the walk reads

  [Fact]
  public async Task ASelectedCloudflareEntry_SerializesAndRestoresWithItsVendor()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry(Chat, ModelVendors.OpenRouter)],
        [Entry(Llama, ModelVendors.Cloudflare)],
        [new ModelSelectionEntry(Chat, ModelVendors.OpenRouter)]).ConfigureAwait(true);

    _ = Select(vm, Llama, ModelVendors.Cloudflare);
    vm.AddModelCommand.Execute(null);

    ModelSelection selection = vm.CurrentSelection();
    Assert.Equal([ModelVendors.OpenRouter, ModelVendors.Cloudflare],
        selection.Entries.Select(entry => entry.Vendor));

    Result<ModelSelection> restored = ModelSelection.Parse(selection.Serialize());
    Assert.True(restored.IsSuccess, restored.Error?.Message);
    Assert.Equal([ModelVendors.OpenRouter, ModelVendors.Cloudflare],
        restored.Value.Entries.Select(entry => entry.Vendor));

    SessionModelPreferences preferences = new() { Entries = selection.Entries };
    Assert.Equal([Chat, Llama], preferences.ModelIds);
    Assert.Equal(ModelVendors.Cloudflare, preferences.Entries?[1].Vendor);
  }
}
