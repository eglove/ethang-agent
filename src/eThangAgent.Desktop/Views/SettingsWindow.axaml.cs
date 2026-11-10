using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: six categorized tabs - API Keys (the sign-in state,
///     the key field while nothing is configured, and the masked management key),
///     Skills (registry default target), Models (compaction model), Agents
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
  private readonly Func<LanguageServersViewModel>? _languageServers;

  public SettingsWindow() => InitializeComponent();

  public SettingsWindow(SettingsValues values) : this(values, null)
  {
  }

  public SettingsWindow(SettingsValues values, Func<LanguageServersViewModel>? languageServers) : this()
  {
    _languageServers = languageServers;
    _vm = new SettingsViewModel(values);
    DataContext = _vm;
    _vm.SaveRequested += (_, update) => Close(update);
    _vm.LanguageServersRequested += (_, _) => _ = ShowLanguageServersAsync();
  }

  /// <summary>Opens the language-servers page over this window. A host that wired no
  ///     language-server seams shows nothing rather than an empty page.</summary>
  private async Task ShowLanguageServersAsync()
  {
    if (_languageServers?.Invoke() is not { } vm)
    {
      return;
    }

    LanguageServersWindow dialog = new(vm);
    await dialog.ShowDialog(this);
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
