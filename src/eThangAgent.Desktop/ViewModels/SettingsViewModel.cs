using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One selectable commit message style in the settings modal.</summary>
internal sealed record CommitStyleOption(CommitStyle Style, string Display)
{
  internal static readonly CommitStyleOption Conventional = new(CommitStyle.Conventional,
      "Conventional commits");
  internal static readonly CommitStyleOption Gitmoji = new(CommitStyle.Gitmoji,
      "Gitmoji");
  internal static readonly CommitStyleOption None = new(CommitStyle.None,
      "Plain (no prefix)");
}

/// <summary>The settings confirmed in the settings modal: the API key and the commit
///     style. Null keys mean "cleared" — the provider stops being configured.
///     (A cancelled dialog closes with no result at all, so unchanged-vs-cleared never
///     collides.)</summary>
/// <summary>One choosable compaction summarizer: the Automatic row (persisted as
///     unset — cheapest capable resolves at compaction time) or a concrete model id.</summary>
internal sealed record CompactionModelOption(string? ModelId, string Display)
{
  internal static readonly CompactionModelOption Automatic = new(null, "Automatic (cheapest capable)");
}

/// <summary>One editable session-file row in the settings modal: the absolute path
///     and its checkbox state.</summary>
internal sealed record SessionFileRow(string Path, bool Enabled)
{
  public static implicit operator SessionFileEntry(SessionFileRow row) => new(row.Path, row.Enabled);
}

internal sealed record SettingsUpdate(string? OpenRouterApiKey, CommitStyle CommitStyle,
    string? CompactionModelId = null, string? CompactionWorkspaceKey = null,
    IReadOnlyList<SessionFileEntry>? GlobalFiles = null, IReadOnlyList<SessionFileEntry>? WorkspaceFiles = null,
    string? WorkspaceRoot = null,
    string? MaxConcurrentAgentsText = null, string? DefaultModelText = null, bool RemoteHost = false,
    string? WatchdogTickText = null, string? WatchdogIdleText = null, string? WatchdogWrapUpText = null,
    string? OpenRouterBaseUrlText = null,
    bool ComputerUse = false,
    IReadOnlyList<SessionFileEntry>? GlobalSkillDirectories = null,
    IReadOnlyList<SessionFileEntry>? WorkspaceSkillDirectories = null,
    string? SkillRegistryDefaultTarget = null);

