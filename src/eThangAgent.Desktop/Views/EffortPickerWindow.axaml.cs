using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.Views;

/// <summary>The effort picker modal: the model-default row plus the seven reasoning
///     levels — the same list on both providers. Confirming closes the dialog with the
///     chosen <see cref="EffortChoice"/>; cancelling closes it with null. The view only
///     owns window mechanics — rows and state live in the view-model.</summary>
internal partial class EffortPickerWindow : Window
{
  private readonly EffortPickerViewModel? _vm;

  public EffortPickerWindow() => InitializeComponent();

  public EffortPickerWindow(ReasoningEffort? currentEffort) : this()
  {
    _vm = new EffortPickerViewModel(currentEffort);
    DataContext = _vm;
    _vm.ConfirmRequested += (_, choice) => Close(choice);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

  private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
      // Same guard as the Select button: only a selected row confirms.
      => _vm?.ConfirmCommand.Execute(null);
}
