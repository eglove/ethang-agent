using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: five categorized tabs - API Keys (one masked field
///     per provider plus the local base URL), Files, Models (z.ai endpoint, compaction
///     model), Agents (sub-agent and watchdog knobs), Advanced (provider base URLs),
///     Git (commit style) -
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
      CompactionModelOption? selectedCompactionModel = null,
      string? localBaseUrl = null, string? localApiKey = null,
      IReadOnlyList<SessionFileEntry>? globalFiles = null,
      IReadOnlyList<SessionFileEntry>? workspaceFiles = null,
      string? workspaceRoot = null,
      string? maxConcurrentAgentsText = null, string? defaultModelText = null, bool remoteHost = false,
      string? watchdogTickText = null, string? watchdogIdleText = null, string? watchdogWrapUpText = null,
      string? openRouterBaseUrlText = null, string? zaiBaseUrlText = null) : this()
  {
    _vm = new SettingsViewModel(openRouterKey, zaiKey, zaiEndpointMode, commitStyle,
        compactionModels, selectedCompactionModel, localBaseUrl, localApiKey,
        globalFiles, workspaceFiles, workspaceRoot,
        maxConcurrentAgentsText, defaultModelText, remoteHost,
        watchdogTickText, watchdogIdleText, watchdogWrapUpText,
        openRouterBaseUrlText, zaiBaseUrlText);
    DataContext = _vm;
    _vm.SaveRequested += (_, update) => Close(update);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
