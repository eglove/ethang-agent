using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One editable row in a session-file or skill-directory list: the absolute path and
///     its checkbox state.</summary>
internal sealed record SessionFileRow(string Path, bool Enabled)
{
  public static implicit operator SessionFileEntry(SessionFileRow row) => new(row.Path, row.Enabled);
}

/// <summary>One editable TRUSTED-DIRECTORY row: the external root, its enable checkbox, and
///     whether mutating tools may write inside it. Both checkboxes are editable in place, so an
///     approval can be narrowed to reading without removing and re-adding the root.</summary>
internal sealed record TrustedDirectoryRow(string Path, bool Enabled, bool Editable);

/// <summary>The three workspace-scoped lists a workspace settings save carries. The lists hold
///     FULL row state: an empty list is meaningful (it clears the stored preference), so the
///     shell persists exactly what the window confirmed.</summary>
/// <param name="WorkspaceRoot">The workspace the lists belong to.</param>
/// <param name="SessionFiles">Session files loaded for this workspace only.</param>
/// <param name="SkillDirectories">Skill directories scanned for this workspace only.</param>
/// <param name="TrustedDirectories">External roots this workspace may reach.</param>
internal sealed record WorkspaceSettingsSave(
    string WorkspaceRoot,
    IReadOnlyList<SessionFileEntry> SessionFiles,
    IReadOnlyList<SessionFileEntry> SkillDirectories,
    IReadOnlyList<TrustedDirectoryEntry> TrustedDirectories);

/// <summary>View-model behind the workspace settings window: the three lists that belong to ONE
///     workspace - the session files it loads, the skill directories it scans, and the external
///     roots it may reach. Rows are added only through the native pickers (no free-text path
///     entry); a duplicate path is a named error, never a silent second row.
///     <para>WORKSPACE SCOPE ONLY. The lists that apply to every workspace live in the app
///     Settings window, which is reachable with no workspace open; a workspace-scoped list
///     there would have nothing to describe.</para>
///     <para>Pure state and commands; the view owns the pickers and the window's close.</para>
///     </summary>
internal sealed partial class WorkspaceSettingsViewModel : ObservableObject
{
  /// <summary>Raised when the user asks for a file picker (session file).</summary>
  public event EventHandler? SessionFilePickRequested;

  /// <summary>Raised when the user asks for a folder picker (skill directory).</summary>
  public event EventHandler? SkillDirectoryPickRequested;

  /// <summary>Raised when the user asks for a folder picker (trusted directory).</summary>
  public event EventHandler? TrustedDirectoryPickRequested;

  /// <summary>Raised when the user confirms; carries the three lists. The view closes the
  ///     window with it and the shell persists it.</summary>
  public event EventHandler<WorkspaceSettingsSave>? SaveRequested;

  /// <summary>The workspace these lists belong to. Never blank: the window cannot be
  ///     constructed without one.</summary>
  public string WorkspaceRoot { get; }

  public IRelayCommand PickSessionFileCommand { get; }

  public IRelayCommand PickSkillDirectoryCommand { get; }

  public IRelayCommand PickTrustedDirectoryCommand { get; }

  public IRelayCommand SaveCommand { get; }

  /// <summary>The session-file rows: what loads for THIS workspace.</summary>
  public ObservableCollection<SessionFileRow> SessionFiles { get; } = [];

  /// <summary>The skill-directory rows: what the skill engine scans for THIS workspace (a
  ///     directory equal to a global one is skipped at the factory's resolution site).</summary>
  public ObservableCollection<SessionFileRow> SkillDirectories { get; } = [];

  /// <summary>The trusted-directory rows: external roots the user approves for THIS
  ///     workspace. Workspace scope only - an approval in one workspace must never silently
  ///     widen every other workspace's reach.</summary>
  public ObservableCollection<TrustedDirectoryRow> TrustedDirectories { get; } = [];

  /// <summary>The named problem with the last pick, or null. Rendered beside the lists so the
  ///     user sees why a pick was refused.</summary>
  [ObservableProperty]
  public partial string? FileError { get; set; }

  public WorkspaceSettingsViewModel(string workspaceRoot,
      IReadOnlyList<SessionFileEntry> workspaceSessionFiles,
      IReadOnlyList<SessionFileEntry> workspaceSkillDirectories,
      IReadOnlyList<TrustedDirectoryEntry> trustedDirectories)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentNullException.ThrowIfNull(workspaceSessionFiles);
    ArgumentNullException.ThrowIfNull(workspaceSkillDirectories);
    ArgumentNullException.ThrowIfNull(trustedDirectories);