/// <summary>View-model behind the settings modal: the API-key field, a reveal toggle,
///     and their shared validation.
///     Blank means cleared; whitespace inside a key is rejected — provider keys never
///     contain any. Pure state and commands; persistence and window closing belong to
///     the caller.</summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
  /// <summary>Raised when the user confirms valid settings; carries the update. The
  ///     view closes the dialog with it.</summary>
  public event EventHandler<SettingsUpdate>? SaveRequested;

  public IRelayCommand SaveCommand { get; }

  /// <summary>The three commit styles, in display order.</summary>
  public IReadOnlyList<CommitStyleOption> CommitStyles { get; } =
      [CommitStyleOption.Conventional, CommitStyleOption.Gitmoji, CommitStyleOption.None];

  /// <summary>The compaction summarizer choices: Automatic plus the session provider's
  ///     catalog model ids.</summary>
  public IReadOnlyList<CompactionModelOption> CompactionModels { get; }

  [ObservableProperty]
  public partial CompactionModelOption SelectedCompactionModel { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string OpenRouterKey { get; set; }

  /// <summary>Maximum concurrently running child agents as raw text — blank means the
  ///     shipped default (4); a non-blank value must be a positive integer.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string MaxConcurrentAgentsText { get; set; }

  /// <summary>The default child-agent model as raw text — blank means no default
  ///     (spawns must then pass a model explicitly).</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string DefaultModelText { get; set; }

  /// <summary>True opts newly opened agents into the out-of-process child host.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial bool RemoteHost { get; set; }

  /// <summary>True enables the 'computer' tool (desktop automation) for newly opened agents.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial bool ComputerUse { get; set; }

  /// <summary>The skill-registry default install target: unset / global / workspace.
  ///     Raw tri-state; strict parsing happens in AgentSettingsLoader at load.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string SkillRegistryTarget { get; set; } = string.Empty;

  /// <summary>ComboBox index mirror of <see cref="SkillRegistryTarget"/>:
  ///     0 = unset, 1 = global, 2 = workspace; anything else renders as unset.</summary>
  public int SkillRegistryTargetIndex
  {
    get => SkillRegistryTarget switch
    {
      "global" => 1,
      "workspace" => 2,
      _ => 0,
    };
    set => SkillRegistryTarget = value switch
    {
      1 => "global",
      2 => "workspace",
      _ => string.Empty,
    };
  }

  /// <summary>The watchdog tick interval as raw text — blank means the watchdog
  ///     default; a non-blank value must be a positive constant-format duration
  ///     (a bare integer is rejected: TimeSpan would read it as days).</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string WatchdogTickText { get; set; }

  /// <summary>The watchdog idle threshold as raw text — same rule as the tick
  ///     interval.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string WatchdogIdleText { get; set; }

  /// <summary>The maximum watchdog wrap-up attempts as raw text — blank means the
  ///     watchdog default; a non-blank value must be a non-negative integer.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string WatchdogWrapUpText { get; set; }

  /// <summary>The OpenRouter base URL as raw text — hosts remember exactly what the
  ///     user typed; a non-blank value must parse as an absolute URI.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ValidationError))]
  [NotifyPropertyChangedFor(nameof(CanSave))]
  public partial string OpenRouterBaseUrlText { get; set; }

  [ObservableProperty]
  public partial CommitStyleOption SelectedCommitStyle { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(KeyPasswordChar))]
  public partial bool KeysVisible { get; set; }

  /// <summary>The global session-file rows: what loads (when checked) for every
  ///     workspace. Editable in place - checkbox toggles, row remove.</summary>
  public ObservableCollection<SessionFileRow> GlobalFiles { get; } = [];

  /// <summary>The workspace the settings dialog is editing for: null when no
  ///     workspace is open - then the workspace scope is INERT (no rows, no add,
  ///     nothing persisted under a blank key).</summary>
  public string? WorkspaceRoot { get; }

  /// <summary>Whether a workspace is open; drives the workspace section's visibility.</summary>
  public bool HasWorkspace => WorkspaceRoot is not null;

  /// <summary>The workspace-scope session-file rows: what loads for THIS workspace.</summary>
  public ObservableCollection<SessionFileRow> WorkspaceFiles { get; } = [];

  /// <summary>The global skill-directory rows: what the skill engine scans for every
  ///     workspace. Editable in place - checkbox toggles, row remove.</summary>
  public ObservableCollection<SessionFileRow> GlobalSkillDirectories { get; } = [];

  /// <summary>The workspace-scope skill-directory rows: what the skill engine scans
  ///     for THIS workspace (a directory equal to a global one is skipped at the
  ///     factory's resolution site).</summary>
  public ObservableCollection<SessionFileRow> WorkspaceSkillDirectories { get; } = [];

  /// <summary>Entry field for a new global file path; Add validates it.</summary>
  [ObservableProperty]
  public partial string NewGlobalFile { get; set; } = string.Empty;

  /// <summary>Entry field for a new workspace file path; Add validates it.</summary>
  [ObservableProperty]
  public partial string NewWorkspaceFile { get; set; } = string.Empty;

  /// <summary>Entry field for a new global skill-directory path; Add validates it.</summary>
  [ObservableProperty]
  public partial string NewGlobalSkillDirectory { get; set; } = string.Empty;

  /// <summary>Entry field for a new workspace skill-directory path; Add validates it.</summary>
  [ObservableProperty]
  public partial string NewWorkspaceSkillDirectory { get; set; } = string.Empty;

  /// <summary>Adds a validated global row. A relative path is a named, shown error -
  ///     never a silent coercion (strict boundaries).</summary>
  [RelayCommand]
  private void AddGlobalFile()
  {
    if (TryAddFile(NewGlobalFile, GlobalFiles))
    {
      NewGlobalFile = string.Empty;
    }
  }

  /// <summary>Adds a validated workspace-scope row.</summary>
  [RelayCommand]
  private void AddWorkspaceFile()
  {
    if (!HasWorkspace)
    {
      FileError = null;
      InfoMessage = "Open a workspace to configure its session files.";
      return;
    }

    if (TryAddFile(NewWorkspaceFile, WorkspaceFiles))
    {
      NewWorkspaceFile = string.Empty;
    }
  }

  [RelayCommand]
  private void RemoveGlobalFile(SessionFileRow row) => _ = GlobalFiles.Remove(row);
  [RelayCommand]
  private void RemoveWorkspaceFile(SessionFileRow row) => _ = WorkspaceFiles.Remove(row);

  /// <summary>Adds a validated global skill-directory row.</summary>
  [RelayCommand]
  private void AddGlobalSkillDirectory()
  {
    if (TryAddDirectory(NewGlobalSkillDirectory, GlobalSkillDirectories))
    {
      NewGlobalSkillDirectory = string.Empty;
    }
  }

  /// <summary>Adds a validated workspace-scope skill-directory row.</summary>
  [RelayCommand]
  private void AddWorkspaceSkillDirectory()
  {
    if (!HasWorkspace)
    {
      FileError = null;
      InfoMessage = "Open a workspace to configure its skill directories.";
      return;
    }

    if (TryAddDirectory(NewWorkspaceSkillDirectory, WorkspaceSkillDirectories))
    {
      NewWorkspaceSkillDirectory = string.Empty;
    }
  }

  [RelayCommand]
  private void RemoveGlobalSkillDirectory(SessionFileRow row) => _ = GlobalSkillDirectories.Remove(row);
  [RelayCommand]
  private void RemoveWorkspaceSkillDirectory(SessionFileRow row) => _ = WorkspaceSkillDirectories.Remove(row);
  /// <summary>The mask the settings window applies to both key fields; null-mask char
  ///     when revealed.</summary>
  public char KeyPasswordChar => KeysVisible ? default : '•';

  /// <summary>Save is only actionable when both fields validate.</summary>
  public bool CanSave => ValidationError is null;

  /// <summary>The first validation problem across all fields, or null when clean.</summary>
  public string? ValidationError =>
      FileError ?? Validate(OpenRouterKey)
      ?? ValidateMaxConcurrent(MaxConcurrentAgentsText)
      ?? ValidateDuration(WatchdogTickText, "Tick interval")
      ?? ValidateDuration(WatchdogIdleText, "Idle threshold")
      ?? ValidateWrapUp(WatchdogWrapUpText)
      ?? ValidateLabeledBaseUrl(OpenRouterBaseUrlText, "OpenRouter base URL");

  public SettingsViewModel(string? openRouterKey, CommitStyle commitStyle = CommitStyle.Conventional,
      IReadOnlyList<CompactionModelOption>? compactionModels = null,
      CompactionModelOption? selectedCompactionModel = null,
      IReadOnlyList<SessionFileEntry>? globalFiles = null,
      IReadOnlyList<SessionFileEntry>? workspaceFiles = null,
      string? workspaceRoot = null,
      string? maxConcurrentAgentsText = null, string? defaultModelText = null, bool remoteHost = false,
      string? watchdogTickText = null, string? watchdogIdleText = null, string? watchdogWrapUpText = null,
      string? openRouterBaseUrlText = null,
      bool computerUse = false,
      IReadOnlyList<SessionFileEntry>? globalSkillDirectories = null,
      IReadOnlyList<SessionFileEntry>? workspaceSkillDirectories = null,
      string? skillRegistryDefaultTarget = null)
  {
    // The command exists before the observable properties: setting those raises
    // the changed hooks, which requery save availability. The guard in the action
    // is load-bearing: ICommand.Execute does not consult CanExecute, and a disabled
    // button is only one of several ways this command can be invoked.
    SaveCommand = new RelayCommand(
        () =>
        {
          if (CanSave)
          {
            SaveRequested?.Invoke(this, new SettingsUpdate(
                Normalize(OpenRouterKey), SelectedCommitStyle.Style,
                SelectedCompactionModel.ModelId, null,
                GlobalFiles: [.. GlobalFiles], WorkspaceFiles: HasWorkspace ? [.. WorkspaceFiles] : null,
                WorkspaceRoot: WorkspaceRoot,
                Normalize(MaxConcurrentAgentsText), Normalize(DefaultModelText), RemoteHost,
                Normalize(WatchdogTickText), Normalize(WatchdogIdleText), Normalize(WatchdogWrapUpText),
                Normalize(OpenRouterBaseUrlText), ComputerUse,
                GlobalSkillDirectories: [.. GlobalSkillDirectories],
                WorkspaceSkillDirectories: HasWorkspace ? [.. WorkspaceSkillDirectories] : null,
                SkillRegistryDefaultTarget: SkillRegistryTarget));
          }
        },
        () => CanSave);
    CompactionModels = compactionModels ?? [CompactionModelOption.Automatic];
    OpenRouterKey = openRouterKey ?? string.Empty;
    MaxConcurrentAgentsText = maxConcurrentAgentsText ?? string.Empty;
    DefaultModelText = defaultModelText ?? string.Empty;
    RemoteHost = remoteHost;
    ComputerUse = computerUse;
    SkillRegistryTarget = skillRegistryDefaultTarget ?? string.Empty;
    WatchdogTickText = watchdogTickText ?? string.Empty;
    WatchdogIdleText = watchdogIdleText ?? string.Empty;
    WatchdogWrapUpText = watchdogWrapUpText ?? string.Empty;
    OpenRouterBaseUrlText = openRouterBaseUrlText ?? string.Empty;
    SelectedCompactionModel = selectedCompactionModel ?? CompactionModelOption.Automatic;
    foreach (SessionFileEntry entry in globalFiles ?? [])
    {
      GlobalFiles.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    WorkspaceRoot = workspaceRoot;
    foreach (SessionFileEntry entry in workspaceRoot is null ? [] : workspaceFiles ?? [])
    {
      WorkspaceFiles.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (SessionFileEntry entry in globalSkillDirectories ?? [])
    {
      GlobalSkillDirectories.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }

    foreach (SessionFileEntry entry in workspaceRoot is null ? [] : workspaceSkillDirectories ?? [])
    {
      WorkspaceSkillDirectories.Add(new SessionFileRow(entry.Path, entry.Enabled));
    }
    SelectedCommitStyle = commitStyle switch
    {
      CommitStyle.Conventional => CommitStyleOption.Conventional,
      CommitStyle.Gitmoji => CommitStyleOption.Gitmoji,
      CommitStyle.None => CommitStyleOption.None,
      _ => CommitStyleOption.Conventional, // unnamed enum values cannot occur across the typed boundary
    };
  }

  // Validation edits must requery the Save button's CanExecute.
  partial void OnOpenRouterKeyChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnMaxConcurrentAgentsTextChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnDefaultModelTextChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnRemoteHostChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnWatchdogTickTextChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnWatchdogIdleTextChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnWatchdogWrapUpTextChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  partial void OnOpenRouterBaseUrlTextChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

  /// <summary>Validates one entered path and appends a checked row. A relative path
  ///     fails into <see cref="FileError"/> - shown where every other validation
  ///     error shows - and nothing is added.</summary>
  private bool TryAddFile(string entered, ObservableCollection<SessionFileRow> rows)
  {
    string trimmed = entered.Trim();
    if (trimmed.Length == 0)
    {
      return false;
    }

    if (!Path.IsPathRooted(trimmed))
    {
      FileError = $"Session file paths must be absolute: '{trimmed}' is relative.";
      return false;
    }

    FileError = null;
    rows.Add(new SessionFileRow(trimmed, Enabled: true));
    return true;
  }

  /// <summary>Validates one entered directory path and appends a checked row. The
  ///     same absolute-path rule the file editor applies (the skill engine rejects
  ///     anything else); a relative path fails into <see cref="FileError"/> - shown
  ///     where every other validation error shows - and nothing is added.</summary>
  private bool TryAddDirectory(string entered, ObservableCollection<SessionFileRow> rows)
  {
    string trimmed = entered.Trim();
    if (trimmed.Length == 0)
    {
      return false;
    }

    if (!Path.IsPathRooted(trimmed))
    {
      FileError = $"Directory paths must be absolute: '{trimmed}' is relative.";
      return false;
    }

    FileError = null;
    rows.Add(new SessionFileRow(trimmed, Enabled: true));
    return true;
  }

  /// <summary>Non-blocking status for the file section (e.g. the workspace scope
  ///     is inert because no workspace is open). Never blocks saving.</summary>
  [ObservableProperty]
  public partial string? InfoMessage { get; set; }

  /// <summary>The named problem with the last file-add attempt, or null. Rendered
  ///     beside the file lists so the user sees why an add was refused.</summary>
  [ObservableProperty]
  public partial string? FileError { get; set; }

  /// <summary>Returns the validation problem with <paramref name="key"/>, or null when
  ///     it is a legal entry: blank (cleared), or a trimmed non-empty value with no
  ///     internal whitespace.</summary>
  private static string? Validate(string key)
  {
    string trimmed = key.Trim();
    if (trimmed.Length == 0)
    {
      return null; // blank clears the key — legal
    }

    return trimmed.Any(char.IsWhiteSpace)
        ? "API keys cannot contain whitespace."
        : null;
  }

  /// <summary>Returns the validation problem with a labeled base-URL text, or null
  ///     when it is a legal entry: blank (cleared), or an absolute URI — the same rule
  ///     the loader's <see cref="AgentSettingsLoader.BindBaseUrl"/> enforces at load
  ///     time, surfaced here so the error shows before the dialog closes. Every
  ///     provider base-URL field shares it.</summary>
  private static string? ValidateLabeledBaseUrl(string text, string label)
  {
    string trimmed = text.Trim();
    if (trimmed.Length == 0)
    {
      return null; // blank clears the base URL — legal (provider default applies)
    }

    return Uri.TryCreate(trimmed, UriKind.Absolute, out _)
        ? null
        : $"{label} is not a valid absolute URI: '{trimmed}'.";
  }

  /// <summary>Returns the validation problem with the max-concurrent-agents text, or
  ///     null when it is a legal entry: blank (the shipped default of 4 applies), or a
  ///     positive integer — the same rule
  ///     <see cref="SubAgentConfiguration.Bind"/> enforces at load time.</summary>
  private static string? ValidateMaxConcurrent(string text)
  {
    string trimmed = text.Trim();
    if (trimmed.Length == 0)
    {
      return null; // blank = shipped default (4)
    }

    bool legal = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1;
    return legal
        ? null
        : "Max concurrent agents must be a positive integer.";
  }

  /// <summary>Returns the validation problem with a watchdog duration text, or null
  ///     when it is a legal entry: blank (the watchdog default applies), or a positive
  ///     constant-format duration. A bare integer is rejected by name — TimeSpan would
  ///     silently read it as DAYS — the same named decision
  ///     <see cref="SubAgentConfiguration.BindWatchdog"/> makes at load time.</summary>
  private static string? ValidateDuration(string text, string label)
  {
    string trimmed = text.Trim();
    if (trimmed.Length == 0)
    {
      return null; // blank = watchdog default
    }

    bool bareInteger = trimmed.All(char.IsDigit);
    if (bareInteger)
    {
      return $"{label} must carry units (e.g. '00:00:02'); the bare integer '{trimmed}' would bind as days.";
    }

    bool legal = TimeSpan.TryParseExact(trimmed, ["c", "g"], CultureInfo.InvariantCulture,
        TimeSpanStyles.None, out TimeSpan d) && d > TimeSpan.Zero;
    return legal
        ? null
        : $"{label} must be a positive duration in constant format (e.g. '00:00:02').";
  }

  /// <summary>Returns the validation problem with the wrap-up-attempts text, or null
  ///     when it is a legal entry: blank (the watchdog default applies), or a
  ///     non-negative integer — the same rule
  ///     <see cref="SubAgentConfiguration.BindWatchdog"/> enforces at load time.</summary>
  private static string? ValidateWrapUp(string text)
  {
    string trimmed = text.Trim();
    if (trimmed.Length == 0)
    {
      return null;
    }

    bool legal = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0;
    return legal
        ? null
        : "Max wrap-up attempts must be a non-negative integer.";
  }

  private static string? Normalize(string key)
  {
    string trimmed = key.Trim();
    return trimmed.Length == 0 ? null : trimmed;
  }
}
