using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The provider/workspace pair chosen in the new-agent dialog, plus the
///     session-file and skill-directory lists configured in the same dialog (E
///     moved out of Settings). The lists carry FULL row state - empty lists are
///     meaningful (clear the stored preference), so MainViewModel persists
///     exactly what the dialog confirmed.</summary>
internal sealed record NewAgentChoice(string ProviderId, string WorkspaceRoot,
    IReadOnlyList<SessionFileEntry> GlobalFiles,
    IReadOnlyList<SessionFileEntry> WorkspaceFiles,
    IReadOnlyList<SessionFileEntry> GlobalSkillDirectories,
    IReadOnlyList<SessionFileEntry> WorkspaceSkillDirectories)
{
  /// <summary>Converts one dialog row list to its persisted entry shape.</summary>
  private static IReadOnlyList<SessionFileEntry> Entries(IEnumerable<SessionFileRow> rows) =>
      [.. rows.Select(r => new SessionFileEntry(r.Path, r.Enabled))];

  /// <summary>Builds the choice from the dialog's live row lists.</summary>
  internal static NewAgentChoice FromRows(string providerId, string workspaceRoot,
      IReadOnlyList<SessionFileRow> globalFiles, IReadOnlyList<SessionFileRow> workspaceFiles,
      IReadOnlyList<SessionFileRow> globalSkillDirectories,
      IReadOnlyList<SessionFileRow> workspaceSkillDirectories) => new(
      providerId, workspaceRoot,
      Entries(globalFiles), Entries(workspaceFiles),
      Entries(globalSkillDirectories), Entries(workspaceSkillDirectories));
}

/// <summary>One editable row in the launch dialog: the absolute path and its
///     checkbox state. Same shape the settings editor used; shared with
///     SettingsViewModel while that surface still edits the same data.</summary>
internal sealed record SessionFileRow(string Path, bool Enabled)
{
  public static implicit operator SessionFileEntry(SessionFileRow row) => new(row.Path, row.Enabled);
}

/// <summary>View-model behind the new-agent modal: an AI-provider dropdown, a
///     workspace chosen through the platform folder picker, and the session-file /
///     skill-directory lists configured in the same dialog. Rows are added ONLY
///     through the native pickers (no free-text path entry); a duplicate path is a
///     named error, never a silent second row. Pure state and commands; the view
///     owns the pickers.</summary>
internal sealed partial class NewAgentViewModel : ObservableObject
{
  /// <summary>Raised when the user asks for the platform folder picker. The view
  ///     shows it and feeds the result back through <see cref="SetWorkspaceRoot"/>.</summary>
  public event EventHandler? WorkspaceRequested;

  /// <summary>Raised when the user asks for a file picker (global session file).</summary>
  public event EventHandler? GlobalFilePickRequested;
  /// <summary>Raised when the user asks for a file picker (workspace session file).</summary>
  public event EventHandler? WorkspaceFilePickRequested;
  /// <summary>Raised when the user asks for a folder picker (global skill directory).</summary>
  public event EventHandler? GlobalSkillDirectoryPickRequested;
  /// <summary>Raised when the user asks for a folder picker (workspace skill directory).</summary>
  public event EventHandler? WorkspaceSkillDirectoryPickRequested;

  /// <summary>Raised when the user confirms; carries the validated choice. The view
  ///     closes the dialog with it.</summary>
  public event EventHandler<NewAgentChoice>? OpenRequested;

  public IReadOnlyList<ProviderOption> Providers { get; }

  public ICommand ChooseWorkspaceCommand { get; }

  public ICommand OpenCommand { get; }

  public ICommand PickGlobalFileCommand { get; }

  public ICommand PickWorkspaceFileCommand { get; }

  public ICommand PickGlobalSkillDirectoryCommand { get; }

  public ICommand PickWorkspaceSkillDirectoryCommand { get; }

  /// <summary>The global session-file rows: what loads (when checked) for every
  ///     workspace. Editable in place - checkbox toggles, row remove.</summary>
  public ObservableCollection<SessionFileRow> GlobalFiles { get; } = [];

  /// <summary>The workspace-scope session-file rows: what loads for THIS workspace.
  ///     The dialog always has a workspace context (it is where the root is chosen),
  ///     so this scope is never inert.</summary>
  public ObservableCollection<SessionFileRow> WorkspaceFiles { get; } = [];

  /// <summary>The global skill-directory rows: what the skill engine scans for every
  ///     workspace.</summary>
  public ObservableCollection<SessionFileRow> GlobalSkillDirectories { get; } = [];

  /// <summary>The workspace-scope skill-directory rows (a directory equal to a
  ///     global one is skipped at the factory's resolution site).</summary>
  public ObservableCollection<SessionFileRow> WorkspaceSkillDirectories { get; } = [];

