using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless tests of the REAL picker window: the binding write-back from the
///     ListBox to the view-model, and the full click-a-row-then-press-Select flow.
///     These exist because a view-layer desync (list shows a row selected while the
///     view-model still holds the pre-selected auto row) is invisible to pure
///     view-model tests — the confirm then silently emits the WRONG model.</summary>
public class ModelPickerWindowTests
{
  private static readonly ModelProviderEntry[] Entries =
  [
      new("deepseek/deepseek-v4-flash", "Deepseek", 0.000001m, 0.000004m, 160_000, 8192,
          SupportsToolUse: true, SupportsVision: false, IntelligenceScore: null, CodingScore: null,
          AgenticScore: null, LatencyMs: null, ThroughputTokensPerSec: null, Description: null),
  ];

  private static (ModelPickerWindow Window, ModelPickerViewModel Vm, ListBox List) ShowPicker()
  {
    ModelPickerWindow window = new(
        _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(Entries)),
        allowAuto: true, currentModelId: null);
    window.Show();
    ModelPickerViewModel vm = (ModelPickerViewModel)window.DataContext!;
    ListBox list = window.GetControl<ListBox>("ModelList");
    // Pump the dispatcher until the Opened-triggered catalog load (instant fake) lands.
    Dispatcher.UIThread.RunJobs();
    Assert.False(vm.IsLoading);
    Assert.Equal(2, list.ItemCount); // auto row + the one model
    return (window, vm, list);
  }

  private static Point CenterInWindow(Control control, Window window)
      => control.TranslatePoint(
          new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
          ?? throw new InvalidOperationException("control not laid out inside the window");

  private static void ClickOn(Window window, Control control)
  {
    Point point = CenterInWindow(control, window);
    window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
    window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
  }

  [AvaloniaFact]
  public void Selecting_Row_On_The_List_Writes_Back_To_The_View_Model()
  {
    (ModelPickerWindow window, ModelPickerViewModel vm, ListBox list) = ShowPicker();
    ModelPickerRow target = vm.FilteredRows.First(r => r.ModelId == "deepseek/deepseek-v4-flash");

    list.SelectedItem = target; // what a row click must ultimately produce

    Assert.Equal("deepseek/deepseek-v4-flash", vm.SelectedRow!.ModelId);
    _ = window;
  }

  [AvaloniaFact]
  public void Clicking_A_Row_Then_Select_Confirms_That_Row_Not_The_PreSelected_Auto()
  {
    ModelChoice? received = null;
    (ModelPickerWindow window, ModelPickerViewModel vm, ListBox list) = ShowPicker();
    vm.ConfirmRequested += (_, choice) => received = choice;
    Assert.Null(vm.SelectedRow!.ModelId); // pre-selected auto row, as in production

    Control row = list.ContainerFromIndex(1)!;
    ClickOn(window, row);

    Assert.Equal("deepseek/deepseek-v4-flash", vm.SelectedRow!.ModelId);

    Button select = window.GetControl<Button>("SelectButton");
    ClickOn(window, select);

    Assert.NotNull(received);
    Assert.Equal("deepseek/deepseek-v4-flash", received!.ModelId);
  }
}
