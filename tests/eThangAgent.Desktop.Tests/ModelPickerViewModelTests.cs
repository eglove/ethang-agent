using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Behavior of the model picker view-model: deduping, search, the auto row,
///     pre-selection, and the confirm payload. Catalog loading is faked at the
///     loader-delegate seam — the real catalog clients are covered in their ACL tests.</summary>
public class ModelPickerViewModelTests
{
  private static ModelProviderEntry Entry(
      string modelId,
      string providerName = "ProviderA",
      decimal promptPrice = 0.000003m,
      decimal completionPrice = 0.000015m,
      int context = 200_000)
      => new(modelId, providerName, promptPrice, completionPrice, context,
          8192, SupportsToolUse: true, SupportsVision: false,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null);

  private static ModelPickerViewModel CreateVm(
      IReadOnlyList<ModelProviderEntry> entries,
      bool allowAuto = true,
      string? currentModelId = null)
      => new(_ => Task.FromResult(Result.Success(entries)),
          allowAuto, currentModelId);

  private static async Task<ModelPickerViewModel> LoadAsync(
      IReadOnlyList<ModelProviderEntry> entries, bool allowAuto = true, string? currentModelId = null)
  {
    ModelPickerViewModel vm = CreateVm(entries, allowAuto, currentModelId);
    await vm.LoadAsync().ConfigureAwait(true);
    return vm;
  }

