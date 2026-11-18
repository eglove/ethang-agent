using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: eight categorized tabs - API Keys (the sign-in state,
///     the key field while nothing is configured, and the masked management key),
///     Skills (registry default target), Language servers (this machine's install state,
///     with install / update / remove), Tools (which command-line tools agents are told
///     about), Models (compaction model), Agents (sub-agent and watchdog knobs), Advanced
///     (provider base URL), Git (commit style) - with a shared validation-error + Save/Cancel
///     footer outside the tabs. Session files and skill directories are NOT here: they are
///     configured in the Open Workspace dialog (the launch surface).
///     <para>EVERY PAGE IS A TAB, and the footer's one Save is the only write: a page that
///     persisted itself from its own button would leave a user who cancelled the dialog with
///     a change they never confirmed.</para>
///     <para>Confirming closes the dialog with the validated <see cref="SettingsUpdate"/>;
///     cancelling closes it with null. The view only owns window mechanics - validation,
///     machine reads and state live in the view-model.</para></summary>
internal partial class SettingsWindow : Window
{
  private readonly SettingsViewModel? _vm;

  public SettingsWindow() => InitializeComponent();

  public SettingsWindow(SettingsValues values) : this()
  {
    ArgumentNullException.ThrowIfNull(values);
    _vm = new SettingsViewModel(values);
    DataContext = _vm;
    _vm.SaveRequested += (_, update) => Close(update);
    Opened += (_, _) => _ = _vm.LoadPagesAsync();
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
