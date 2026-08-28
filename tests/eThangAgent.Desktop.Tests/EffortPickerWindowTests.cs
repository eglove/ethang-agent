using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless tests of the REAL effort picker window: the binding write-back
///     from the ListBox to the view-model, and the click-a-row-then-press-Select flow
///     — the same view-layer surface where the model picker's confirm-desync lived.</summary>
public class EffortPickerWindowTests
{
  private static (EffortPickerWindow Window, EffortPickerViewModel Vm, ListBox List) ShowPicker(
      ReasoningEffort? current)
  {
    EffortPickerWindow window = new(current);
    window.Show();
    EffortPickerViewModel vm = (EffortPickerViewModel)window.DataContext!;
    ListBox list = window.GetControl<ListBox>("EffortList");
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(8, list.ItemCount); // model default + the seven levels
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
  public void Preselected_Current_Level_Confirms_As_Is()
  {
    EffortChoice? received = null;
    (EffortPickerWindow window, EffortPickerViewModel vm, ListBox _) = ShowPicker(ReasoningEffort.High);
    vm.ConfirmRequested += (_, choice) => received = choice;

    Assert.Equal(ReasoningEffort.High, vm.SelectedRow!.Level);

    Button select = window.GetControl<Button>("SelectButton");
    ClickOn(window, select);

    Assert.Equal(ReasoningEffort.High, received!.Level);
  }

  [AvaloniaFact]
  public void Clicking_A_Row_Then_Select_Confirms_That_Row_Not_The_PreSelected_Current()
  {
    EffortChoice? received = null;
    (EffortPickerWindow window, EffortPickerViewModel vm, ListBox list) = ShowPicker(ReasoningEffort.High);
    vm.ConfirmRequested += (_, choice) => received = choice;
    Assert.Equal(ReasoningEffort.High, vm.SelectedRow!.Level); // pre-selected current, as in production

    Control row = list.ContainerFromIndex(6)!; // Minimal
    EffortPickerRow expected = vm.Rows[6];
    ClickOn(window, row);

    Assert.Equal(expected, vm.SelectedRow);

    Button select = window.GetControl<Button>("SelectButton");
    ClickOn(window, select);

    Assert.Equal(ReasoningEffort.Minimal, received!.Level);
  }
}
