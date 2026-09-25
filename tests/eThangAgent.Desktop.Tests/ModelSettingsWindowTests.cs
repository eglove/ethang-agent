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
  private static ModelProviderEntry Entry(string modelId) => new(
      modelId, "ProviderA", 0.000003m, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, IntelligenceScore: null,
      CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  [AvaloniaFact]
  public async Task Opening_TheWindow_KicksTheCatalogLoad()
  {
    ModelSettingsViewModel vm = new(
        new SessionModelPreferences(), "openrouter",
        persist: _ => { },
        loadCatalog: _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [Entry("alpha/model")])),
        allowAuto: true, currentModelId: null);
    Views.ModelSettingsWindow window = new(vm);

    window.Show();
    // The load is fire-and-forget from the Opened handler; pump the dispatcher
    // (bounded — no infinite wait) until it lands or the pump budget is spent.
    for (int i = 0; i < 50 && vm.ModelSection!.FilteredRows.Count <= 1; i++)
    {
      await Dispatcher.UIThread.InvokeAsync(() => { });
    }
    window.Close();

    Assert.Equal(2, vm.ModelSection!.FilteredRows.Count); // auto row + the loaded entry
  }
}
