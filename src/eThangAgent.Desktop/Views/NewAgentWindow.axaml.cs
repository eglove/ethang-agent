using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The workspace-scope rows loaded once a workspace is picked: the
///     session-file list and the skill-directory list for that root.</summary>
internal delegate Task<(IReadOnlyList<SessionFileEntry> Files,
    IReadOnlyList<SessionFileEntry> Directories)> WorkspaceRowsLoader(string workspaceRoot);

/// <summary>The new-agent modal: (A) a provider dropdown of the configured providers,
///     (B) a "Choose Workspace" button that opens the platform folder picker, and
///     (C) the session-file / skill-directory lists configured at launch - rows added
///     through the native file and folder pickers (no free-text path entry). Global
///     rows prefill from the stored preferences; picking a workspace loads THAT
///     workspace's stored rows through <see cref="WorkspaceRowsLoader"/>. Confirming
///     closes the dialog with the chosen pair plus both lists; cancelling closes it
///     with null. The view owns the pickers because they are UI-affine platform
///     concerns; the view-model owns the rows.</summary>
internal partial class NewAgentWindow : Window
{
  private readonly NewAgentViewModel? _vm;
  private readonly WorkspaceRowsLoader? _loadWorkspaceRows;

  public NewAgentWindow() => InitializeComponent();

  public NewAgentWindow(IReadOnlyList<ProviderOption> providers, string preferredProviderId,
      IReadOnlyList<SessionFileEntry>? globalFiles = null,
      IReadOnlyList<SessionFileEntry>? globalSkillDirectories = null,
      WorkspaceRowsLoader? loadWorkspaceRows = null,
      IReadOnlyList<SessionFileEntry>? workspaceFiles = null,
      IReadOnlyList<SessionFileEntry>? workspaceSkillDirectories = null) : this()
  {
    _vm = new NewAgentViewModel(providers, preferredProviderId,
        globalFiles, workspaceFiles, globalSkillDirectories, workspaceSkillDirectories);
    _loadWorkspaceRows = loadWorkspaceRows;
    DataContext = _vm;
    _vm.WorkspaceRequested += async (_, _) => await ChooseWorkspaceAsync();
    _vm.GlobalFilePickRequested += async (_, _) => await PickFileAsync("global");
    _vm.WorkspaceFilePickRequested += async (_, _) => await PickFileAsync("workspace");
    _vm.GlobalSkillDirectoryPickRequested += async (_, _) => await PickSkillDirectoryAsync("global");
    _vm.WorkspaceSkillDirectoryPickRequested += async (_, _) => await PickSkillDirectoryAsync("workspace");
    _vm.OpenRequested += (_, choice) => Close(choice);
  }

  /// <summary>Shows the native folder picker and feeds the choice back into the
  ///     view-model. A cancelled pick keeps the previous value. A CONFIRMED pick
  ///     also loads that workspace's stored rows into the workspace scope.</summary>
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
      await LoadWorkspaceRowsAsync();
    }
  }

  /// <summary>Loads the picked workspace's stored rows into the workspace scope
  ///     (replacing whatever rows are there — the scope reflects the stored
  ///     preference for THIS root). A failed load leaves the rows empty; the
  ///     user can still add rows through the pickers.</summary>
  private async Task LoadWorkspaceRowsAsync()
  {
    if (_vm?.WorkspaceRoot is not { } root || _loadWorkspaceRows is null)
    {
      return;
    }

    (IReadOnlyList<SessionFileEntry> files, IReadOnlyList<SessionFileEntry> directories) =
        await _loadWorkspaceRows(root);
    _vm.ReplaceWorkspaceRows(files, directories);
  }

  /// <summary>Shows the native open-file picker and feeds the picked path back in.
  ///     A cancelled pick changes nothing.</summary>
  private async Task PickFileAsync(string scope)
  {
    IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
      Title = scope == "workspace"
          ? "Choose a session file for this workspace"
          : "Choose a global session file (loaded for every workspace)",
      AllowMultiple = false,
    });
    if (files.Count > 0)
    {
      string path = files[0].Path.LocalPath;
      if (scope == "workspace")
      {
        _vm?.SetPickedWorkspaceFile(path);
      }
      else
      {
        _vm?.SetPickedGlobalFile(path);
      }
    }
  }

  /// <summary>Shows the native folder picker and feeds the picked directory back in.
  ///     A cancelled pick changes nothing.</summary>
  private async Task PickSkillDirectoryAsync(string scope)
  {
    IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    {
      Title = scope == "workspace"
          ? "Choose a skill directory for this workspace"
          : "Choose a global skill directory (scanned for every workspace)",
      AllowMultiple = false,
    });
    if (folders.Count > 0)
    {
      string path = folders[0].Path.LocalPath;
      if (scope == "workspace")
      {
        _vm?.SetPickedWorkspaceSkillDirectory(path);
      }
      else
      {
        _vm?.SetPickedGlobalSkillDirectory(path);
      }
    }
  }

  /// <summary>Test seam: runs the same feedback methods the picker handlers call, so
  ///     headless tests exercise the view's wiring without the native picker.</summary>
  internal void FeedPickedFileForTest(string scope, string path)
  {
    if (scope == "workspace")
    {
      _vm?.SetPickedWorkspaceFile(path);
    }
    else
    {
      _vm?.SetPickedGlobalFile(path);
    }
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
