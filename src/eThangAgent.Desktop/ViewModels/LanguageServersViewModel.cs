using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The one status a language row shows.</summary>
internal enum LanguageServerStatus
{
  Installed,
  NotInstalled,
  NeedsPrerequisite,
  Installing,
  UpdateAvailable,
  Failed,
}

/// <summary>One language the page lists: the catalog row's display data, the status this
///     machine is in, and the reason for it. Plain data - the page owns the commands, so one
///     row's state never has to reach into another's.</summary>
internal sealed record LanguageServerRow(
    string LanguageId,
    string DisplayName,
    string Version,
    string? PrerequisiteExecutable)
{
  /// <summary>The installed version the stored preference records, or null.</summary>
  public string? InstalledVersion { get; init; }

  /// <summary>The status this row shows.</summary>
  public LanguageServerStatus Status { get; init; } = LanguageServerStatus.NotInstalled;

  /// <summary>The sentence explaining the status: the missing executable's name, the install
  ///     failure's reason, or the installed version.</summary>
  public string StatusDetail { get; init; } = string.Empty;

  /// <summary>True when the install command can run: everything but C#, which this app
  ///     installs without being asked, and nothing while an install is already running.</summary>
  public bool CanInstall => LanguageId != LanguageServerCatalogBootstrap.CSharpLanguageId
      && Status != LanguageServerStatus.Installing;

  /// <summary>True when a remove command can run: only an installed non-C# language has
  ///     anything to remove.</summary>
  public bool CanRemove => LanguageId != LanguageServerCatalogBootstrap.CSharpLanguageId
      && Status == LanguageServerStatus.Installed;

  /// <summary>True when an update command can run: the stored version is behind the
  ///     catalog's pinned version.</summary>
  public bool CanUpdate => LanguageId != LanguageServerCatalogBootstrap.CSharpLanguageId
      && Status == LanguageServerStatus.UpdateAvailable;
}

/// <summary>The language ids the page treats specially, named once.</summary>
internal static class LanguageServerCatalogBootstrap
{
  /// <summary>The language this app installs silently and automatically.</summary>
  internal const string CSharpLanguageId = "csharp";
}

/// <summary>One page row: the display data plus the three commands that act on it. The
///     commands delegate to the page, so the page owns the install/remove/update policy and
///     the row owns only what it shows.</summary>
internal sealed partial class LanguageServerRowViewModel : ObservableObject
{
  private readonly LanguageServersViewModel _owner;

  public LanguageServerRowViewModel(LanguageServersViewModel owner, LanguageServerRow row)
  {
    _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    ArgumentNullException.ThrowIfNull(row);
    // The commands exist before the data: assigning the data raises the changed hooks,
    // which requery command availability.
    InstallCommand = new AsyncRelayCommand(() => _owner.InstallAsync(this), () => Data.CanInstall);
    RemoveCommand = new AsyncRelayCommand(() => _owner.RemoveAsync(this), () => Data.CanRemove);
    UpdateCommand = new AsyncRelayCommand(() => _owner.InstallAsync(this), () => Data.CanUpdate);
    Data = row;
  }

