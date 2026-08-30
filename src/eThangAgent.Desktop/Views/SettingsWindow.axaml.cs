using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: three categorized tabs - API Keys (one masked field
///     per provider), Models (z.ai endpoint, compaction model), Git (commit style) -
///     with a shared validation-error + Save/Cancel footer outside the tabs.
///     Confirming closes the dialog with the validated <see cref="SettingsUpdate"/>;
///     cancelling closes it with null. The view only owns window mechanics —
///     validation and state live in the view-model.</summary>
internal partial class SettingsWindow : Window
{
  private readonly SettingsViewModel? _vm;

  public SettingsWindow() => InitializeComponent();

  public SettingsWindow(string? openRouterKey, string? zaiKey,
      ZaiEndpointMode zaiEndpointMode, CommitStyle commitStyle,
      IReadOnlyList<CompactionModelOption>? compactionModels = null,
      CompactionModelOption? selectedCompactionModel = null) : this()
  {
    _vm = new SettingsViewModel(openRouterKey, zaiKey, zaiEndpointMode, commitStyle,
        compactionModels, selectedCompactionModel);
    DataContext = _vm;
    _vm.SaveRequested += (_, update) => Close(update);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
