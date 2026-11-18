using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The workspace launcher: the recent directories, an open-folder action, and the
///     settings entry that makes a provider key configurable before any workspace exists.
///     <para>The window owns only window mechanics — the folder picker and the settings
///     dialog are its job because they are UI-affine; the rows, the loading state and what a
///     chosen workspace means belong to the view-model and the host.</para></summary>
internal partial class LauncherWindow : Window
{
  private readonly LauncherViewModel? _vm;

  public LauncherWindow() => InitializeComponent();

  public LauncherWindow(LauncherViewModel vm) : this()
  {
    ArgumentNullException.ThrowIfNull(vm);
    _vm = vm;
    DataContext = vm;
    vm.OpenFolderRequested += async (_, _) => await PickFolderAsync();
    // The list fetch starts once the window is up: LoadAsync keeps failures internal (they
    // land in its error state), so the discarded task is safe.
    Opened += (_, _) => _ = _vm.LoadAsync();
  }

  /// <summary>Shows the platform folder picker and feeds the pick back to the view-model.
  ///     A cancelled pick is a no-op.</summary>
  private async Task PickFolderAsync()
  {
    IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions { Title = "Open a workspace folder", AllowMultiple = false })
        .ConfigureAwait(true);
    if (picked.Count == 0)
    {
      return;
    }

    if (picked[0].TryGetLocalPath() is { } path)
    {
      _vm?.ChooseFolder(path);
    }
  }

  private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
      // Same guard as the row's own open command: only an existing directory opens.
      => _vm?.OpenRecentCommand.Execute((sender as ListBox)?.SelectedItem);
}
