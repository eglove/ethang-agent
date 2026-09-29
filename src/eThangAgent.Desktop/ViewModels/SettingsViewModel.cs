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

internal sealed record SettingsUpdate(string? OpenRouterApiKey, CommitStyle CommitStyle,
    string? CompactionModelId = null, string? CompactionWorkspaceKey = null,
    string? MaxConcurrentAgentsText = null, string? DefaultModelText = null, bool RemoteHost = false,
    string? WatchdogTickText = null, string? WatchdogIdleText = null, string? WatchdogWrapUpText = null,
    string? OpenRouterBaseUrlText = null,
    bool ComputerUse = false,
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

  /// <summary>Session files and skill directories moved to the Open Workspace
  ///     dialog (the launch surface): the settings modal no longer edits them,
  ///     so the workspace scope (and its inert-without-workspace state) is gone
  ///     with them.</summary>
  public string? WorkspaceRoot { get; }

  /// <summary>Whether a workspace is open; kept for the compaction model's
  ///     per-workspace capture (the dialog opened from a tab edits that
  ///     workspace's summarizer).</summary>
  public bool HasWorkspace => WorkspaceRoot is not null;

  /// <summary>The mask the settings window applies to both key fields; null-mask char
  ///     when revealed.</summary>
  public char KeyPasswordChar => KeysVisible ? default : '•';

  /// <summary>Save is only actionable when both fields validate.</summary>
  public bool CanSave => ValidationError is null;

  /// <summary>The first validation problem across all fields, or null when clean.</summary>
  public string? ValidationError =>
      Validate(OpenRouterKey)
      ?? ValidateMaxConcurrent(MaxConcurrentAgentsText)
      ?? ValidateDuration(WatchdogTickText, "Tick interval")
      ?? ValidateDuration(WatchdogIdleText, "Idle threshold")
      ?? ValidateWrapUp(WatchdogWrapUpText)
      ?? ValidateLabeledBaseUrl(OpenRouterBaseUrlText, "OpenRouter base URL");

  public SettingsViewModel(string? openRouterKey, CommitStyle commitStyle = CommitStyle.Conventional,
      IReadOnlyList<CompactionModelOption>? compactionModels = null,
      CompactionModelOption? selectedCompactionModel = null,
      string? workspaceRoot = null,
      string? maxConcurrentAgentsText = null, string? defaultModelText = null, bool remoteHost = false,
      string? watchdogTickText = null, string? watchdogIdleText = null, string? watchdogWrapUpText = null,
      string? openRouterBaseUrlText = null,
      bool computerUse = false,
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
                Normalize(MaxConcurrentAgentsText), Normalize(DefaultModelText), RemoteHost,
                Normalize(WatchdogTickText), Normalize(WatchdogIdleText), Normalize(WatchdogWrapUpText),
                Normalize(OpenRouterBaseUrlText), ComputerUse,
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
    WorkspaceRoot = workspaceRoot;
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
