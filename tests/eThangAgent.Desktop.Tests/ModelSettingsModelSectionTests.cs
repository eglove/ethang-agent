using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan items 116-117: the Model and Effort rail entries fold into
///     the Model Settings window. The window's view-model owns the model choice:
///     a searchable catalog section, pre-selected from the session's live choice,
///     applied on save through the snapshot's tri-state model field - unchanged /
///     pinned id. The rail keeps no per-choice entries: MainWindow exposes neither
///     the model nor the effort picker anymore, and the settings window is the
///     single surface.</summary>
public class ModelSettingsModelSectionTests
{
  private static ModelProviderEntry Entry(string modelId, string providerName = "ProviderA") => new(
      modelId, providerName, 0.000003m, null, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  private static Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> Loader(
      params ModelProviderEntry[] entries)
      => _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));

  private const string ProvidersOpenRouter = "openrouter";

  [Fact]
  public async Task ModelSection_LoadsCatalog_AndPreservesThePickerContract()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter,
        persist: _ => { },
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model"), Entry("zeta/model")),
            "zeta/model"));

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.False(vm.ModelSection!.IsLoading);
    Assert.Null(vm.ModelSection.LoadError);
    Assert.Equal(["alpha/model", "zeta/model"],
        vm.ModelSection.FilteredRows.Select(r => r.DisplayName));
    ModelPickerRow? selected = vm.ModelSection.SelectedRow;
    Assert.Equal("zeta/model", selected!.ModelId); // live choice pre-selected
  }

  [Fact]
  public async Task CatalogFailure_LandsInTheSectionErrorState()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter,
        persist: _ => { },
        modelSection: new ModelSectionSeams(_ => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("Catalog", "down")))));

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(vm.ModelSection!.LoadError);
  }

  [Fact]
  public async Task Save_WithUnchangedModel_CarriesUnchangedInTheSnapshot()
  {
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model")), "alpha/model"));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

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
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model"), Entry("beta/model")),
            "alpha/model"));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(r => r.ModelId == "beta/model");

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received!.Model);
    Assert.Equal(ModelChoiceKind.Pinned, received.Model.Kind);
    Assert.Equal("beta/model", received.Model.ModelId);
    Assert.Equal("beta/model", live.ModelId); // applied to the live preferences
  }

  [Fact]
  public async Task Save_WithAPinnedPair_CarriesBothHalves()
  {
    // Issue #296, D7/B6: the save carries the PAIR. A model without its provider is half a
    // decision, and the provider is what the picker's evidence surface exists to choose.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            Loader(Entry("alpha/model", "ProviderA"), Entry("alpha/model", "ProviderB")),
            "alpha/model"));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    // The model pre-selects from the session's live choice; the provider level then offers
    // both endpoints and the user switches to the one they want.
    Assert.Equal(2, vm.ModelSection!.ProviderRows.Count);
    vm.ModelSection.SelectedProviderRow = vm.ModelSection.ProviderRows
        .First(row => row.ProviderName == "ProviderB");

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received);
    Assert.Equal(ModelChoiceKind.Pinned, received.Model!.Kind);
    Assert.Equal("alpha/model", received.Model.ModelId);
    Assert.Equal("ProviderB", received.Model.ProviderName);
    Assert.Equal("ProviderB", live.ProviderName); // applied to the live preferences
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Refresh_PreservesSelection_WithoutInventingAUserEdit(bool changeSelection)
  {
    TaskCompletionSource<Result<IReadOnlyList<ModelProviderEntry>>> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ModelSettingsSnapshot? saved = null;
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, s => saved = s,
        modelSection: new ModelSectionSeams(
            Loader(Entry("alpha/model"), Entry("beta/model")), "alpha/model", ct =>
            {
              _ = ready.TrySetResult();
              return refresh.Task.WaitAsync(ct);
            }));
    Task load = vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    if (changeSelection)
    {
      vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(row => row.ModelId == "beta/model");
    }
    refresh.SetResult(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry("alpha/model"), Entry("beta/model"), Entry("gamma/model")]));
    await load.ConfigureAwait(true);
    Assert.Equal(changeSelection ? "beta/model" : "alpha/model", vm.ModelSection!.SelectedRow!.ModelId);
    Assert.True(vm.TrySave(out string? error), error);
    Assert.Equal(changeSelection ? ModelChoiceKind.Pinned : ModelChoiceKind.Unchanged, saved!.Model!.Kind);
  }
}
