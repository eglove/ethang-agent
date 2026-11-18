using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The tools page: every console program and script on the PATH, grouped by the
///     directory it lives in, with a name filter, a free-text add box and Save. The page reads
///     the PATH when it opens - never at app launch - because reading every PE header costs
///     several seconds. The view owns window mechanics; the scan, the selection and the save
///     live in the view-model.</summary>
internal partial class ToolsSettingsWindow : Window
{
  public ToolsSettingsWindow() => InitializeComponent();

  public ToolsSettingsWindow(ToolsSettingsViewModel vm) : this()
  {
    ArgumentNullException.ThrowIfNull(vm);
    DataContext = vm;
    Opened += (_, _) => _ = vm.LoadAsync();
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
