using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>The rendered Model section: the numbered list, its three buttons, and the catalog list
///     that feeds it. The view-model's own state is not the claim here - these tests read the
///     CONTROLS, because the defects this section has had were binding round trips (an ItemsSource
///     swap clearing a selection, a button whose CanExecute never requeries) rather than mistakes in
///     the view-model.</summary>
[Trait("Category", "Process")]
public class ModelListSectionTests
{
  private const string Alpha = "alpha/model";
  private const string Beta = "beta/model";

  private static ModelProviderEntry Entry(string modelId, int context = 200_000) => new(
      modelId, "ProviderA", 0.000003m, null, 0.000015m, context, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  private static ModelSettingsViewModel Settings(params string[] currentModelIds)
      => new(
          new SessionModelPreferences(), "openrouter",
          persist: _ => { },
          modelSection: new ModelSectionSeams(
              _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
                  [Entry(Alpha), Entry(Beta)])),
              currentModelIds));

  /// <summary>Shows the window and pumps until the catalog load has landed, so every assertion
  ///     reads a settled render rather than a race.</summary>
  private static async Task<Views.ModelSettingsWindow> ShowAsync(ModelSettingsViewModel vm)
  {
    Views.ModelSettingsWindow window = new(vm);
    window.Show();
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (DateTimeOffset.UtcNow < deadline && vm.ModelSection!.IsLoading)
    {
      await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    return window;
  }

  [AvaloniaFact]
  public async Task TheNumberedList_RendersOneRowPerSelectionEntry()
  {
    ModelSettingsViewModel vm = Settings(Alpha, Beta);
    Views.ModelSettingsWindow window = await ShowAsync(vm).ConfigureAwait(true);
    ListBox list = window.FindControl<ListBox>("SelectedModelList")!;
    int rows = list.ItemCount;
    window.Close();

    Assert.Equal(2, rows);
    Assert.Equal(2, vm.ModelSection!.SelectedModels.Count);
  }

  [AvaloniaFact]
  public async Task TheAddButton_IsDisabledUntilTheCatalogHasASelection()
  {
    // The section opens with a saved model the catalog does not list, so the catalog list has
    // nothing selected - the state the add button has to be disabled in.
    ModelSettingsViewModel vm = Settings("gone/model");
    Views.ModelSettingsWindow window = await ShowAsync(vm).ConfigureAwait(true);
    Button add = window.FindControl<Button>("AddModelButton")!;
    bool beforeSelection = add.IsEffectivelyEnabled;
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(row => row.ModelId == Beta);
    await Dispatcher.UIThread.InvokeAsync(() => { });
    bool afterSelection = add.IsEffectivelyEnabled;
    window.Close();

    Assert.False(beforeSelection, "nothing is selected in the catalog, so there is nothing to add");
    Assert.True(afterSelection, "the add button must enable once a catalog row is selected");
  }

  [AvaloniaFact]
  public async Task EveryListCommand_IsReachableFromTheWindow()
  {
    // The four commands the section's buttons bind are declared on the view-model and MUST reach the
    // controls: a command the window cannot find is a button that does nothing, silently.
    ModelSettingsViewModel vm = Settings(Alpha, Beta);
    Views.ModelSettingsWindow window = await ShowAsync(vm).ConfigureAwait(true);
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(row => row.ModelId == Beta);
    vm.ModelSection.SelectedModel = vm.ModelSection.SelectedModels[1];
    await Dispatcher.UIThread.InvokeAsync(() => { });
    Button add = window.FindControl<Button>("AddModelButton")!;
    Button up = window.FindControl<Button>("MoveUpButton")!;
    Button down = window.FindControl<Button>("MoveDownButton")!;
    Button remove = window.FindControl<Button>("RemoveModelButton")!;
    window.Close();

    Assert.Same(vm.ModelSection.AddModelCommand, add.Command);
    Assert.Same(vm.ModelSection.MoveUpCommand, up.Command);
    Assert.Same(vm.ModelSection.MoveDownCommand, down.Command);
    Assert.Same(vm.ModelSection.RemoveModelCommand, remove.Command);
  }

  [AvaloniaFact]
  public async Task AddingACatalogModel_AppendsANumberedRowToTheRenderedList()
  {
    // The rendered list is where the user reads the order, so the row count and the numbering are
    // asserted on the control, not only on the view-model.
    ModelSettingsViewModel vm = Settings(Alpha);
    Views.ModelSettingsWindow window = await ShowAsync(vm).ConfigureAwait(true);
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(row => row.ModelId == Beta);
    await Dispatcher.UIThread.InvokeAsync(() => { });
    window.FindControl<Button>("AddModelButton")!.Command!.Execute(null);
    await Dispatcher.UIThread.InvokeAsync(() => { });
    ListBox list = window.FindControl<ListBox>("SelectedModelList")!;
    int rows = list.ItemCount;
    string[] rendered = [.. vm.ModelSection.SelectedModels.Select(row => $"{row.Position}:{row.ModelId}")];
    window.Close();

    Assert.Equal(2, rows);
    Assert.Equal([$"1:{Alpha}", $"2:{Beta}"], rendered);
  }

  [AvaloniaFact]
  public async Task RemovingEveryRowButOne_LeavesTheRemoveButtonRefusingAndTheReasonShown()
  {
    ModelSettingsViewModel vm = Settings(Alpha, Beta);
    Views.ModelSettingsWindow window = await ShowAsync(vm).ConfigureAwait(true);
    vm.ModelSection!.SelectedModel = vm.ModelSection.SelectedModels[1];
    await Dispatcher.UIThread.InvokeAsync(() => { });
    window.FindControl<Button>("RemoveModelButton")!.Command!.Execute(null);
    await Dispatcher.UIThread.InvokeAsync(() => { });
    window.FindControl<Button>("RemoveModelButton")!.Command!.Execute(null);
    await Dispatcher.UIThread.InvokeAsync(() => { });
    int rows = window.FindControl<ListBox>("SelectedModelList")!.ItemCount;
    string? reason = vm.ModelSection.SelectionError;
    window.Close();

    Assert.Equal(1, rows);
    Assert.Contains("At least one model", reason!, StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public async Task EveryControlTheSectionOffers_IsReachableInTheWindow()
  {
    // Nothing the section already offered may be hidden by the numbered list: the catalog search, the
    // catalog list, the numbered list, both dropdowns with their captions, and the load-error line all
    // stay, and the whole form is inside the window's scroll viewer so no fixed height can hide them.
    ModelSettingsViewModel vm = Settings(Alpha);
    Views.ModelSettingsWindow window = await ShowAsync(vm).ConfigureAwait(true);
    string[] required =
    [
      "SelectedModelList", "ModelSearchBox", "ModelList", "AddModelButton",
      "MoveUpButton", "MoveDownButton", "RemoveModelButton",
      "VariantBox", "VariantCaptionLine", "RetentionBox", "RetentionCaptionLine", "LoadErrorLine",
    ];
    List<string> missing = [.. required.Where(name => window.FindControl<Control>(name) is null)];
    bool scrollable = window.GetVisualDescendants().OfType<ScrollViewer>().Any();
    window.Close();

    Assert.Empty(missing);
    Assert.True(scrollable, "the form must stay inside a scroll viewer so a small window cannot hide it");
  }
}
