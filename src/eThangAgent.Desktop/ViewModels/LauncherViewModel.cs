using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>View-model behind the workspace launcher: the recent directories, an open-folder
///     action, and a settings entry so a provider key can be configured before any workspace
///     exists. Pure state and commands; the window owns the folder picker and the settings
///     dialog, and the host owns what a chosen workspace opens.</summary>
internal sealed partial class LauncherViewModel : ObservableObject
{
  /// <summary>How many recent directories the launcher lists.</summary>
  internal const int RecentLimit = 10;

  private readonly IRecentWorkspaceStore? _recents;
  private bool _loaded;

  /// <summary>Raised when the user asks for the platform folder picker. The view shows it
  ///     and feeds the result back through <see cref="ChooseFolder"/>.</summary>
  public event EventHandler? OpenFolderRequested;

  /// <summary>Raised when a workspace is chosen; carries its root.</summary>
  public event EventHandler<WorkspaceHandoff>? WorkspaceChosen;

  /// <summary>Raised when the user asks for the app Settings window.</summary>
  public event EventHandler? SettingsRequested;

  /// <summary>The recent directories, newest first.</summary>
  public ObservableCollection<RecentWorkspaceRow> Recents { get; } = [];

  /// <summary>Whether the launcher should say there is nothing to list yet: the load has
  ///     finished and produced no rows.</summary>
  public bool ShowEmptyState => !IsLoading && Recents.Count == 0;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
  public partial bool IsLoading { get; set; }

  /// <summary>The named problem with the last load or forget, or null.</summary>
  [ObservableProperty]
  public partial string? LoadError { get; set; }

  /// <summary>Whether any provider is configured, so the launcher can say that a key is
  ///     needed before an agent can run.</summary>
  public bool HasConfiguredProvider { get; }

  public IRelayCommand OpenFolderCommand { get; }

  public IRelayCommand SettingsCommand { get; }

  public IRelayCommand<RecentWorkspaceRow> OpenRecentCommand { get; }

  public IRelayCommand<RecentWorkspaceRow> ForgetCommand { get; }

  /// <param name="recents">The recent-workspace store; null (tests, hosts without a
  ///     database) means the list stays empty and nothing is remembered.</param>
  /// <param name="hasConfiguredProvider">Whether a provider key is configured.</param>
  public LauncherViewModel(IRecentWorkspaceStore? recents, bool hasConfiguredProvider)
  {
    _recents = recents;
    HasConfiguredProvider = hasConfiguredProvider;

    // The commands exist before the observable properties: setting those raises the
    // generated change hooks, which requery command availability.
    OpenFolderCommand = new RelayCommand(() => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
    SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    OpenRecentCommand = new RelayCommand<RecentWorkspaceRow>(Open, row => row is { Exists: true });
    ForgetCommand = new RelayCommand<RecentWorkspaceRow>(Forget, row => row is not null);
    Recents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyState));
  }

  /// <summary>Loads the recent list off the UI thread. A failure lands in
  ///     <see cref="LoadError"/> and the launcher stays usable: opening a folder by hand
  ///     never depends on the list. Only the first call loads.</summary>
  public async Task LoadAsync()
  {
    if (_loaded || IsLoading)
    {
      return;
    }

    _loaded = true;
    if (_recents is null)
    {
      return;
    }

    IsLoading = true;
    // Named decision (CA1031): a loader fault must land in the launcher's error state,
    // never escape - the fire-and-forget caller cannot observe it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Task<Result<IReadOnlyList<RecentWorkspace>>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _recents.ListAsync(RecentLimit, CancellationToken.None));
      }

      Result<IReadOnlyList<RecentWorkspace>> listed = await scheduled;
      if (!listed.IsSuccess)
      {
        LoadError = listed.Error.Message;
        return;
      }

      Recents.Clear();
      foreach (RecentWorkspace recent in listed.Value)
      {
        Recents.Add(RecentWorkspaceRow.From(recent));
      }
    }
    catch (Exception ex)
    {
      LoadError = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
#pragma warning restore CA1031
  }

  /// <summary>Accepts a directory the picker returned and raises it as the chosen
  ///     workspace. A blank or missing directory is refused into <see cref="LoadError"/>
  ///     rather than opening a window bound to nothing.</summary>
  /// <param name="root">The absolute directory the picker returned.</param>
  public void ChooseFolder(string root)
  {
    if (string.IsNullOrWhiteSpace(root))
    {
      LoadError = "No folder was chosen.";
      return;
    }

    if (!Directory.Exists(root))
    {
      LoadError = $"'{root}' is not a directory.";
      return;
    }

    LoadError = null;
    WorkspaceChosen?.Invoke(this, new WorkspaceHandoff(root));
  }

  private void Open(RecentWorkspaceRow? row)
  {
    if (row is { Exists: true })
    {
      WorkspaceChosen?.Invoke(this, new WorkspaceHandoff(row.Root));
    }
  }

  /// <summary>Removes a row from the list and from the store. The row leaves the list
  ///     immediately; a failed delete puts it back and reports why.</summary>
  private void Forget(RecentWorkspaceRow? row)
  {
    if (row is null)
    {
      return;
    }

    int index = Recents.IndexOf(row);
    _ = Recents.Remove(row);
    if (_recents is null)
    {
      return;
    }

    _ = ForgetAsync(row, index);
  }

  private async Task ForgetAsync(RecentWorkspaceRow row, int index)
  {
    // Named decision (CA1031): a failed forget is information for the user, not a crash.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<bool> forgotten = await _recents!.ForgetAsync(row.Root, CancellationToken.None)
          .ConfigureAwait(true);
      if (!forgotten.IsSuccess)
      {
        Restore(row, index);
        LoadError = forgotten.Error.Message;
      }
    }
    catch (Exception ex)
    {
      Restore(row, index);
      LoadError = ex.Message;
    }
#pragma warning restore CA1031
  }

  private void Restore(RecentWorkspaceRow row, int index)
  {
    if (Recents.Contains(row))
    {
      return;
    }

    Recents.Insert(Math.Clamp(index, 0, Recents.Count), row);
  }
}
