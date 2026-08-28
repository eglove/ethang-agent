using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: the API Keys section with one masked field per
///     provider. Confirming closes the dialog with the validated
///     <see cref="SettingsUpdate"/>; cancelling closes it with null. The view only
///     owns window mechanics — validation and state live in the view-model.</summary>
internal partial class SettingsWindow : Window
{
  private readonly SettingsViewModel? _vm;

  public SettingsWindow() => InitializeComponent();

  public SettingsWindow(string? openRouterKey, string? zaiKey) : this()
  {
    _vm = new SettingsViewModel(openRouterKey, zaiKey);
    DataContext = _vm;
    _vm.SaveRequested += (_, update) => Close(update);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