  /// <summary>The row's data. Replacing it raises every bound property at once.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(DisplayName))]
  [NotifyPropertyChangedFor(nameof(Status))]
  [NotifyPropertyChangedFor(nameof(StatusText))]
  [NotifyPropertyChangedFor(nameof(StatusDetail))]
  [NotifyPropertyChangedFor(nameof(Version))]
  [NotifyPropertyChangedFor(nameof(CanInstall))]
  [NotifyPropertyChangedFor(nameof(CanRemove))]
  [NotifyPropertyChangedFor(nameof(CanUpdate))]
  [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
  [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
  [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
  public partial LanguageServerRow Data { get; set; }

  public string LanguageId => Data.LanguageId;

  public string DisplayName => Data.DisplayName;

  public string Version => Data.Version;

  public LanguageServerStatus Status => Data.Status;

  /// <summary>The status as the page prints it.</summary>
  public string StatusText => Data.Status switch
  {
    LanguageServerStatus.Installed => "Installed",
    LanguageServerStatus.NotInstalled => "Not installed",
    LanguageServerStatus.NeedsPrerequisite => "Needs prerequisite",
    LanguageServerStatus.Installing => "Installing",
    LanguageServerStatus.UpdateAvailable => "Update available",
    LanguageServerStatus.Failed => "Failed",
    _ => "Unknown",
  };

  public string StatusDetail => Data.StatusDetail;

  public bool CanInstall => Data.CanInstall;

  public bool CanRemove => Data.CanRemove;

  public bool CanUpdate => Data.CanUpdate;

  public IAsyncRelayCommand InstallCommand { get; }

  public IAsyncRelayCommand RemoveCommand { get; }

  public IAsyncRelayCommand UpdateCommand { get; }
}

/// <summary>View-model behind the language-servers page: one row per catalog language, the
///     status this machine is in for each, and the install / remove / update commands. The
///     page lists every language ALWAYS - a missing prerequisite is a status, never a reason
///     to hide a row. Installed state lives in the app's own preference store, so a language
///     installed in one run reads back as installed in the next.
///     <para>The catalog, the installer and detection arrive through the composition seams,
///     so this assembly names no language-server type of its own beyond the status values
///     the installer reports.</para></summary>
internal sealed partial class LanguageServersViewModel : ObservableObject
{
  private readonly ILanguageServerCatalog _catalog;
  private readonly ILanguageServerInstaller _installer;
  private readonly IWorkspaceLanguageDetector _detector;
  private readonly string? _workspaceRoot;
  private readonly List<LanguageServerRowViewModel> _rows = [];

  public LanguageServersViewModel(
      ILanguageServerCatalog catalog,
      ILanguageServerInstaller installer,
      IWorkspaceLanguageDetector detector,
      string? workspaceRoot = null)
  {
    _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    _workspaceRoot = workspaceRoot;
    foreach (LspLanguageRow row in _catalog.All)
    {
      _rows.Add(new LanguageServerRowViewModel(this, new LanguageServerRow(
          row.LanguageId, row.DisplayName, row.Version, row.PrerequisiteExecutable)));
    }

    Rows = _rows;
    RefreshCommand = new AsyncRelayCommand(RefreshAsync);
  }

  /// <summary>Every language the catalog serves, in catalog order.</summary>
  public IReadOnlyList<LanguageServerRowViewModel> Rows { get; }

  public IAsyncRelayCommand RefreshCommand { get; }

  /// <summary>The first failure the page has to report, or null when nothing failed.</summary>
  [ObservableProperty]
  public partial string? PageError { get; set; }

  /// <summary>Re-reads installed state and prerequisite availability for every row, then
  ///     fills each row's status.</summary>
  public async Task RefreshAsync()
  {
    await _installer.RefreshAsync(CancellationToken.None);
    foreach (LanguageServerRowViewModel row in _rows)
    {
      row.Data = Resolve(row.Data);
    }
  }

  /// <summary>Runs the row's install strategy, showing Installing for the duration. A
  ///     success persists the language and its version; a failure shows the reason and
  ///     persists nothing.</summary>
  internal async Task InstallAsync(LanguageServerRowViewModel row)
  {
    ArgumentNullException.ThrowIfNull(row);
    // The guard is load-bearing: ICommand.Execute does not consult CanExecute, so the C# row
    // refuses here rather than relying on a disabled button.
    if (!row.CanInstall && row.Status != LanguageServerStatus.UpdateAvailable)
    {
      return;
    }

    row.Data = row.Data with
    {
      Status = LanguageServerStatus.Installing,
      StatusDetail = "Installing version " + row.Data.Version + ".",
    };

    LspInstallResult result;
    try
    {
      result = await _installer.InstallAsync(row.LanguageId, CancellationToken.None);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      row.Data = row.Data with { Status = LanguageServerStatus.Failed, StatusDetail = ex.Message };
      PageError = ex.Message;
      return;
    }

    if (result.Outcome == LspInstallOutcome.Installed)
    {
      PageError = null;
      row.Data = Resolve(row.Data);
      return;
    }

    string reason = result.Reason ?? Describe(result.Outcome, row.Data);
    row.Data = row.Data with { Status = LanguageServerStatus.Failed, StatusDetail = reason };
    PageError = reason;
  }

  /// <summary>Deletes the row's install directory and clears its stored entry. Nothing
  ///     outside that directory is touched, so the user's own toolchain is never at risk.</summary>
  internal async Task RemoveAsync(LanguageServerRowViewModel row)
  {
    ArgumentNullException.ThrowIfNull(row);
    if (!row.CanRemove)
    {
      return;
    }

    try
    {
      await _installer.RemoveAsync(row.LanguageId, CancellationToken.None);
      PageError = null;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      PageError = ex.Message;
    }

    row.Data = Resolve(row.Data);
  }

  /// <summary>The language ids this workspace holds but this machine cannot serve, in
  ///     detection order. A workspace with no root, or a language with no catalog row, is
  ///     never reported.</summary>
  public IReadOnlyList<string> MissingForWorkspace()
  {
    if (string.IsNullOrWhiteSpace(_workspaceRoot))
    {
      return [];
    }

    HashSet<string> installed = new(_installer.InstalledLanguageIds(), StringComparer.OrdinalIgnoreCase);
    return
    [
        .. _detector.Detect(_workspaceRoot)
            .Where(detected => !installed.Contains(detected.LanguageId))
            .Select(detected => detected.LanguageId),
    ];
  }

  /// <summary>Fills one row's status from the stored preference and this machine's
  ///     toolchain. C# is always installed: this app installs it without being asked.</summary>
  private LanguageServerRow Resolve(LanguageServerRow row)
  {
    if (string.Equals(row.LanguageId, LanguageServerCatalogBootstrap.CSharpLanguageId, StringComparison.OrdinalIgnoreCase))
    {
      return row with
      {
        Status = LanguageServerStatus.Installed,
        StatusDetail = "Installed automatically with the app.",
        InstalledVersion = row.Version,
      };
    }

    string? installedVersion = _installer.InstalledVersion(row.LanguageId);
    return installedVersion is not null
        ? ResolveInstalled(row, installedVersion)
        : ResolveAbsent(row);
  }

  /// <summary>The status of a language the stored preference names: installed, or an update
  ///     available when the stored version is behind the catalog's pin.</summary>
  private static LanguageServerRow ResolveInstalled(LanguageServerRow row, string installedVersion) =>
      string.Equals(installedVersion, row.Version, StringComparison.Ordinal)
          ? row with
          {
            Status = LanguageServerStatus.Installed,
            StatusDetail = "Installed version " + installedVersion + ".",
            InstalledVersion = installedVersion,
          }
          : row with
          {
            Status = LanguageServerStatus.UpdateAvailable,
            StatusDetail = "Installed version " + installedVersion + "; version " + row.Version + " is available.",
            InstalledVersion = installedVersion,
          };

  /// <summary>The status of a language the stored preference does not name: not installed, or
  ///     gated on a toolchain this machine lacks.</summary>
  private LanguageServerRow ResolveAbsent(LanguageServerRow row)
  {
    string? missing = _installer.MissingPrerequisiteExecutable(row.LanguageId);
    return missing is null
        ? row with { Status = LanguageServerStatus.NotInstalled, StatusDetail = "Not installed.", InstalledVersion = null }
        : row with
        {
          Status = LanguageServerStatus.NeedsPrerequisite,
          StatusDetail = "Needs " + missing + " (not found)",
          InstalledVersion = null,
        };
  }

  private static string Describe(LspInstallOutcome outcome, LanguageServerRow row)
  {
    string reason = "The install did not complete: the download or the install command failed.";
    if (outcome == LspInstallOutcome.HashMismatch)
    {
      reason = "The downloaded artifact did not match the recorded checksum and was discarded.";
    }

    if (outcome == LspInstallOutcome.PrerequisiteMissing)
    {
      reason = "Needs " + row.PrerequisiteExecutable + " (not found)";
    }

    return reason;
  }
}
