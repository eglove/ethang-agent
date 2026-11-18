using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The new-agent modal: (A) a provider dropdown of the configured providers and (B) a
///     "Choose Workspace" button that opens the platform folder picker. Confirming closes the
///     dialog with the chosen pair; cancelling closes it with null.
///     <para>NO FILE LISTS ARE HERE. The session files, skill directories and trusted
///     directories a workspace uses are edited in the workspace settings window, and the ones
///     that apply to every workspace in the app Settings window, so this dialog persists
///     nothing and carries nothing to persist.</para>
///     <para>The view owns the folder picker because it is a UI-affine platform concern; the
///     selection and the Open guard belong to the view-model.</para></summary>
internal partial class NewAgentWindow : Window
{
  private readonly NewAgentViewModel? _vm;

  public NewAgentWindow() => InitializeComponent();

  public NewAgentWindow(IReadOnlyList<ProviderOption> providers, string preferredProviderId) : this()
  {
    _vm = new NewAgentViewModel(providers, preferredProviderId);
    DataContext = _vm;
    _vm.WorkspaceRequested += async (_, _) => await ChooseWorkspaceAsync();
    _vm.OpenRequested += (_, choice) => Close(choice);
  }

  /// <summary>Shows the native folder picker and feeds the choice back into the view-model. A
  ///     cancelled pick keeps the previous value.</summary>
  private async Task ChooseWorkspaceAsync()
  {
    IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    {
      Title = "Choose the directory this agent will work from",
      AllowMultiple = false,
    });
    if (folders.Count > 0)
    {
      _vm?.SetWorkspaceRoot(folders[0].Path.LocalPath);
    }
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
