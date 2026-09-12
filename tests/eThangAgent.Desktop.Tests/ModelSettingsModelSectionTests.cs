using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan items 116-117: the Model and Effort rail entries fold into
///     the Model Settings window. The window's view-model owns the model choice:
///     a searchable catalog section (auto row where the provider offers one),
///     pre-selected from the session's live choice, applied on save through the
///     snapshot's tri-state model field - unchanged / auto / pinned id. The rail
///     keeps no per-choice entries: MainWindow exposes neither the model nor the
///     effort picker anymore, and the settings window is the single surface.</summary>
public class ModelSettingsModelSectionTests
{
  private static ModelProviderEntry Entry(string modelId) => new(
      modelId, "ProviderA", 0.000003m, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, IntelligenceScore: null,
      CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  private static Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> Loader(
      params ModelProviderEntry[] entries)
      => _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));

  private const string ProvidersOpenRouter = "openrouter";

  private static SessionModelPreferences Pinned(string modelId) => new() { ModelId = modelId };

  [Fact]
  public async Task ModelSection_LoadsCatalog_AndPreservesThePickerContract()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter,
        persist: _ => { }, loadCatalog: Loader(Entry("alpha/model"), Entry("zeta/model")),
        allowAuto: true, currentModelId: "zeta/model");

    await vm.LoadModelCatalogAsync();

    Assert.False(vm.ModelSection!.IsLoading);
    Assert.Null(vm.ModelSection.LoadError);
    Assert.Equal(["Auto (smart selection)", "alpha/model", "zeta/model"],
        vm.ModelSection.FilteredRows.Select(r => r.DisplayName));
    ModelPickerRow? selected = vm.ModelSection.SelectedRow;
    Assert.Equal("zeta/model", selected!.ModelId); // live choice pre-selected
  }

  [Fact]
  public async Task CatalogFailure_LandsInTheSectionErrorState()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter,
        persist: _ => { }, loadCatalog: _ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("Catalog", "down"))), allowAuto: false, currentModelId: null);

    await vm.LoadModelCatalogAsync();

    Assert.NotNull(vm.ModelSection!.LoadError);
  }

  [Fact]
  public async Task Save_WithUnchangedModel_CarriesUnchangedInTheSnapshot()
  {
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        loadCatalog: Loader(Entry("alpha/model")), allowAuto: true, currentModelId: "alpha/model");
    await vm.LoadModelCatalogAsync();

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received!.Model);
    Assert.Equal(ModelChoiceKind.Unchanged, received.Model.Kind);
    // Unchanged carries no value; the shell's existing model persistence is untouched.
    Assert.Null(received.Model.ModelId);
  }

  [Fact]
  public async Task Save_WithPinnedModel_AppliesAndCarriesTheId()
  {
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        loadCatalog: Loader(Entry("alpha/model"), Entry("beta/model")), allowAuto: true,
        currentModelId: "alpha/model");
    await vm.LoadModelCatalogAsync();
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(r => r.ModelId == "beta/model");

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received!.Model);
    Assert.Equal(ModelChoiceKind.Pinned, received.Model.Kind);
    Assert.Equal("beta/model", received.Model.ModelId);
    Assert.Equal("beta/model", live.ModelId); // applied to the live preferences
  }

  [Fact]
  public async Task Save_WithAutoChoice_ClearsThePinnedId()
  {
    SessionModelPreferences live = Pinned("alpha/model");
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        loadCatalog: Loader(Entry("alpha/model")), allowAuto: true, currentModelId: "alpha/model");
    await vm.LoadModelCatalogAsync();
    vm.ModelSection!.SelectedRow = ModelPickerViewModel.AutoRow;

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received!.Model);
    Assert.Equal(ModelChoiceKind.Auto, received.Model.Kind);
    Assert.Null(received.Model.ModelId);
    Assert.Null(live.ModelId); // auto resets the session to automatic choice
  }

  [Fact]
  public void EffortChoice_IsDisabled_OnLocalSessions_NeverSentThere()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), "local", persist: _ => { });

    Assert.False(vm.IsEffortApplicable);
  }

  [Fact]
  public void EffortChoice_IsEnabled_OnOpenRouterAndZai()
  {
    ModelSettingsViewModel openRouter = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });
    ModelSettingsViewModel zai = new(new SessionModelPreferences(), "zai", persist: _ => { });

    Assert.True(openRouter.IsEffortApplicable);
    Assert.True(zai.IsEffortApplicable);
  }
}
