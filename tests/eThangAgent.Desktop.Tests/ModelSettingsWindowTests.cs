using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Model Settings window owns the catalog-load kick: opening it starts
///     the embedded model section's load. This is the regression test for the
///     picker fold-in (the standalone ModelPickerWindow's Opened trigger was
///     dropped, leaving the catalog never fetched and search matching nothing
///     but the auto row).</summary>
public class ModelSettingsWindowTests
{
  private static ModelProviderEntry Entry(string modelId, string providerName = "ProviderA") => new(
      modelId, providerName, 0.000003m, null, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  [AvaloniaFact]
  public async Task Opening_TheWindow_KicksTheCatalogLoad()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), "openrouter",
        persist: _ => { },
        modelSection: new ModelSectionSeams(_ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [Entry("alpha/model")]))));
    Views.ModelSettingsWindow window = new(vm);

    window.Show();
    // The load is fire-and-forget from the Opened handler; pump the dispatcher
    // (bounded — no infinite wait) until it lands or the budget is spent. The budget
    // is a WALL-CLOCK bound, not a pump count: under load a pump takes longer and the
    // continuation lands later, so a fixed count expired before the load did and the
    // test failed on a busy machine (2026-10-22).
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
    for (int i = 0; i < 5_000 && vm.ModelSection!.FilteredRows.Count <= 1; i++)
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        break;
      }

      await Dispatcher.UIThread.InvokeAsync(() => { });
    }
    window.Close();

    _ = Assert.Single(vm.ModelSection!.FilteredRows); // the loaded entry
  }

  /// <summary>The window opens ON the choice the session is serving, and a re-render must not drop it.</summary>
  [AvaloniaFact]
  public async Task TheWindow_OpensOnTheServedChoice_EvenAfterTheListRerenders()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), "openrouter",
        persist: _ => { },
        modelSection: new ModelSectionSeams(
            _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [
              Entry("alpha/model"),
              Entry("zeta/model", "ProviderA"),
              Entry("zeta/model", "ProviderB"),
            ])),
            CurrentModelId: "zeta/model",
            CurrentVariant: ModelVariant.Nitro));
    Views.ModelSettingsWindow window = new(vm);

    window.Show();
    // Pump until the WHOLE load has landed: the catalog, then the rebuild that re-renders the
    // list. That last re-render is the one that used to drop the selection, so stopping at the
    // first non-null selection would miss the defect.
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
    while (DateTimeOffset.UtcNow < deadline && vm.ModelSection!.IsLoading)
    {
      await Dispatcher.UIThread.InvokeAsync(() => { });
    }
    window.Close();

    Assert.Equal("zeta/model", vm.ModelSection!.SelectedRow?.ModelId);
    // The variant dropdown shows the tradeoff the session committed.
    Assert.Equal(ModelVariant.Nitro, vm.ModelSection.SelectedVariantOption.Variant);
    // ...and the list the user is looking at actually highlights it. The view-model state
    // alone is not the claim: the defect was a binding round trip (ItemsSource swap -> the
    // control clears its selection -> writes null back), so the control is where it has to
    // be pinned.
    ListBox list = window.FindControl<ListBox>("ModelList")!;
    Assert.Same(vm.ModelSection.SelectedRow, list.SelectedItem);
  }
}