  /// <summary>The named problem with the last pick, or null. Rendered beside the
  ///     lists so the user sees why a pick was refused.</summary>
  [ObservableProperty]
  public partial string? FileError { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  public partial ProviderOption? SelectedProvider { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  public partial string? WorkspaceRoot { get; set; }

  /// <summary>Open is only actionable once BOTH a provider and a workspace are chosen.</summary>
  public bool CanOpen => SelectedProvider is not null && !string.IsNullOrWhiteSpace(WorkspaceRoot);

  public NewAgentViewModel(IReadOnlyList<ProviderOption> providers, string preferredProviderId,
      IReadOnlyList<SessionFileEntry>? globalFiles = null,
      IReadOnlyList<SessionFileEntry>? workspaceFiles = null,
      IReadOnlyList<SessionFileEntry>? globalSkillDirectories = null,
      IReadOnlyList<SessionFileEntry>? workspaceSkillDirectories = null)
  {
    Providers = providers is { Count: > 0 }
        ? providers
        : throw new ArgumentException("At least one configured provider is required.", nameof(providers));
    SelectedProvider = providers.FirstOrDefault(p => p.Id == preferredProviderId) ?? providers[0];
    ChooseWorkspaceCommand = new RelayCommand(() => WorkspaceRequested?.Invoke(this, EventArgs.Empty));
    PickGlobalFileCommand = new RelayCommand(() => GlobalFilePickRequested?.Invoke(this, EventArgs.Empty));
    PickWorkspaceFileCommand = new RelayCommand(() => WorkspaceFilePickRequested?.Invoke(this, EventArgs.Empty));
    PickGlobalSkillDirectoryCommand = new RelayCommand(() => GlobalSkillDirectoryPickRequested?.Invoke(this, EventArgs.Empty));
    PickWorkspaceSkillDirectoryCommand = new RelayCommand(() => WorkspaceSkillDirectoryPickRequested?.Invoke(this, EventArgs.Empty));
    OpenCommand = new RelayCommand(() => OpenRequested?.Invoke(this, NewAgentChoice.FromRows(
        SelectedProvider!.Id, WorkspaceRoot!, GlobalFiles, WorkspaceFiles,
        GlobalSkillDirectories, WorkspaceSkillDirectories)));
    foreach (SessionFileEntry entry in globalFiles ?? [])
    {
      GlobalFiles.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (SessionFileEntry entry in workspaceFiles ?? [])
    {
      WorkspaceFiles.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (SessionFileEntry entry in globalSkillDirectories ?? [])
    {
      GlobalSkillDirectories.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (SessionFileEntry entry in workspaceSkillDirectories ?? [])
    {
      WorkspaceSkillDirectories.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }
  }

  /// <summary>Feeds the folder-picker result back in. An empty pick (cancelled
  ///     dialog) keeps the previous value.</summary>
  public void SetWorkspaceRoot(string? path)
  {
    if (!string.IsNullOrWhiteSpace(path))
    {
      WorkspaceRoot = path;
    }
  }

  /// <summary>Feeds a picked global session-file path back in. A cancelled pick
  ///     (null/blank) changes nothing; a duplicate is a named error.</summary>
  public void SetPickedGlobalFile(string? path) => SetPicked(path, GlobalFiles, "session file");

  /// <summary>Feeds a picked workspace session-file path back in.</summary>
  public void SetPickedWorkspaceFile(string? path) => SetPicked(path, WorkspaceFiles, "session file");

  /// <summary>Feeds a picked global skill-directory path back in.</summary>
  public void SetPickedGlobalSkillDirectory(string? path) => SetPicked(path, GlobalSkillDirectories, "skill directory");

  /// <summary>Feeds a picked workspace skill-directory path back in.</summary>
  public void SetPickedWorkspaceSkillDirectory(string? path) => SetPicked(path, WorkspaceSkillDirectories, "skill directory");

  /// <summary>Replaces BOTH workspace-scope row lists with a picked workspace's
  ///     stored rows (the view loads them when a workspace is picked). The rows
  ///     reflect the stored preference for THIS root — the previous root's rows
  ///     never linger.</summary>
  public void ReplaceWorkspaceRows(IReadOnlyList<SessionFileEntry> files,
      IReadOnlyList<SessionFileEntry> directories)
  {
    WorkspaceFiles.Clear();
    foreach (SessionFileEntry entry in files)
    {
      WorkspaceFiles.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    WorkspaceSkillDirectories.Clear();
    foreach (SessionFileEntry entry in directories)
    {
      WorkspaceSkillDirectories.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }
  }

  [RelayCommand]
  private void RemoveGlobalFile(SessionFileRow row) => _ = GlobalFiles.Remove(row);

  [RelayCommand]
  private void RemoveWorkspaceFile(SessionFileRow row) => _ = WorkspaceFiles.Remove(row);

  [RelayCommand]
  private void RemoveGlobalSkillDirectory(SessionFileRow row) => _ = GlobalSkillDirectories.Remove(row);

  [RelayCommand]
  private void RemoveWorkspaceSkillDirectory(SessionFileRow row) => _ = WorkspaceSkillDirectories.Remove(row);

  /// <summary>Validates one picked path and appends a checked row. Pickers return
  ///     absolute paths by construction, so the only named failure is a duplicate
  ///     (case-insensitive, full-path normalized) - never a silent second row.</summary>
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
