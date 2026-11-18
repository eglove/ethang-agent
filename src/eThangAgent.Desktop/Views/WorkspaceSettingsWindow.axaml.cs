using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The workspace settings window: the three lists that belong to ONE workspace - the
///     session files it loads, the skill directories it scans, and the external roots it may
///     reach - with a shared duplicate-path error and Save/Cancel footer.
///     <para>Its constructor DEMANDS the workspace root, so a window with no workspace to
///     describe cannot be built: the lists are workspace-scoped and have no meaning without
///     one.</para>
///     <para>The view owns the pickers because they are UI-affine platform concerns; the rows
///     and the save belong to the view-model, and the shell persists what the window
///     confirms.</para></summary>
internal partial class WorkspaceSettingsWindow : Window
{
  private readonly WorkspaceSettingsViewModel? _vm;

  public WorkspaceSettingsWindow() => InitializeComponent();

  public WorkspaceSettingsWindow(WorkspaceSettingsViewModel vm) : this()
  {
    ArgumentNullException.ThrowIfNull(vm);
    _vm = vm;
    DataContext = vm;
    vm.SessionFilePickRequested += async (_, _) => await PickFileAsync();
    vm.SkillDirectoryPickRequested += async (_, _) => await PickSkillDirectoryAsync();
    vm.TrustedDirectoryPickRequested += async (_, _) => await PickTrustedDirectoryAsync();
    vm.SaveRequested += (_, save) => Close(save);
  }

  /// <summary>Shows the native open-file picker and feeds the picked path back in. A
  ///     cancelled pick changes nothing.</summary>
  private async Task PickFileAsync()
  {
    IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
      Title = "Choose a session file for this workspace",
      AllowMultiple = false,
    });
    if (files.Count > 0)
    {
      _vm?.SetPickedSessionFile(files[0].Path.LocalPath);
    }
  }

  /// <summary>Shows the native folder picker for a skill directory and feeds the pick back
  ///     in. A cancelled pick changes nothing.</summary>
  private async Task PickSkillDirectoryAsync()
  {
    IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    {
      Title = "Choose a skill directory for this workspace",
      AllowMultiple = false,
    });
    if (folders.Count > 0)
    {
      _vm?.SetPickedSkillDirectory(folders[0].Path.LocalPath);
    }
  }

  /// <summary>Shows the native folder picker for a trusted directory and feeds the picked
  ///     root back in. A cancelled pick changes nothing.</summary>
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

  /// <summary>Test seam: runs the same feedback method the picker handler calls, so a headless
  ///     test exercises the view's wiring without the native picker.</summary>
  /// <param name="list">Which list the pick lands in: <c>session-file</c>, <c>skill-directory</c>
  ///     or <c>trusted-directory</c>.</param>
  /// <param name="path">The picked path.</param>
  internal void FeedPickedForTest(string list, string path)
  {
    switch (list)
    {
      case "session-file":
        _vm?.SetPickedSessionFile(path);
        break;
      case "skill-directory":
        _vm?.SetPickedSkillDirectory(path);
        break;
      case "trusted-directory":
        _vm?.SetPickedTrustedDirectory(path);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(list), list, "unknown list.");
    }
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
