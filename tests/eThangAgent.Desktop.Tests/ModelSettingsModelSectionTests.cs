using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

using eThangAgent.TestSupport;

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

  private static Func<string, CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> Loader(
      params ModelProviderEntry[] entries)
      => (_, _) => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));

  /// <summary>The ordered list a test seeds: every entry names no vendor, so the section's own
  ///     offered vendor serves them.</summary>
  private static IReadOnlyList<ModelSelectionEntry> Entries(params string[] modelIds)
      => [.. modelIds.Select(id => new ModelSelectionEntry(id))];

  private const string ProvidersOpenRouter = "openrouter";

  [Fact]
  public async Task ModelSection_LoadsCatalog_AndPreservesThePickerContract()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter,
        persist: _ => { },
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model"), Entry("zeta/model")),
            Entries("zeta/model")));

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.False(vm.ModelSection!.IsLoading);
    Assert.Null(vm.ModelSection.LoadError);
    Assert.Equal(["alpha/model", "zeta/model"],
        vm.ModelSection.FilteredRows.Select(r => r.ModelId));
    ModelPickerRow? selected = vm.ModelSection.SelectedRow;
    Assert.Equal("zeta/model", selected!.ModelId); // live choice pre-selected
  }

  [Fact]
  public async Task CatalogFailure_LandsInTheSectionErrorState()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter,
        persist: _ => { },
        modelSection: new ModelSectionSeams((_, _) => Task.FromResult(
            Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError("Catalog", "down")))));

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
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model")), Entries("alpha/model")));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received!.Model);
    Assert.Equal(ModelChoiceKind.Unchanged, received.Model.Kind);
    // Unchanged carries no value; the shell's existing model persistence is untouched.
    Assert.Empty(received.Model.Entries);
  }

  [Fact]
  public async Task Save_WithPinnedModel_AppliesAndCarriesTheId()
  {
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model"), Entry("beta/model")),
            Entries("alpha/model")));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    // The save is over the ORDERED LIST, so the edit is an addition to it - selecting a catalog row
    // is how a model is offered, not how the decision is stated.
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(r => r.ModelId == "beta/model");
    vm.ModelSection.AddModelCommand.Execute(null);

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received!.Model);
    Assert.Equal(ModelChoiceKind.Pinned, received.Model.Kind);
    Assert.Equal(["alpha/model", "beta/model"], received.Model.Entries.Select(e => e.ModelId));
    Assert.Equal(["alpha/model", "beta/model"], live.ModelIds); // applied to the live preferences
  }

  [Fact]
  public async Task Save_WithAPinnedChoice_CarriesBothHalves()
  {
    // THE CHOICE IS THE MODEL PLUS THE ROUTING VARIANT. A model without its variant is half a
    // decision - the model name says nothing about whether the turn should route for speed or
    // for price - so the save must carry both, and both must land on the live preferences.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            Loader(Entry("alpha/model", "ProviderA"), Entry("alpha/model", "ProviderB")),
            Entries("alpha/model")));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    // The model pre-selects from the session's live choice; the user then states the tradeoff.
    vm.ModelSection!.SelectedVariantOption = vm.ModelSection.VariantOptions
        .First(option => option.Variant == ModelVariant.Floor);

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.NotNull(received);
    Assert.Equal(ModelChoiceKind.Pinned, received.Model!.Kind);
    Assert.Equal(["alpha/model"], received.Model.Entries.Select(e => e.ModelId));
    Assert.Equal(ModelVariant.Floor, received.Model.Variant);
    Assert.Equal(ModelVariant.Floor, live.ModelVariant); // applied to the live preferences
  }

  [Fact]
  public async Task Save_ChangingOnlyTheVariant_IsARealChoice()
  {
    // Tri-state is on the PAIR: a user who keeps the model and switches the variant has made a
    // real choice, and reporting it as Unchanged would silently discard it - the same class of
    // defect as a status line naming a routing the request never carried.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            Loader(Entry("alpha/model")),
            CurrentEntries: Entries("alpha/model"), CurrentVariant: ModelVariant.Standard));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    vm.ModelSection!.SelectedVariantOption = vm.ModelSection.VariantOptions
        .First(option => option.Variant == ModelVariant.Nitro);
    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(ModelChoiceKind.Pinned, received!.Model!.Kind);
    Assert.Equal(["alpha/model"], received.Model.Entries.Select(e => e.ModelId));
    Assert.Equal(ModelVariant.Nitro, received.Model.Variant);
    Assert.Equal(ModelVariant.Nitro, live.ModelVariant);
  }

  [Fact]
  public async Task Save_OnAnUntouchedVariant_StaysUnchanged()
  {
    // Opening the window and pressing Save must not invent an edit: the same model AND the same
    // variant means Unchanged, so a session that never committed a variant keeps its plain
    // routing instead of having Standard written over it.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            Loader(Entry("alpha/model")),
            CurrentEntries: Entries("alpha/model"), CurrentVariant: ModelVariant.Nitro));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(ModelChoiceKind.Unchanged, received!.Model!.Kind);
    Assert.Null(live.ModelVariant); // untouched means untouched
  }

  [Fact]
  public async Task Save_WithAnUntouchedChoice_StaysUnchanged_WhenTheListRerenders()
  {
    // The rows are REBUILT whenever the rates are re-resolved - the catalog landing, then a
    // refresh - and the ORDERED IDS survive both, which is what makes an untouched window an
    // unchanged one.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            Loader(Entry("alpha/model"), Entry("beta/model")), CurrentEntries: Entries("alpha/model")));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.TrySave(out string? error), error);
    Assert.Equal(ModelChoiceKind.Unchanged, received!.Model!.Kind);
  }


  // ── the vendor is part of the choice the save carries ─────────────────────

  [Fact]
  public async Task Save_CarriesEachEntriesVendor_AndLandsItOnTheLivePreferences()
  {
    // A model picked from a vendor's catalog must still name that vendor after the save, or the
    // walk would resolve it through whichever provider the session happens to be bound to.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            (vendor, _) => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
                vendor == ModelVendors.Cloudflare
                    ? [Entry("meta/llama-3.1-8b-instruct", ModelVendors.Cloudflare)]
                    : [Entry("alpha/model")])),
            [new ModelSelectionEntry("alpha/model", ModelVendors.OpenRouter)],
            Vendors: [ModelVendors.OpenRouter, ModelVendors.Cloudflare]));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.SearchText = "llama";
    vm.ModelSection.SelectedRow = vm.ModelSection.FilteredRows.First(
        row => row.Vendor == ModelVendors.Cloudflare);
    vm.ModelSection.AddModelCommand.Execute(null);

    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal([ModelVendors.OpenRouter, ModelVendors.Cloudflare],
        received!.Model!.Entries.Select(entry => entry.Vendor));
    Assert.Equal([ModelVendors.OpenRouter, ModelVendors.Cloudflare],
        live.Entries!.Select(entry => entry.Vendor));
  }

  [Fact]
  public async Task ReorderingBetweenTwoVendorsRowsOfTheSameModel_IsARealEdit()
  {
    // The same model under two vendors is two entries; swapping them changes which vendor serves
    // the turn, so a comparison over bare model ids would report 'unchanged' and drop the edit.
    const string Shared = "meta/llama-3.1-8b-instruct";
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = new(
        live, ProvidersOpenRouter, persist: s => received = s,
        modelSection: new ModelSectionSeams(
            (vendor, _) => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
                [Entry(Shared, vendor)])),
            [
              new ModelSelectionEntry(Shared, ModelVendors.OpenRouter),
              new ModelSelectionEntry(Shared, ModelVendors.Cloudflare),
            ],
            Vendors: [ModelVendors.OpenRouter, ModelVendors.Cloudflare]));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.SelectedModel = vm.ModelSection.SelectedModels[1];
    vm.ModelSection.MoveUpCommand.Execute(null);

    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(ModelChoiceKind.Pinned, received!.Model!.Kind);
    Assert.Equal([ModelVendors.Cloudflare, ModelVendors.OpenRouter],
        live.Entries!.Select(entry => entry.Vendor));
  }

  [Fact]
  public async Task ACloudflareCatalogFailure_IsStated_AndTheOpenRouterRowsStay()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { },
        modelSection: new ModelSectionSeams(
            (vendor, _) => Task.FromResult(vendor == ModelVendors.Cloudflare
                ? Result.Failure<IReadOnlyList<ModelProviderEntry>>(
                    new DomainError("CatalogUnavailable", "Cloudflare refused the read"))
                : Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry("alpha/model")])),
            Vendors: [ModelVendors.OpenRouter, ModelVendors.Cloudflare]));
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["alpha/model"], vm.ModelSection!.FilteredRows.Select(row => row.ModelId));
    Assert.Contains("Cloudflare", vm.ModelSection.LoadError!, StringComparison.Ordinal);
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
            Loader(Entry("alpha/model"), Entry("beta/model")), Entries("alpha/model"),
            (_, ct) =>
            {
              ready.SetResult();
              return refresh.Task.WaitAsync(ct);
            }));
    Task load = vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    await ready.Task.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    if (changeSelection)
    {
      vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(row => row.ModelId == "beta/model");
      vm.ModelSection.AddModelCommand.Execute(null);
    }

    refresh.SetResult(Result.Success<IReadOnlyList<ModelProviderEntry>>([Entry("alpha/model"), Entry("beta/model"), Entry("gamma/model")]));
    await load.ConfigureAwait(true);
    Assert.Equal(changeSelection ? ["alpha/model", "beta/model"] : ["alpha/model"],
        vm.ModelSection!.SelectedModels.Select(row => row.ModelId));
    Assert.True(vm.TrySave(out string? error), error);
    Assert.Equal(changeSelection ? ModelChoiceKind.Pinned : ModelChoiceKind.Unchanged, saved!.Model!.Kind);
  }
}
