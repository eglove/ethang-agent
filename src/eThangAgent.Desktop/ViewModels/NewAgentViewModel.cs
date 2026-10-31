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
    IReadOnlyList<SessionFileEntry> WorkspaceSkillDirectories,
    IReadOnlyList<TrustedDirectoryEntry>? TrustedDirectories = null,
    GateDeclaration? Gate = null)
{
  /// <summary>Converts one dialog row list to its persisted entry shape.</summary>
  private static IReadOnlyList<SessionFileEntry> Entries(IEnumerable<SessionFileRow> rows) =>
      [.. rows.Select(r => new SessionFileEntry(r.Path, r.Enabled))];

  /// <summary>Builds the choice from the dialog's live row lists. The trusted-directory
  ///     rows carry one extra field (the write permission), so they convert separately:
  ///     the two shapes differ and a shared converter would have to lie about one.</summary>
  /// <param name="providerId">The chosen provider.</param>
  /// <param name="workspaceRoot">The chosen workspace directory.</param>
  /// <param name="rows">Every configured row list, grouped so the parameter count stays
  ///     inside S107's limit - the <c>LaunchRows</c> shape the dialog's own seeding uses.
  ///     </param>
  /// <param name="gate">What the dialog decided about the completion gate, or null when it
  ///     said nothing (issue #285).</param>
  internal static NewAgentChoice FromRows(string providerId, string workspaceRoot,
      DialogRowSet rows, GateDeclaration? gate = null) => new(
      providerId, workspaceRoot,
      Entries(rows.GlobalFiles), Entries(rows.WorkspaceFiles),
      Entries(rows.GlobalSkillDirectories), Entries(rows.WorkspaceSkillDirectories),
      [.. rows.TrustedDirectories.Select(r => new TrustedDirectoryEntry(r.Path, r.Enabled, r.Editable))],
      gate);
}

/// <summary>Every row list the dialog edits, grouped so a caller passes one value rather than
///     five positional lists (S107). The same grouping <c>LaunchRows</c> uses on the seeding
///     side, so the two directions of the dialog read alike.</summary>
/// <param name="GlobalFiles">Session files loaded for every workspace.</param>
/// <param name="WorkspaceFiles">Session files loaded for this workspace only.</param>
/// <param name="GlobalSkillDirectories">Skill directories scanned for every workspace.</param>
/// <param name="WorkspaceSkillDirectories">Skill directories scanned for this workspace only.</param>
/// <param name="TrustedDirectories">External roots this workspace may reach.</param>
internal sealed record DialogRowSet(
    IReadOnlyList<SessionFileRow> GlobalFiles,
    IReadOnlyList<SessionFileRow> WorkspaceFiles,
    IReadOnlyList<SessionFileRow> GlobalSkillDirectories,
    IReadOnlyList<SessionFileRow> WorkspaceSkillDirectories,
    IReadOnlyList<TrustedDirectoryRow> TrustedDirectories);

/// <summary>One editable row in the launch dialog: the absolute path and its
///     checkbox state. Same shape the settings editor used; shared with
///     SettingsViewModel while that surface still edits the same data.</summary>
internal sealed record SessionFileRow(string Path, bool Enabled)
{
  public static implicit operator SessionFileEntry(SessionFileRow row) => new(row.Path, row.Enabled);
}

