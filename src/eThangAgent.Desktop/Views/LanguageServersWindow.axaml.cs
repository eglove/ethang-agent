using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The language-servers page: one row per language this app can serve, the status
///     this machine is in for each, and the install / remove / update commands. The view only
///     owns window mechanics; status and guards live in the view-model.</summary>
internal partial class LanguageServersWindow : Window
{
  public LanguageServersWindow() => InitializeComponent();

  public LanguageServersWindow(LanguageServersViewModel vm) : this()
  {
    DataContext = vm;
    Opened += (_, _) => _ = vm.RefreshAsync();
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
