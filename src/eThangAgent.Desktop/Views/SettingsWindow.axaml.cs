using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: six categorized tabs - API Keys (one masked key
///     field), Skills (registry default target), Models (compaction model), Agents
///     (sub-agent and watchdog knobs), Advanced (provider base URL), Git (commit
///     style) - with a shared validation-error + Save/Cancel footer outside the
///     tabs. Session files and skill directories are NOT here: they are configured
///     in the Open Workspace dialog (the launch surface). Confirming closes the
///     dialog with the validated <see cref="SettingsUpdate"/>; cancelling closes it
///     with null. The view only owns window mechanics — validation and state live
///     in the view-model.</summary>
internal partial class SettingsWindow : Window
{
  private readonly SettingsViewModel? _vm;

  public SettingsWindow() => InitializeComponent();

  public SettingsWindow(string? openRouterKey, CommitStyle commitStyle,
      IReadOnlyList<CompactionModelOption>? compactionModels = null,
      CompactionModelOption? selectedCompactionModel = null,
      string? workspaceRoot = null,
      string? maxConcurrentAgentsText = null, string? defaultModelText = null, bool remoteHost = false,
      string? watchdogTickText = null, string? watchdogIdleText = null, string? watchdogWrapUpText = null,
      string? openRouterBaseUrlText = null, bool computerUse = false,
      string? skillRegistryDefaultTarget = null,
      string? openRouterManagementKey = null) : this()
  {
    _vm = new SettingsViewModel(openRouterKey, commitStyle,
        compactionModels, selectedCompactionModel, workspaceRoot,
        maxConcurrentAgentsText, defaultModelText, remoteHost,
        watchdogTickText, watchdogIdleText, watchdogWrapUpText,
        openRouterBaseUrlText, computerUse, skillRegistryDefaultTarget,
        openRouterManagementKey);
    DataContext = _vm;
    _vm.SaveRequested += (_, update) => Close(update);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