/// <summary>One editable TRUSTED-DIRECTORY row (issue #96): the external root, its enable
///     checkbox, and whether mutating tools may write inside it. Both checkboxes are
///     editable in place, so an approval can be narrowed to reading without removing and
///     re-adding the root.</summary>
internal sealed record TrustedDirectoryRow(string Path, bool Enabled, bool Editable);

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
  /// <summary>Raised when the user asks for a folder picker (trusted directory).</summary>
  public event EventHandler? TrustedDirectoryPickRequested;

  /// <summary>Raised when the user confirms; carries the validated choice. The view
  ///     closes the dialog with it.</summary>
  public event EventHandler<NewAgentChoice>? OpenRequested;

  public IReadOnlyList<ProviderOption> Providers { get; }

  public ICommand ChooseWorkspaceCommand { get; }

  /// <summary>The Open command: GATED by <see cref="CanOpen"/>, so the button
  ///     stays disabled until a provider AND a workspace are chosen. The command
  ///     type is IRelayCommand (not ICommand) so the generated change hooks can
  ///     requery its availability when the provider or the workspace changes.</summary>
  public IRelayCommand OpenCommand { get; }

  public ICommand PickGlobalFileCommand { get; }

  public ICommand PickWorkspaceFileCommand { get; }

  public ICommand PickGlobalSkillDirectoryCommand { get; }

  public ICommand PickWorkspaceSkillDirectoryCommand { get; }

  public ICommand PickTrustedDirectoryCommand { get; }

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

  /// <summary>The trusted-directory rows (issue #96): external roots the user approves for
  ///     THIS workspace. Workspace scope only - an approval in one workspace must never
  ///     silently widen every other workspace's reach.</summary>
  public ObservableCollection<TrustedDirectoryRow> TrustedDirectories { get; } = [];

  /// <summary>The named problem with the last pick, or null. Rendered beside the
  ///     lists so the user sees why a pick was refused.</summary>
  [ObservableProperty]
  public partial string? FileError { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
  public partial ProviderOption? SelectedProvider { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
  public partial string? WorkspaceRoot { get; set; }

  /// <summary>Open is only actionable once BOTH a provider and a workspace are chosen.</summary>
  public bool CanOpen => SelectedProvider is not null && !string.IsNullOrWhiteSpace(WorkspaceRoot);

  // ---- the completion gate (issue #285) ------------------------------------

  /// <summary>True when the gate section should be SHOWN: a workspace is chosen and nobody has
  ///     answered for it yet.
  ///     <para>THE PROMPT IS FOR THE UNASKED ONLY (D1). A workspace that already declares commands
  ///     shows them as an editable prefill; one that was DECLINED shows nothing to answer, because
  ///     asking again is the nagging the issue forbids.</para></summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ShowGateQuestion))]
  public partial bool GateNeedsAnswer { get; set; }

  /// <summary>True when the section renders at all: a question to answer, or a declaration to
  ///     edit.</summary>
  public bool ShowGateQuestion => GateNeedsAnswer || GateCommands.Length > 0;

  /// <summary>The declared commands, ONE PER LINE, as the user wrote them.
  ///     <para>NEVER AUTO-FILLED FROM A TEMPLATE (D2): a suggestion the user clicks past would
  ///     turn a decision into a default. The box starts empty for a workspace nobody has asked,
  ///     and shows only what the user themselves stored.</para></summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ShowGateQuestion))]
  public partial string GateCommands { get; set; } = string.Empty;

  /// <summary>True when the user chose to decline. Mutually exclusive with typing commands:
  ///     a workspace either has a gate or has a recorded no.</summary>
  [ObservableProperty]
  public partial bool GateDeclined { get; set; }

  /// <summary>What the agent observed about this project, in prose, or null when nothing was
  ///     observed.
  ///     <para>THIS IS A SUGGESTION, NOT A VALUE. It is rendered as text beside an EMPTY box
  ///     (D2): the user reads the reasoning and types or ignores it. Filling the box would make
  ///     the suggestion a default, which is exactly what the user rejected.</para></summary>
  [ObservableProperty]
  public partial string? GateSuggestion { get; set; }

  /// <summary>What the dialog will persist about the gate, derived from the three controls.
  ///     <para>THE DECISION IS READ FROM THE CONTROLS AT OPEN TIME, never latched earlier: a user
  ///     who types commands and then ticks 'decline' must get the decline, and one who ticks
  ///     decline and then types must get the declaration. Whichever control states an answer last
  ///     wins, which is why typing CLEARS the decline and declining CLEARS the box.</para></summary>
  internal GateDeclaration GateChoice()
  {
    if (GateDeclined)
    {
      return GateDeclaration.Decline();
    }

    string[] commands = [.. GateCommands
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)];
    return commands.Length > 0 ? GateDeclaration.Declare(commands) : GateDeclaration.Unchanged;
  }

  /// <summary>Applies a workspace's stored gate state to the controls (called when a workspace is
  ///     picked). An unknown state asks the question rather than assuming a decision.</summary>
  /// <param name="state">What the workspace's gate keys say.</param>
  public void ApplyGateState(WorkspaceGateState state)
  {
    ArgumentNullException.ThrowIfNull(state);
    GateCommands = state.Kind == GateStateKind.Declared ? string.Join("\n", state.Commands) : string.Empty;
    GateDeclined = state.Kind == GateStateKind.Declined;
    GateNeedsAnswer = state.NeedsAnswer;
    GateSuggestion = null;
  }

  /// <summary>Typing in the command box is an ANSWER, so it clears a ticked decline: two
  ///     contradictory statements would otherwise reach the store and the last writer would win
  ///     invisibly.</summary>
  partial void OnGateCommandsChanged(string value)
  {
    if (value.Length > 0 && GateDeclined)
    {
      GateDeclined = false;
    }
  }

  /// <summary>Ticking decline is an ANSWER, so it clears any typed commands for the same reason.
  ///     </summary>
  partial void OnGateDeclinedChanged(bool value)
  {
    if (value && GateCommands.Length > 0)
    {
      GateCommands = string.Empty;
    }
  }

  public NewAgentViewModel(IReadOnlyList<ProviderOption> providers, string preferredProviderId,
      IReadOnlyList<SessionFileEntry>? globalFiles = null,
      IReadOnlyList<SessionFileEntry>? workspaceFiles = null,
      IReadOnlyList<SessionFileEntry>? globalSkillDirectories = null,
      IReadOnlyList<SessionFileEntry>? workspaceSkillDirectories = null,
      IReadOnlyList<TrustedDirectoryEntry>? trustedDirectories = null)
  {
    Providers = providers is { Count: > 0 }
        ? providers
        : throw new ArgumentException("At least one configured provider is required.", nameof(providers));

    // The commands exist BEFORE the selected provider is assigned: assigning it
    // raises the generated change hook, which requeries the Open command's
    // availability - a command that does not exist yet would fault there.
    CommandSet commands = CreateCommands();
    ChooseWorkspaceCommand = commands.ChooseWorkspace;
    PickGlobalFileCommand = commands.PickGlobalFile;
    PickWorkspaceFileCommand = commands.PickWorkspaceFile;
    PickGlobalSkillDirectoryCommand = commands.PickGlobalSkillDirectory;
    PickWorkspaceSkillDirectoryCommand = commands.PickWorkspaceSkillDirectory;
    PickTrustedDirectoryCommand = commands.PickTrustedDirectory;
    OpenCommand = commands.Open;

    SelectedProvider = providers.FirstOrDefault(p => p.Id == preferredProviderId) ?? providers[0];

    SeedRows(GlobalFiles, globalFiles);
    SeedRows(WorkspaceFiles, workspaceFiles);
    SeedRows(GlobalSkillDirectories, globalSkillDirectories);
    SeedRows(WorkspaceSkillDirectories, workspaceSkillDirectories);
    SeedTrustedRows(trustedDirectories);
  }

  /// <summary>Seeds the trusted-directory rows from the stored workspace list. Blank paths
  ///     are dropped here rather than rendered as an unusable row - the stored parse
  ///     already refuses them, so this only guards a hand-built entry.</summary>
  private void SeedTrustedRows(IReadOnlyList<TrustedDirectoryEntry>? entries)
  {
    foreach (TrustedDirectoryEntry entry in entries ?? [])
    {
      TrustedDirectories.Add(new TrustedDirectoryRow(entry.Path, entry.Enabled, entry.Editable));
    }
  }

  /// <summary>The dialog's command set, built in one place so the constructor stays
  ///     a wiring list. Every command only raises its event - the view owns the
  ///     pickers and the dialog's close.</summary>
  private CommandSet CreateCommands() => new(
      new RelayCommand(() => WorkspaceRequested?.Invoke(this, EventArgs.Empty)),
      new RelayCommand(() => GlobalFilePickRequested?.Invoke(this, EventArgs.Empty)),
      new RelayCommand(() => WorkspaceFilePickRequested?.Invoke(this, EventArgs.Empty)),
      new RelayCommand(() => GlobalSkillDirectoryPickRequested?.Invoke(this, EventArgs.Empty)),
      new RelayCommand(() => WorkspaceSkillDirectoryPickRequested?.Invoke(this, EventArgs.Empty)),
      new RelayCommand(() => TrustedDirectoryPickRequested?.Invoke(this, EventArgs.Empty)),
      // The Open command is GATED by CanOpen: until a provider AND a workspace
      // are chosen, the button stays disabled. It was always enabled before
      // (the no-canExecute overload), so pressing Open with no workspace
      // reached the shell with a null root and crashed the app on 2026-10-20.
      // The action's OWN guard is load-bearing: ICommand.Execute does not
      // consult CanExecute, so a programmatic Execute must not raise a choice
      // over a missing workspace either.
      new RelayCommand(Open, () => CanOpen));

  /// <summary>The Open action: raises the confirmed choice. It refuses a missing
  ///     provider or workspace by NOT raising the event at all - the dialog
  ///     stays open, and nothing downstream ever sees a half-built choice. The
  ///     null-forgiving operators that used to sit here (SelectedProvider! /
  ///     WorkspaceRoot!) suppressed the compiler's null warning and let a null
  ///     workspace reach Path.GetFullPath downstream; they are gone.</summary>
  private void Open()
  {
    if (SelectedProvider is not { } provider || string.IsNullOrWhiteSpace(WorkspaceRoot))
    {
      return;
    }

    OpenRequested?.Invoke(this, NewAgentChoice.FromRows(provider.Id, WorkspaceRoot,
        new DialogRowSet(GlobalFiles, WorkspaceFiles, GlobalSkillDirectories,
            WorkspaceSkillDirectories, TrustedDirectories), GateChoice()));
  }

  /// <summary>Appends one persisted entry list to a row list as checked rows. The
  ///     constructor's seeding loop for every scope, and the workspace reload's
  ///     refill.</summary>
  private static void SeedRows(ObservableCollection<SessionFileRow> rows,
      IReadOnlyList<SessionFileEntry>? entries)
  {
    foreach (SessionFileEntry entry in entries ?? [])
    {
      rows.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }
  }

  /// <summary>The commands the constructor wires onto its read-only properties.
  ///     Open is an IRelayCommand: the generated change hooks requery its
  ///     availability when the provider or the workspace changes.</summary>
  private sealed record CommandSet(
      ICommand ChooseWorkspace,
      ICommand PickGlobalFile,
      ICommand PickWorkspaceFile,
      ICommand PickGlobalSkillDirectory,
      ICommand PickWorkspaceSkillDirectory,
      ICommand PickTrustedDirectory,
      IRelayCommand Open);

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

  /// <summary>Feeds a picked trusted-directory path back in. A cancelled pick (null/blank)
  ///     changes nothing; a duplicate is a named error. A new row starts ENABLED and
  ///     READ-ONLY: approving reach and approving writes are separate decisions, and the
  ///     dangerous one must be made deliberately.</summary>
  public void SetPickedTrustedDirectory(string? path) => SetPickedTrusted(path);

  /// <summary>Replaces BOTH workspace-scope row lists with a picked workspace's
  ///     stored rows (the view loads them when a workspace is picked). The rows
  ///     reflect the stored preference for THIS root — the previous root's rows
  ///     never linger.</summary>
  public void ReplaceWorkspaceRows(IReadOnlyList<SessionFileEntry> files,
      IReadOnlyList<SessionFileEntry> directories, IReadOnlyList<TrustedDirectoryEntry>? trusted = null)
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

    // Trusted directories are workspace-scoped, so they reload with the workspace: a
    // previous root's approvals must never linger over a different directory.
    TrustedDirectories.Clear();
    SeedTrustedRows(trusted);
  }

  [RelayCommand]
  private void RemoveGlobalFile(SessionFileRow row) => _ = GlobalFiles.Remove(row);

  [RelayCommand]
  private void RemoveWorkspaceFile(SessionFileRow row) => _ = WorkspaceFiles.Remove(row);

  [RelayCommand]
  private void RemoveGlobalSkillDirectory(SessionFileRow row) => _ = GlobalSkillDirectories.Remove(row);

  [RelayCommand]
  private void RemoveWorkspaceSkillDirectory(SessionFileRow row) => _ = WorkspaceSkillDirectories.Remove(row);

  [RelayCommand]
  private void RemoveTrustedDirectory(TrustedDirectoryRow row) => _ = TrustedDirectories.Remove(row);

  /// <summary>Duplicates are refused case-insensitively on the FULL path, exactly as the
  ///     other row lists do: two rows naming one root would persist two approvals with
  ///     different permissions and the resolver would answer with whichever came first.</summary>
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