    WorkspaceRoot = workspaceRoot;
    PickSessionFileCommand = new RelayCommand(() => SessionFilePickRequested?.Invoke(this, EventArgs.Empty));
    PickSkillDirectoryCommand = new RelayCommand(() => SkillDirectoryPickRequested?.Invoke(this, EventArgs.Empty));
    PickTrustedDirectoryCommand = new RelayCommand(() => TrustedDirectoryPickRequested?.Invoke(this, EventArgs.Empty));
    SaveCommand = new RelayCommand(Save);

    foreach (SessionFileEntry entry in workspaceSessionFiles)
    {
      SessionFiles.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (SessionFileEntry entry in workspaceSkillDirectories)
    {
      SkillDirectories.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (TrustedDirectoryEntry entry in trustedDirectories)
    {
      TrustedDirectories.Add(new TrustedDirectoryRow(entry.Path, entry.Enabled, entry.Editable));
    }
  }

  /// <summary>Feeds a picked session-file path back in. A cancelled pick (null/blank) changes
  ///     nothing; a duplicate is a named error.</summary>
  public void SetPickedSessionFile(string? path) => SetPicked(path, SessionFiles, "session file");

  /// <summary>Feeds a picked skill-directory path back in.</summary>
  public void SetPickedSkillDirectory(string? path) => SetPicked(path, SkillDirectories, "skill directory");

  /// <summary>Feeds a picked trusted-directory path back in. A new row starts ENABLED and
  ///     READ-ONLY: approving reach and approving writes are separate decisions, and the
  ///     dangerous one must be made deliberately.</summary>
  public void SetPickedTrustedDirectory(string? path) => SetPickedTrusted(path);

  [RelayCommand]
  private void RemoveSessionFile(SessionFileRow row) => _ = SessionFiles.Remove(row);

  [RelayCommand]
  private void RemoveSkillDirectory(SessionFileRow row) => _ = SkillDirectories.Remove(row);

  [RelayCommand]
  private void RemoveTrustedDirectory(TrustedDirectoryRow row) => _ = TrustedDirectories.Remove(row);

  /// <summary>Raises the confirmed lists. The rows are converted to their persisted shapes
  ///     HERE, so the shell receives exactly the three lists the user saw.</summary>
  private void Save() => SaveRequested?.Invoke(this, new WorkspaceSettingsSave(
      WorkspaceRoot,
      [.. SessionFiles.Select(row => new SessionFileEntry(row.Path, row.Enabled))],
      [.. SkillDirectories.Select(row => new SessionFileEntry(row.Path, row.Enabled))],
      [.. TrustedDirectories.Select(row => new TrustedDirectoryEntry(row.Path, row.Enabled, row.Editable))]));

  /// <summary>Duplicates are refused case-insensitively on the FULL path: two rows naming one
  ///     root would persist two approvals with different permissions and the resolver would
  ///     answer with whichever came first.</summary>
  private void SetPickedTrusted(string? path)
  {
    string trimmed = path?.Trim() ?? string.Empty;
    if (trimmed.Length == 0)
    {
      return; // cancelled pick - no-op
    }

    string normalized = Path.GetFullPath(trimmed);
    if (TrustedDirectories.Any(r =>
        string.Equals(Path.GetFullPath(r.Path), normalized, StringComparison.OrdinalIgnoreCase)))
    {
      FileError = $"The trusted directory is already configured: '{trimmed}'.";
      return;
    }

    FileError = null;
    TrustedDirectories.Add(new TrustedDirectoryRow(trimmed, Enabled: true, Editable: false));
  }

  /// <summary>Validates one picked path and appends a checked row. Pickers return absolute
  ///     paths by construction, so the only named failure is a duplicate (case-insensitive,
  ///     full-path normalized) - never a silent second row.</summary>
  private void SetPicked(string? path, ObservableCollection<SessionFileRow> rows, string kind)
  {
    string trimmed = path?.Trim() ?? string.Empty;
    if (trimmed.Length == 0)
    {
      return; // cancelled pick - no-op
    }

    string normalized = Path.GetFullPath(trimmed);
    if (rows.Any(r => string.Equals(Path.GetFullPath(r.Path), normalized, StringComparison.OrdinalIgnoreCase)))
    {
      FileError = $"The {kind} is already configured: '{trimmed}'.";
      return;
    }

    FileError = null;
    rows.Add(new SessionFileRow(trimmed, Enabled: true));
  }
}
