using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The workspace-scope rows loaded once a workspace is picked: the
///     session-file list and the skill-directory list for that root.</summary>
internal delegate Task<(IReadOnlyList<SessionFileEntry> Files,
    IReadOnlyList<SessionFileEntry> Directories,
    IReadOnlyList<TrustedDirectoryEntry> TrustedDirectories)> WorkspaceRowsLoader(string workspaceRoot);

/// <summary>Reads a workspace's gate state for the dialog (issue #285). The dialog edits the
///     workspace it is ABOUT to open, so no session container exists to ask - the shell supplies
///     this over the app-owned database.</summary>
internal delegate Task<WorkspaceGateState> WorkspaceGateLoader(string workspaceRoot);

/// <summary>The dialog's prefill rows, grouped so the window constructor stays
/// parameter-light (S107: constructor parameter count) - the same shape the adjacent
/// settings modal uses through <c>SettingsValues</c>. Every member is optional and its
/// absence means an empty scope; the trusted-directory rows are workspace-scoped like the
/// workspace-scope file and skill lists.</summary>
internal sealed record LaunchRows
{
  public IReadOnlyList<SessionFileEntry>? GlobalFiles { get; init; }
  public IReadOnlyList<SessionFileEntry>? GlobalSkillDirectories { get; init; }
  public IReadOnlyList<SessionFileEntry>? WorkspaceFiles { get; init; }
  public IReadOnlyList<SessionFileEntry>? WorkspaceSkillDirectories { get; init; }
  public IReadOnlyList<TrustedDirectoryEntry>? TrustedDirectories { get; init; }
}

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
  private readonly WorkspaceGateLoader? _loadGate;

  public NewAgentWindow() => InitializeComponent();

  public NewAgentWindow(IReadOnlyList<ProviderOption> providers, string preferredProviderId,
      LaunchRows? rows = null, WorkspaceRowsLoader? loadWorkspaceRows = null,
      WorkspaceGateLoader? loadGate = null) : this()
  {
    LaunchRows seeded = rows ?? new LaunchRows();
    _vm = new NewAgentViewModel(providers, preferredProviderId,
        seeded.GlobalFiles, seeded.WorkspaceFiles,
        seeded.GlobalSkillDirectories, seeded.WorkspaceSkillDirectories,
        seeded.TrustedDirectories);
    _loadWorkspaceRows = loadWorkspaceRows;
    _loadGate = loadGate;
    DataContext = _vm;
    _vm.WorkspaceRequested += async (_, _) => await ChooseWorkspaceAsync();
    _vm.GlobalFilePickRequested += async (_, _) => await PickFileAsync("global");
    _vm.WorkspaceFilePickRequested += async (_, _) => await PickFileAsync("workspace");
    _vm.GlobalSkillDirectoryPickRequested += async (_, _) => await PickSkillDirectoryAsync("global");
    _vm.WorkspaceSkillDirectoryPickRequested += async (_, _) => await PickSkillDirectoryAsync("workspace");
    _vm.TrustedDirectoryPickRequested += async (_, _) => await PickTrustedDirectoryAsync();
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

    (IReadOnlyList<SessionFileEntry> files, IReadOnlyList<SessionFileEntry> directories,
        IReadOnlyList<TrustedDirectoryEntry> trusted) = await _loadWorkspaceRows(root);
    _vm.ReplaceWorkspaceRows(files, directories, trusted);

    // The gate section reflects THIS workspace (issue #285): a declared gate prefills, a
    // declined one shows nothing to answer, and an unasked one asks. Reading it here rather
    // than in the view-model keeps the view-model free of a database.
    if (_loadGate is not null)
    {
      _vm.ApplyGateState(await _loadGate(root));
    }
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

  /// <summary>Shows the native folder picker for a trusted directory (issue #96) and
  ///     feeds the picked root back in. A cancelled pick changes nothing.</summary>
  private async Task PickTrustedDirectoryAsync()
  {
    IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    {
      Title = "Choose an external directory the agent may reach from this workspace",
      AllowMultiple = false,
    });
    if (folders.Count > 0)
    {
      _vm?.SetPickedTrustedDirectory(folders[0].Path.LocalPath);
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
