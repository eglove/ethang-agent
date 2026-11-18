using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The settings modal: eight categorized tabs - API Keys (the sign-in state,
///     the key field while nothing is configured, and the masked management key),
///     Skills (registry default target plus the two lists that apply to EVERY workspace:
///     session files and skill directories), Language servers (this machine's install state,
///     with install / update / remove), Tools (which command-line tools agents are told
///     about), Models (compaction model), Agents (sub-agent and watchdog knobs), Advanced
///     (provider base URL), Git (commit style) - with a shared validation-error + Save/Cancel
///     footer outside the tabs.
///     <para>NO WORKSPACE-SCOPED LIST IS HERE. This dialog is reachable with no workspace open,
///     where a list describing one workspace would have nothing to describe; the
///     workspace-scoped lists and the trusted directories live in
///     <see cref="WorkspaceSettingsWindow"/>.</para>
///     <para>EVERY PAGE IS A TAB, and the footer's one Save is the only write: a page that
///     persisted itself from its own button would leave a user who cancelled the dialog with
///     a change they never confirmed.</para>
///     <para>Confirming closes the dialog with the validated <see cref="SettingsUpdate"/>;
///     cancelling closes it with null. The view only owns window mechanics - validation,
///     machine reads, the file pickers and state live in the view-model.</para></summary>
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
    _vm.GlobalFilePickRequested += async (_, _) => await PickGlobalFileAsync();
    _vm.GlobalSkillDirectoryPickRequested += async (_, _) => await PickGlobalSkillDirectoryAsync();
    Opened += (_, _) => _ = _vm.LoadPagesAsync();
  }

  /// <summary>Shows the native open-file picker for a global session file and feeds the
  ///     picked path back in. A cancelled pick changes nothing.</summary>
  private async Task PickGlobalFileAsync()
  {
    IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
      Title = "Choose a session file loaded for every workspace",
      AllowMultiple = false,
    });
    if (files.Count > 0)
    {
      _vm?.SetPickedGlobalFile(files[0].Path.LocalPath);
    }
  }

  /// <summary>Shows the native folder picker for a global skill directory and feeds the
  ///     picked path back in. A cancelled pick changes nothing.</summary>
  private async Task PickGlobalSkillDirectoryAsync()
  {
    IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    {
      Title = "Choose a skill directory scanned for every workspace",
      AllowMultiple = false,
    });
    if (folders.Count > 0)
    {
      _vm?.SetPickedGlobalSkillDirectory(folders[0].Path.LocalPath);
    }
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
