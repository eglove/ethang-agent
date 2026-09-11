using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The Model Settings modal: reasoning effort, the twelve sampling knobs,
///     and — on OpenRouter sessions — the routing/server-tool/plugin section.
///     Confirming closes the dialog with the saved
///     <see cref="ModelSettingsSnapshot"/>; cancelling closes it with null and
///     writes nothing. The view only owns window mechanics — fields, validation,
///     and persistence calls live in the view-model and the shell.</summary>
internal partial class ModelSettingsWindow : Window
{
  public ModelSettingsWindow() => InitializeComponent();

  public ModelSettingsWindow(ModelSettingsViewModel vm) : this()
  {
    DataContext = vm;
    vm.SettingsSaved += (_, snapshot) => Close(snapshot);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