  [Fact]
  public async Task LoadAsync_Populates_Rows_Deduped_By_Model_Sorted_ByName()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
        Entry("zeta/model"),
            Entry("alpha/model", providerName: "ProviderB"),
            Entry("alpha/model", providerName: "ProviderC"),
    ], allowAuto: false);

    // One row per model id — the catalog's per-provider endpoints collapse.
    Assert.Equal(["alpha/model", "zeta/model"], vm.FilteredRows.Select(r => r.ModelId));
  }

  [Fact]
  public async Task LoadAsync_Cheapest_Endpoint_Represents_The_Model()
  {
    ModelPickerViewModel vm = await LoadAsync(
    [
        Entry("alpha/model", providerName: "Expensive", promptPrice: 0.000009m, completionPrice: 0.000045m),
            Entry("alpha/model", providerName: "Cheap", promptPrice: 0.000001m, completionPrice: 0.000002m),
    ], allowAuto: false);

    ModelPickerRow row = Assert.Single(vm.FilteredRows);
    // Prices scale to per-million tokens of the CHEAPEST endpoint.
    Assert.Contains("$1 in / $2 out", row.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("$9", row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadAsync_Detail_Includes_Context_Size()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry("alpha/model", context: 1_048_576)], allowAuto: false);

    Assert.Contains("1M ctx", Assert.Single(vm.FilteredRows).Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadAsync_Negative_Pricing_Renders_PricingVaries()
  {
    // OpenRouter reports -1 per-token prices on routing pseudo-models such as
    // openrouter/auto: the raw number must never render as a dollar figure.
    ModelPickerViewModel vm = await LoadAsync(
        [Entry("openrouter/auto", promptPrice: -1, completionPrice: -1, context: 2_000_000)],
        allowAuto: false);

    string detail = Assert.Single(vm.FilteredRows).Detail;
    Assert.Contains("pricing varies", detail, StringComparison.Ordinal);
    Assert.Contains("2M ctx", detail, StringComparison.Ordinal);
    Assert.DoesNotContain("-", detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadAsync_Negative_Priced_Endpoint_Never_Wins_The_Cheapest_Comparison()
  {
    // One routing endpoint (-1) alongside normally-priced endpoints for the same
    // model: the row must be priced by the cheapest REAL endpoint, not the -1.
    ModelPickerViewModel vm = await LoadAsync(
    [
        Entry("alpha/model", providerName: "Routing", promptPrice: -1, completionPrice: -1),
            Entry("alpha/model", providerName: "Cheap", promptPrice: 0.000001m, completionPrice: 0.000002m),
            Entry("alpha/model", providerName: "Expensive", promptPrice: 0.000009m, completionPrice: 0.000045m),
    ], allowAuto: false);

    string detail = Assert.Single(vm.FilteredRows).Detail;
    Assert.Contains("$1 in / $2 out", detail, StringComparison.Ordinal);
    Assert.DoesNotContain("pricing varies", detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AutoRow_Pinned_First_When_Allowed_And_Absent_Otherwise()
  {
    ModelPickerViewModel withAuto = await LoadAsync([Entry("alpha/model")], allowAuto: true);
    ModelPickerViewModel withoutAuto = await LoadAsync([Entry("alpha/model")], allowAuto: false);

    Assert.Null(withAuto.FilteredRows[0].ModelId); // the auto pseudo-row leads the list
    Assert.Equal("alpha/model", withAuto.FilteredRows[1].ModelId);
    Assert.All(withoutAuto.FilteredRows, r => Assert.NotNull(r.ModelId));
  }

  [Fact]
  public async Task Search_Filters_Model_Rows_CaseInsensitive_And_Keeps_Auto_Row()
  {
    ModelPickerViewModel vm = await LoadAsync(
        [Entry("anthropic/claude"), Entry("google/gemini")], allowAuto: true);

    vm.SearchText = "CLAUDE";

    IReadOnlyList<ModelPickerRow> rows = vm.FilteredRows;
    Assert.Equal(2, rows.Count);
    Assert.Null(rows[0].ModelId); // auto survives every filter
    Assert.Equal("anthropic/claude", rows[1].ModelId);
  }

  [Fact]
  public async Task LoadAsync_Preselects_Current_Model_Else_Auto_Else_Nothing()
  {
    ModelPickerViewModel current = await LoadAsync(
        [Entry("alpha/model"), Entry("beta/model")], allowAuto: true, currentModelId: "beta/model");
    ModelPickerViewModel automatic = await LoadAsync(
        [Entry("alpha/model")], allowAuto: true, currentModelId: null);
    ModelPickerViewModel noAuto = await LoadAsync(
        [Entry("alpha/model")], allowAuto: false, currentModelId: null);

    Assert.Equal("beta/model", current.SelectedRow!.ModelId);
    Assert.Null(automatic.SelectedRow!.ModelId); // auto preselected when nothing chosen
    Assert.Null(noAuto.SelectedRow); // no lineup default to guess on z.ai
  }

  [Fact]
  public async Task Confirm_Emits_Choice_With_Selected_Model_Id()
  {
    ModelChoice? received = null;
    ModelPickerViewModel vm = await LoadAsync([Entry("alpha/model")], allowAuto: true);
    vm.ConfirmRequested += (_, choice) => received = choice;

    vm.SelectedRow = Assert.Single(vm.FilteredRows, r => r.ModelId == "alpha/model");
    vm.ConfirmCommand.Execute(null);

    Assert.NotNull(received);
    Assert.Equal("alpha/model", received!.ModelId);
  }

  [Fact]
  public async Task Confirm_Auto_Row_Emits_Null_Model_Choice()
  {
    ModelChoice? received = null;
    ModelPickerViewModel vm = await LoadAsync([Entry("alpha/model")], allowAuto: true);
    vm.ConfirmRequested += (_, choice) => received = choice;

    vm.SelectedRow = vm.FilteredRows[0]; // the auto row
    vm.ConfirmCommand.Execute(null);

    Assert.NotNull(received);
    Assert.Null(received!.ModelId);
  }

  [Fact]
  public async Task Confirm_Without_Selection_Does_Not_Emit()
  {
    ModelChoice? received = null;
    ModelPickerViewModel vm = await LoadAsync([Entry("alpha/model")], allowAuto: false);
    vm.ConfirmRequested += (_, choice) => received = choice;

    Assert.Null(vm.SelectedRow);
    vm.ConfirmCommand.Execute(null);

    Assert.Null(received);
  }

  [Fact]
  public async Task LoadAsync_Catalog_Failure_Lands_In_LoadError_And_No_Model_Rows()
  {
    ModelPickerViewModel vm = new(
        _ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogDown", "catalog unreachable"))),
        allowAuto: true, currentModelId: null);

    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal("catalog unreachable", vm.LoadError);
    Assert.Null(vm.SelectedRow);
    Assert.False(vm.IsLoading);
  }

  [Fact]
  public async Task LoadAsync_Is_Idempotent_Second_Call_Does_Not_Reload()
  {
    int loads = 0;
    ModelPickerViewModel vm = new(_ =>
    {
      loads++;
      return Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry("alpha/model")]));
    }, allowAuto: true, currentModelId: null);

    await vm.LoadAsync().ConfigureAwait(true);
    await vm.LoadAsync().ConfigureAwait(true);

    Assert.Equal(1, loads);
  }
}
