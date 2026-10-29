using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Mcp;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.ViewModels;

internal delegate Task<Result<string>> TurnRunner(
    SendMessageCommand command,
    CancellationToken ct,
    TurnCallbacks? callbacks,
    Action<string>? onNotice = null);

/// <summary>Session creation WITH a conversation seed (issue #234): the compaction handoff
///     seeds the new session with its summary, so the seeded shape needs its own seam — the
///     plain creation delegate cannot express it. A host that supplies only the plain
///     delegate keeps every other path working and reports the seeded launch as
///     unavailable, which is honest: it has no store to seed.</summary>
internal delegate Task<Result<AgentSession>> SeededSessionCreator(
    string workspaceRoot,
    string providerName,
    IReadOnlyList<Message>? seed);

/// <summary>Optional shell configuration for <see cref="MainViewModel"/>. Every member
///     is an optional seam or default override whose null behaves exactly as the
///     former absent parameter: providers fall back to the built-in list, the dialog's
///     pre-selection to the first configured provider, the preference store and key
///     protector remember/persist nothing, the stream sink falls back to the session
///     view-model's own marshaling, and a null settings/factory pair disables settings
///     editing (prebuilt-session hosts own their configuration).</summary>
internal sealed record MainViewModelOptions
{
  /// <summary>Host hook fired when a session tab opens (after Tabs.Add): the watchdog host
  ///     attaches the session's container-scoped watchdog here. Null: no-op (tests, hosts
  ///     without a watchdog).</summary>
  public Action<AgentSession>? SessionOpened { get; init; }

  /// <summary>Host hook fired when a session tab closes, before the container is disposed.</summary>
  public Action<AgentId>? SessionClosed { get; init; }

  /// <summary>Providers the dialog offers when no settings snapshot is injected;
  ///     ignored (derived from the snapshot) otherwise.</summary>
  public IReadOnlyList<ProviderOption>? AvailableProviders { get; init; }

  /// <summary>Provider the dialog pre-selects; falls back to the first configured
  ///     one when absent or no longer configured.</summary>
  public string? PreferredProviderId { get; init; }

  /// <summary>Optional preference store persisting the last-chosen provider and the
  ///     protected API keys (test seam: when null, opens still work and nothing is
  ///     remembered).</summary>
  public IAppPreferenceStore? Preferences { get; init; }

  /// <summary>Commit style the settings modal prefills; the host loads it from the
  ///     preference store at startup (async) and passes it in. Null (hosts that load
  ///     nothing) means the Conventional default — the same one the tool side uses.</summary>
  public CommitStyle? CommitStyle { get; init; }

  /// <summary>Whether a compaction hands off to a new session (issue #234), as loaded from
  ///     the preference store at startup — the settings modal's prefill. Null means unset,
  ///     which is OFF (the shipped default). The composition reads the LIVE preference at
  ///     every compaction; this only seeds the checkbox.</summary>
  public bool? CompactionHandoff { get; init; }

  /// <summary>Optional stream-sink override for every opened session (test seam).
  ///     When null, production self-marshaling applies per session view-model.</summary>
  public Func<UiStreamEvent, Task>? UiStreamSink { get; init; }

  /// <summary>Current settings snapshot; enables settings editing.</summary>
  public AgentSettings? Settings { get; init; }

  /// <summary>Factory rebound when saved keys change; pairs with
  ///     <see cref="Settings"/>.</summary>
  public AgentSessionFactory? SessionFactory { get; init; }

  /// <summary>Protects keys before they reach durable storage. When null (tests),
  ///     key saves are not persisted at all.</summary>
  public IApiKeyProtector? ApiKeyProtector { get; init; }

  /// <summary>Lists resumable root sessions for the Sessions dialog. When null (hosts
  ///     that compose sessions without a shared store), the Sessions entry reports the
  ///     catalog as unavailable.</summary>
  public SessionCatalogQueryHandler? SessionCatalog { get; init; }

  /// <summary>Session creation that can SEED the new conversation (issue #234): the
  ///     compaction handoff seeds the new session with its summary. Null falls back to the
  ///     plain creation delegate with no seed, which serves every non-handoff path; a
  ///     handoff then reports the seeded launch as unavailable instead of silently
  ///     dropping the summary it exists to carry.</summary>
  public SeededSessionCreator? SeededSessionCreator { get; init; }
}

/// <summary>Shell-level state for the main window: the left menu bar (Open Workspace,
///     Sessions, the per-tab Model and Effort entries, and the bottom-anchored Settings
///     entry) and the open agent tabs. 'Open Workspace' shows the new-agent dialog (provider
///     dropdown plus workspace picker); each tab owns an <see cref="AgentSessionViewModel"/>
///     bound to its own isolated <see cref="AgentSession"/> created through the injected
///     provider-aware session-factory hook. 'Sessions' lists every persisted root session
///     (newest first, already-open ones greyed); confirming one resumes it — the persisted
///     transcript replays into the tab — while a plain open always starts a FRESH session
///     (never an automatic resume). A workspace holds many sessions; resume targets one
///     session id. The Model entry shows the selected tab's model picker and the Effort
///     entry its effort picker: confirming applies the choice to the session (from the
///     next turn, root and children alike) and persists it per workspace + provider, so
///     reopening the same directory restores it. The settings modal edits the provider
///     API keys: saving persists them (protected) and rebuilds the factory so future
///     opens use the new keys — already-open tabs keep the credentials they were created
///     with. The shell itself holds no agent state. A static
///     <see cref="ForPrebuiltSessionAsync"/> keeps single-session hosts and tests simple
///     while tabs remain the primary surface.</summary>
internal sealed partial class MainViewModel : ObservableObject
{
  // The delegate reads _sessionFactory at invocation time, so a settings-save rebind
  // reaches future opens without touching this field.
  private readonly Func<string, string, Task<Result<AgentSession>>> _createSession;

  /// <summary>Creation WITH a conversation seed (issue #234). Derived from the same
  ///     factory as <see cref="_createSession"/> when one is wired, so a settings-save
  ///     rebind reaches it too; a host that supplied only a plain creation delegate has
  ///     none, and a handoff then reports the seeded launch as unavailable.</summary>
  private readonly SeededSessionCreator? _createSeededSession;

  /// <summary>Optional preference store persisting the last-chosen provider and the
  ///     protected API keys (test seam: when null, opens still work and nothing is
  ///     remembered).</summary>
  private readonly IAppPreferenceStore? _preferences;

  /// <summary>Optional stream-sink override for every opened session (test seam).
  ///     When null, production self-marshaling applies per session view-model.</summary>
  private readonly Func<UiStreamEvent, Task>? _streamSink;

  /// <summary>Optional API-key protector guarding durable storage (test seam: when
  ///     null, keys are never persisted — plaintext must never reach the store).</summary>
  private readonly IApiKeyProtector? _keyProtector;

  /// <summary>Current settings snapshot. Null together with a null factory disables
  ///     settings editing (prebuilt-session hosts own their configuration).</summary>
  private AgentSettings? _settings;

  /// <summary>Optional host hooks for the watchdog host: attach/detach per-session
  ///     watchdogs as tabs open and close. Null (tests, hosts without one): no-op.</summary>
  private readonly Action<AgentSession>? _sessionOpened;
  private readonly Action<AgentId>? _sessionClosed;

  /// <summary>Factory rebound via <see cref="AgentSessionFactory.WithSettings"/> when
  ///     saved keys change; the creation delegate always reads the current instance.</summary>
  private AgentSessionFactory? _sessionFactory;

  /// <summary>Lists resumable sessions for the Sessions dialog (null when the host
  ///     has no shared store — the dialog then reports the catalog unavailable).</summary>
  private readonly SessionCatalogQueryHandler? _sessionCatalog;

  /// <summary>In-flight in-place replacements (issue #234), keyed by the tab they will
  ///     replace. A session-request launch is answered IMMEDIATELY but swapped only when the
  ///     requesting turn goes idle, so the pending session needs a home in between; the map
  ///     is also the one-replacement-per-tab guard (two launches racing for one slot would
  ///     orphan one session in no tab at all).</summary>
  private readonly Dictionary<AgentTabViewModel, AgentSession> _pendingReplacements = [];

  /// <summary>Resume hook mirroring the creation delegate: derived from the factory so
  ///     saved keys rebind future resumes; a host without a factory gets a structured
  ///     ResumeUnavailable failure instead of a crash.</summary>
  private readonly Func<AgentId, Task<Result<AgentSession>>> _resumeSession;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HasSelectedTab))]
  public partial AgentTabViewModel? SelectedTab { get; set; }

  [ObservableProperty]
  public partial bool IsOpeningAgent { get; set; }

  /// <summary>Providers offered by the new-agent dialog: those with a configured key.
  ///     Refreshed when the settings modal saves new keys.</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HasConfiguredProvider))]
  public partial IReadOnlyList<ProviderOption> AvailableProviders { get; set; }

  /// <summary>Provider the new-agent dialog pre-selects (last persisted choice).</summary>
  [ObservableProperty]
  public partial string PreferredProviderId { get; set; }

  public ObservableCollection<AgentTabViewModel> Tabs { get; } = [];

  /// <summary>The app-wide memory indicator (spec D14): usage against the derived
  ///     limit, the largest consumers, and the decisions the governor has taken.
  ///     App-wide, because the budget is app-wide: one indicator serves every
  ///     conversation, and it is READ-ONLY - the user declined a manual reclaim, so
  ///     the governor owns the ladder alone.</summary>
  public ResourceIndicatorViewModel Resource { get; } = new();

  /// <summary>How many conversations are open right now. The governor's per-
  ///     conversation share is derived from this count, so the indicator and the
  ///     governor always agree on how the budget is split.</summary>
  public int OpenConversationCount => Tabs.Count;

  /// <summary>Whether the indicator's details (the consumers and the governor's
  ///     decisions) are shown. A toggle in the same bar, not a separate panel: the
  ///     data is two short lists, and a window would be more chrome than it deserves.</summary>
  [ObservableProperty]
  public partial bool IsResourceDetailsOpen { get; set; }

  /// <summary>Shows or hides the indicator's details. A VIEW toggle only - it changes
  ///     what is on screen and nothing else. The governor's decisions are read-only,
  ///     so no command here can act on the governor.</summary>
  public IRelayCommand ToggleResourceDetailsCommand { get; }


  public bool HasTabs => Tabs.Count > 0;

  /// <summary>True when a tab is selected — drives the Model menu entry's visibility
  ///     (every provider has a model picker).</summary>
  public bool HasSelectedTab => SelectedTab is not null;

  /// <summary>True when at least one provider has a configured API key; gates Open Workspace.</summary>
  public bool HasConfiguredProvider => AvailableProviders.Count > 0;

  /// <summary>Keys currently configured — the settings dialog's prefill (null when
  ///     no key is set or settings editing is disabled).</summary>
  public string? ConfiguredOpenRouterKey => _settings?.OpenRouter.ApiKey;

  /// <summary>The configured OpenRouter management key - the settings modal's
  ///     prefill (null when unset or settings editing is disabled).</summary>
  public string? ConfiguredOpenRouterManagementKey => _settings?.OpenRouter.ManagementKey;

  /// <summary>The max-concurrent-agents text the settings modal prefills: the loaded
  ///     value (the shipped default of 4 when no preference is stored). Null when no
  ///     settings snapshot exists.</summary>
  public string? ConfiguredMaxConcurrentAgents =>
      _settings?.SubAgents.MaxConcurrentAgents.ToString(CultureInfo.InvariantCulture);

  /// <summary>The default sub-agent model the settings modal prefills (null when
  ///     unset — spawns must then pass a model explicitly).</summary>
  public string? ConfiguredDefaultModel => _settings?.SubAgents.DefaultModel;

  /// <summary>The watchdog tick-interval text the settings modal prefills in constant
  ///     format (null when the knob is unset — the watchdog default applies).</summary>
  /// <summary>The configured root-turn idle bound as constant-format text, or null when
  ///     unconfigured (the runtime default applies).</summary>
  public string? ConfiguredRootTurnIdle => _settings?.SubAgents.RootTurnBudget?.Idle.ToString("c");

  public string? ConfiguredWatchdogTick => _settings?.Watchdog?.TickInterval?.ToString("c");
  /// <summary>The watchdog idle-threshold text the settings modal prefills in
  ///     constant format (null when the knob is unset).</summary>
  public string? ConfiguredWatchdogIdle => _settings?.Watchdog?.IdleThreshold?.ToString("c");

  /// <summary>The watchdog wrap-up-attempts text the settings modal prefills (null
  ///     when the knob is unset).</summary>
  public string? ConfiguredWatchdogWrapUp =>
      _settings?.Watchdog?.MaxWrapUpAttempts?.ToString(CultureInfo.InvariantCulture);

  /// <summary>The OpenRouter base URL the settings modal prefills — the configured
  ///     absolute URI, or the provider default when none is stored.</summary>
  public string? ConfiguredOpenRouterBaseUrl => _settings?.OpenRouter.BaseUrl.ToString();

  public bool ConfiguredComputerUse => _settings?.ComputerUse ?? false;

  /// <summary>The compaction-handoff choice the settings modal prefills (issue #234).
  ///     Read from the STORE, not from <see cref="_settings"/>: the handoff is a live
  ///     preference the composition reads at every compaction, not a container-build
  ///     setting, so it has no place in the settings snapshot. Off when unset — the
  ///     shipped default. Loaded at construction and updated on save.</summary>
  public bool ConfiguredCompactionHandoff { get; private set; }

  /// <summary>The commit style the settings modal prefills; the stored choice, or the
  ///     Conventional default when none is stored (matching the tool-side default).
  ///     Loaded from preferences at construction; updated when settings save.</summary>
  public CommitStyle ConfiguredCommitStyle { get; private set; }

  public IRelayCommand OpenAgentCommand { get; }

  public IRelayCommand OpenSessionsCommand { get; }

  public IRelayCommand OpenSettingsCommand { get; }

  public IRelayCommand ChooseModelSettingsCommand { get; }

  public IRelayCommand OpenMcpServersCommand { get; }

  /// <summary>Raised when the shell wants the new-agent dialog shown.</summary>
  public event EventHandler? OpenAgentRequested;

  /// <summary>Raised when the shell wants the Sessions dialog shown.</summary>
  public event EventHandler? SessionsRequested;

  /// <summary>Raised when the shell wants the settings modal shown.</summary>
  public event EventHandler? SettingsRequested;

  /// <summary>Raised when the shell wants the selected tab's Model Settings window shown.</summary>
  public event EventHandler? ModelSettingsRequested;

  /// <summary>Raised when the shell wants the MCP servers dialog shown (issue #106).</summary>
  public event EventHandler? McpServersRequested;

  /// <param name="createSession">Session-creation hook. Null in production — the shell
  ///     derives it from <paramref name="options"/> so saved keys can rebuild it; hosts
  ///     and tests that compose sessions themselves pass their own delegate and forgo
  ///     settings editing.</param>
  /// <param name="options">Optional shell configuration: the offered providers, the
  ///     dialog's pre-selected provider, the preference store, the shell-level stream
  ///     sink, the settings snapshot, the rebinding session factory, and the key
  ///     protector. Each null member behaves as the corresponding absent parameter
  ///     always did.</param>
  public MainViewModel(Func<string, string, Task<Result<AgentSession>>>? createSession,
      MainViewModelOptions? options = null)
  {
    EnsureSessionSource(createSession, options);

    // Each member is an optional seam: a null one behaves exactly as the
    // corresponding absent parameter always did.
    _preferences = options?.Preferences;
    SeedSettingsPrefills(options);
    _streamSink = options?.UiStreamSink;
    _settings = options?.Settings;
    _sessionOpened = options?.SessionOpened;
    _sessionClosed = options?.SessionClosed;
    _sessionFactory = options?.SessionFactory;
    _keyProtector = options?.ApiKeyProtector;
    _sessionCatalog = options?.SessionCatalog;
    _createSession = CreateSessionDelegate(createSession);
    _createSeededSession = SeededDelegate(options, createSession);
    _resumeSession = CreateResumeDelegate();

    // Commands exist before the observable properties: setting those raises the
    // changed hooks, which requery command availability.
    OpenAgentCommand = CreateOpenAgentCommand();
    OpenSessionsCommand = CreateOpenSessionsCommand();
    OpenSettingsCommand = CreateOpenSettingsCommand();
    ChooseModelSettingsCommand = CreateChooseModelSettingsCommand();
    OpenMcpServersCommand = CreateOpenMcpServersCommand();
    ToggleResourceDetailsCommand = new RelayCommand(() => IsResourceDetailsOpen = !IsResourceDetailsOpen);

    InitializeProviders(options);
    Tabs.CollectionChanged += OnTabsChanged;
  }

  /// <summary>Refuses a shell with no session source at all: neither a creation
  ///     delegate nor the settings/factory pair settings editing needs.</summary>
  private static void EnsureSessionSource(Func<string, string, Task<Result<AgentSession>>>? createSession,
      MainViewModelOptions? options)
  {
    if (createSession is null && (options?.Settings is null || options.SessionFactory is null))
    {
      throw new ArgumentException(
          "Either a session-creation delegate or both settings and a session factory are required.",
          nameof(createSession));
    }
  }

  /// <summary>Seeds the settings modal's prefills the host loaded from the preference store
  ///     at startup: the commit style (Conventional when the host loaded nothing — the same
  ///     default the tool side uses) and the compaction-handoff choice (off when unset, the
  ///     shipped default).</summary>
  private void SeedSettingsPrefills(MainViewModelOptions? options)
  {
    ConfiguredCommitStyle = options?.CommitStyle ?? CommitStyle.Conventional;
    ConfiguredCompactionHandoff = options?.CompactionHandoff ?? false;
  }

  /// <summary>The session-creation delegate: the host-supplied one, else the factory's
  ///     create. It reads the factory at invocation, so a settings-save rebind reaches
  ///     future opens without touching the stored delegate.</summary>
  private Func<string, string, Task<Result<AgentSession>>> CreateSessionDelegate(
      Func<string, string, Task<Result<AgentSession>>>? createSession)
      => createSession ?? ((root, provider) => _sessionFactory!.CreateAsync(root, provider));

  /// <summary>The seeded creation delegate (issue #234): the host-supplied one, else the
  ///     factory's seeded create read at invocation (so a settings-save rebind reaches it).
  ///     A host that supplied its own plain creation delegate has no seeded path at all
  ///     (null): the caller reports the seeded launch as unavailable rather than silently
  ///     dropping the handoff summary it exists to carry.</summary>
  private SeededSessionCreator? SeededDelegate(MainViewModelOptions? options,
      Func<string, string, Task<Result<AgentSession>>>? createSession)
      => options?.SeededSessionCreator
          ?? (createSession is not null
              ? null
              : (root, provider, seed) => _sessionFactory!.CreateAsync(root, provider, seed));

  /// <summary>The resume delegate: the factory's resume, read at invocation so a
  ///     settings-save rebind reaches future resumes. A host composing sessions itself
  ///     (no factory) degrades to a structured failure — resume needs the shared store
  ///     the factory owns.</summary>
  private Func<AgentId, Task<Result<AgentSession>>> CreateResumeDelegate()
      => sessionId => _sessionFactory is null
          ? Task.FromResult(Result.Failure<AgentSession>(new DomainError("ResumeUnavailable",
              "this host composed its sessions without a shared store; sessions cannot be resumed.")))
          : _sessionFactory.ResumeAsync(sessionId);

  /// <summary>The Open Workspace command: gated on no open in flight and at least one
  ///     configured provider.</summary>
  private RelayCommand CreateOpenAgentCommand() => new(
      () => OpenAgentRequested?.Invoke(this, EventArgs.Empty),
      () => !IsOpeningAgent && HasConfiguredProvider);

  private RelayCommand CreateOpenSessionsCommand() => new(
      () => SessionsRequested?.Invoke(this, EventArgs.Empty));

  private RelayCommand CreateOpenSettingsCommand() => new(
      () => SettingsRequested?.Invoke(this, EventArgs.Empty));

  /// <summary>The Model entry: gated on a selected tab (every provider has a picker).</summary>
  private RelayCommand CreateChooseModelSettingsCommand() => new(
      () => ModelSettingsRequested?.Invoke(this, EventArgs.Empty),
      () => HasSelectedTab);

  /// <summary>The MCP entry: gated on a selected tab (the dialog is per session).</summary>
  private RelayCommand CreateOpenMcpServersCommand() => new(
      () => McpServersRequested?.Invoke(this, EventArgs.Empty),
      () => HasSelectedTab);

  /// <summary>Seeds the provider surface: the configured providers (from the settings
  ///     snapshot when present, else the host-supplied list) and the pre-selected one.</summary>
  private void InitializeProviders(MainViewModelOptions? options)
  {
    AvailableProviders = _settings is not null
        ? ProvidersFrom(_settings)
        : options?.AvailableProviders ?? [new(Providers.OpenRouter, Providers.DisplayName(Providers.OpenRouter))];
    PreferredProviderId = ResolvePreferredProviderId(options?.PreferredProviderId);
  }

  /// <summary>Guard-style early returns: the requested provider when it is among the
  /// configured ones, else the first configured provider, else the OpenRouter default.</summary>
  private string ResolvePreferredProviderId(string? preferred)
  {
    if (preferred is not null && AvailableProviders.Any(p => p.Id == preferred))
    {
      return preferred;
    }

    bool hasProvider = AvailableProviders.Count > 0;
    return hasProvider ? AvailableProviders[0].Id : Providers.OpenRouter;
  }

  // Command availability depends on both; requery on every change.
  partial void OnIsOpeningAgentChanged(bool value) => OpenAgentCommand.NotifyCanExecuteChanged();

  partial void OnAvailableProvidersChanged(IReadOnlyList<ProviderOption> value) =>
      OpenAgentCommand.NotifyCanExecuteChanged();

  partial void OnSelectedTabChanged(AgentTabViewModel? value)
  {
    ChooseModelSettingsCommand.NotifyCanExecuteChanged();
    OpenMcpServersCommand.NotifyCanExecuteChanged();

    // Keep-alive tabs: every open tab's view stays built; IsSelected toggles the
    // visibility of exactly one. A null selection hides all.
    foreach (AgentTabViewModel tab in Tabs)
    {
      tab.IsSelected = ReferenceEquals(tab, value);
    }
  }

  private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
  {
    OnPropertyChanged(nameof(HasTabs));
    if (SelectedTab is not null && !Tabs.Contains(SelectedTab))
    {
      SelectedTab = null;
    }
  }

  /// <summary>Menu-bar entry point: raises the dialog request. The view shows the
  ///     new-agent dialog and calls <see cref="OpenAgentAsync"/> with the choices.</summary>
  public void RequestOpenAgent() => OpenAgentRequested?.Invoke(this, EventArgs.Empty);

  /// <summary>Menu-bar entry point: raises the settings request. The view shows the
  ///     settings modal and calls <see cref="ApplySettingsAsync"/> with the result.</summary>
  public void RequestOpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

  /// <summary>The selected tab's workspace key, captured when the settings modal opens
  ///     so the confirmed compaction choice lands on the right (provider, workspace).</summary>
  private string? _compactionWorkspaceKey;

  /// <summary>Builds the compaction-model picker rows for the selected tab: Automatic
  ///     plus the session provider's catalog ids. Empty catalog → Automatic only.</summary>
  public async Task<IReadOnlyList<CompactionModelOption>> GetCompactionOptionsAsync()
  {
    List<CompactionModelOption> options = [CompactionModelOption.Automatic];
    _compactionWorkspaceKey = SelectedTab?.Container.WorkspaceRoot;
    if (_settings is not null && _compactionWorkspaceKey is not null && _preferences is not null)
    {
      string? preferred = await _preferences.GetAsync(
          CompactionModelResolver.PreferenceKey(PreferredProviderId, _compactionWorkspaceKey));
      if (!string.IsNullOrWhiteSpace(preferred))
      {
        options.Add(new CompactionModelOption(preferred, preferred));
      }
    }

    return options;
  }

  /// <summary>Loads the global session-file rows for the launch dialog's prefill (E).
  ///     Unset preference renders as an empty list, never an error.</summary>
  public async Task<IReadOnlyList<SessionFileEntry>> GetGlobalSessionFilesAsync()
  {
    if (_preferences is null)
    {
      return [];
    }

    string? stored = await _preferences.GetAsync(SessionFilePreferences.GlobalKey);
    Result<IReadOnlyList<SessionFileEntry>> parsed = SessionFilePreferences.Parse(stored);
    return parsed.IsSuccess ? parsed.Value : [];
  }

  /// <summary>Loads the workspace session-file rows for the launch dialog's prefill.
  ///     The launch dialog edits the workspace it is ABOUT to open, so the prefill
  ///     comes from the caller's chosen root — null root means an empty scope.</summary>
  public async Task<IReadOnlyList<SessionFileEntry>> GetWorkspaceSessionFilesAsync(string? workspaceRoot = null)
  {
    if (_preferences is null || workspaceRoot is null)
    {
      return [];
    }

    // The root is resolved through the same guard the launch path uses:
    // an unresolvable path is a NAMED failure, not an escaping exception.
    Result<string> resolved = ResolveWorkspaceRoot(workspaceRoot);
    if (!resolved.IsSuccess)
    {
      return [];
    }

    string? stored = await _preferences.GetAsync(
        SessionFilePreferences.WorkspaceKey(resolved.Value));
    Result<IReadOnlyList<SessionFileEntry>> parsedWs = SessionFilePreferences.Parse(stored);
    return parsedWs.IsSuccess ? parsedWs.Value : [];
  }

  /// <summary>Loads the global skill-directory rows for the launch dialog's prefill.
  ///     Unset preference renders as an empty list, never an error.</summary>
  public async Task<IReadOnlyList<SessionFileEntry>> GetGlobalSkillDirectoriesAsync()
  {
    if (_preferences is null)
    {
      return [];
    }

    string? stored = await _preferences.GetAsync(SkillDirectoryPreferences.GlobalKey);
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(stored);
    return parsed.IsSuccess ? parsed.Value : [];
  }

  /// <summary>Loads the workspace skill-directory rows for the launch dialog's
  ///     prefill. Null root means an empty scope.</summary>
  public async Task<IReadOnlyList<SessionFileEntry>> GetWorkspaceSkillDirectoriesAsync(string? workspaceRoot = null)
  {
    if (_preferences is null || workspaceRoot is null)
    {
      return [];
    }

    Result<string> resolved = ResolveWorkspaceRoot(workspaceRoot);
    if (!resolved.IsSuccess)
    {
      return [];
    }

    string? stored = await _preferences.GetAsync(
        SkillDirectoryPreferences.WorkspaceKey(resolved.Value));
    Result<IReadOnlyList<SessionFileEntry>> parsedWs = SkillDirectoryPreferences.Parse(stored);
    return parsedWs.IsSuccess ? parsedWs.Value : [];
  }

  /// <summary>Loads the workspace trusted-directory rows for the launch dialog's prefill
  ///     (issue #96). Null root means an empty scope; a parse failure renders as empty
  ///     rather than half a list - the safe direction for external reach is nothing.</summary>
  public async Task<IReadOnlyList<TrustedDirectoryEntry>> GetTrustedDirectoriesAsync(string? workspaceRoot = null)
  {
    if (_preferences is null || workspaceRoot is null)
    {
      return [];
    }

    Result<string> resolved = ResolveWorkspaceRoot(workspaceRoot);
    if (!resolved.IsSuccess)
    {
      return [];
    }

    string? stored = await _preferences.GetAsync(
        TrustedDirectoryPreferences.WorkspaceKey(resolved.Value));
    Result<IReadOnlyList<TrustedDirectoryEntry>> parsed = TrustedDirectoryPreferences.Parse(stored);
    return parsed.IsSuccess ? parsed.Value : [];
  }

  /// <summary>Loads the skill-registry default target for the settings prefill.
  ///     The raw stored value prefills; strict parsing happens at settings load.</summary>
  public async Task<string> GetSkillRegistryDefaultTargetAsync()
  {
    if (_preferences is null)
    {
      return string.Empty;
    }

    string? stored = await _preferences.GetAsync(AgentPreferenceKeys.SkillRegistryDefaultTarget);
    return stored ?? string.Empty;
  }

  /// <summary>The currently-selected compaction model row for the modal's prefill.</summary>
  public Task<CompactionModelOption?> GetSelectedCompactionModelAsync()
  {
    string? preferred = _compactionWorkspaceKey is null || _preferences is null
        ? null
        : _preferences.GetAsync(CompactionModelResolver.PreferenceKey(PreferredProviderId, _compactionWorkspaceKey)).GetAwaiter().GetResult();
    return Task.FromResult(preferred is null ? null : new CompactionModelOption(preferred, preferred));
  }

  /// <summary>Menu-bar entry point: raises the Model Settings request. The view
  ///     shows the settings window and calls <see cref="ApplySamplingSettingsAsync"/>
  ///     with the saved snapshot. Available on every provider: z.ai sessions grey
  ///     out the knobs they do not accept instead of hiding the window.</summary>
  public void RequestChooseModelSettings()
  {
    if (ChooseModelSettingsCommand.CanExecute(null))
    {
      ModelSettingsRequested?.Invoke(this, EventArgs.Empty);
    }
  }

  /// <summary>Loads the selected tab's provider catalog for the model picker, or null
  ///     when no tab is selected. The loader escapes collection evaluation in the
  ///     picker's view-model, which runs it off the UI thread.</summary>
  public Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? SelectedTabCatalogLoader
  {
    get
    {
      AgentTabViewModel? tab = SelectedTab;
      return tab is null
          ? null
          : ct => tab.Container.Services.GetRequiredService<IModelCatalog>().GetAsync(ct);
    }
  }


  /// <summary>Joins source revalidation for the selected tab's settings dialog.</summary>
  public Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? SelectedTabCatalogRefresher
  {
    get
    {
      AgentTabViewModel? tab = SelectedTab;
      return tab is null ? null
          : ct => tab.Container.Services.GetRequiredService<IModelCatalog>().RefreshAsync(ct);
    }
  }

  /// <summary>Applies a picker choice to the selected tab and persists it per
  ///     workspace + provider (best effort — the same named decision as the other
  ///     preferences). Null means the session default and clears the persisted
  ///     preference, so future opens of the same directory start on it.
  ///     <para>THE CHOICE PERSISTS TOGETHER: a model without its routing variant is half a
  ///     decision, so the stored value carries both and a later open restores both.</para>
  ///     </summary>
  /// <param name="modelId">The model to serve, or null for the session's default model.</param>
  /// <param name="variant">The routing variant, or null for the provider's own routing.</param>
  public async Task ApplyModelChoiceAsync(string? modelId, ModelVariant? variant = null)
  {
    AgentTabViewModel? tab = SelectedTab;
    if (tab is null)
    {
      return;
    }

    tab.ViewModel.ApplyModelChoice(modelId, variant);
    await PersistModelChoiceAsync(tab.Container.ProviderName, tab.Container.WorkspaceRoot,
        modelId, variant);
  }

  /// <summary>Applies an effort-picker choice to the selected tab and persists it per
  ///     workspace + provider (best effort — the same named decision as the other
  ///     preferences). Null means the model default and clears the persisted
  ///     preference, so future opens of the same directory start on the provider's
  ///     own reasoning behavior.</summary>
  public async Task ApplyEffortChoiceAsync(ReasoningEffort? effort)
  {
    AgentTabViewModel? tab = SelectedTab;
    if (tab is null)
    {
      return;
    }

    tab.ViewModel.ApplyEffortChoice(effort);
    await PersistEffortChoiceAsync(tab.Container.ProviderName, tab.Container.WorkspaceRoot, effort);
  }

  /// <summary>Applies a Model Settings save to the selected tab and persists it
  ///     per workspace + provider (best effort — the same named decision as the
  ///     other preferences): the knob texts under the sampling_prefs key, the
  ///     serialized OpenRouter settings under the provider_settings key (null
  ///     deletes — an unset value never lingers), and the effort choice under
  ///     the SAME effort_choice key the effort picker uses (a saved Model
  ///     default deletes — a stored level must never outlive an explicit
  ///     Model default save and silently win on reopen). The typed values are
  ///     already on the session's live preferences through the session
  ///     view-model's apply.
  ///     </summary>
  public async Task ApplySamplingSettingsAsync(ModelSettingsSnapshot snapshot)
  {
    ArgumentNullException.ThrowIfNull(snapshot);
    AgentTabViewModel? tab = SelectedTab;
    if (tab is null)
    {
      return;
    }

    tab.ViewModel.ApplySamplingSettings(snapshot.Preferences);
    string providerName = tab.Container.ProviderName;
    string workspaceRoot = tab.Container.WorkspaceRoot;
    // The embedded model section rides the same save: Unchanged leaves the
    // session's model choice (and its persisted preference) untouched; Auto
    // clears it (automatic resolution applies from the next turn); Pinned pins
    // the chosen id. Same apply + per-workspace persistence the old picker used.
    if (snapshot.Model is { } model && model.Kind != ModelChoiceKind.Unchanged)
    {
      string? modelId = model.Kind == ModelChoiceKind.Pinned ? model.ModelId : null;
      ModelVariant? variant = model.Kind == ModelChoiceKind.Pinned ? model.Variant : null;
      tab.ViewModel.ApplyModelChoice(modelId, variant);
      await PersistModelChoiceAsync(providerName, workspaceRoot, modelId, variant);
    }

    await PersistEffortChoiceAsync(providerName, workspaceRoot, snapshot.Preferences.ReasoningEffort);
    // Every knob unset means the whole preference is unset: delete the key instead
    // of persisting an empty map (matching the documented null-deletes contract;
    // the restore path already treats a missing key as unset). An all-whitespace
    // map serializes to entries whose values are empty strings — still unset.
    bool allUnset = snapshot.KnobTexts.Count == 0
        || snapshot.KnobTexts.Values.All(value => value.Length == 0);
    string? knobPayload = allUnset ? null : JsonSerializer.Serialize(snapshot.KnobTexts);
    await PersistSamplingPrefsAsync(providerName, workspaceRoot, knobPayload);
    await PersistProviderSettingsAsync(providerName, workspaceRoot, snapshot.ProviderSettingsJson);
  }

  /// <summary>Applies a settings-modal result: persists the key (protected) or deletes
  ///     the cleared one, rebuilds the session factory so
  ///     future opens use the new settings, and refreshes the provider surface.
  ///     Already-open tabs keep the wiring they were created with. The update carries
  ///     FULL field state, not a delta; values are normalized defensively here (trimmed,
  ///     blank → cleared) — the stricter no-internal-whitespace rule is the modal's
  ///     boundary. A no-op for hosts that compose sessions themselves (no settings
  ///     snapshot). Persistence is best effort — the same named decision as the provider
  ///     preference.</summary>
  public async Task ApplySettingsAsync(SettingsUpdate update)
  {
    ArgumentNullException.ThrowIfNull(update);
    if (_settings is null)
    {
      return;
    }

    string? openRouterKey = Normalize(update.OpenRouterApiKey);
    string? managementKey = Normalize(update.OpenRouterManagementKey);

    await PersistSettingsTextAsync(update, openRouterKey, managementKey);
    ConfiguredCommitStyle = update.CommitStyle;
    PersistScopedSettings(update);

    // Re-hydrate non-secrets from the store they just landed in — the loader's strict
    // binding is the single interpretation of these values (no parallel with-mutators).
    // (Deviation from the plan snippet's literal 'previous.OpenRouter.ApiKey': reading
    // the PRE-save snapshot would resurrect keys this very save cleared — the update
    // carries FULL field state and null keys mean cleared. The keys current to this
    // save are the normalized update values, the same ones persisted above; deviating
    // to re-apply them keeps both contracts — no loss on re-hydration (rebuilt carries
    // none), no resurrection of cleared keys.)
    AgentSettings rebuilt = _preferences is not null
        ? await AgentSettingsLoader.LoadAsync(_preferences)
        : BindFromUpdate(update); // test seam: no store, same strict binders
    _settings = rebuilt.WithApiKeys(openRouterKey).WithManagementKey(managementKey);
    _sessionFactory = _sessionFactory?.WithSettings(_settings);

    PushLiveManagementKey(managementKey);
    RefreshProviderSurface(_settings);
  }

  /// <summary>Persists every non-scoped field of a settings-modal update: the two
  ///     keys (protected) and the plaintext knobs, each normalized defensively.
  ///     Best effort, the same named decision as every other preference write.</summary>
  private async Task PersistSettingsTextAsync(SettingsUpdate update, string? openRouterKey,
      string? managementKey)
  {
    await PersistApiKeyAsync(OpenRouterSettings.PreferenceKey, openRouterKey);
    await PersistApiKeyAsync(OpenRouterSettings.ManagementKeyPreferenceKey, managementKey);
    await PersistPreferenceAsync(AppPreferenceCommitStyleProvider.PreferenceKey,
        update.CommitStyle.ToString());
    await PersistPreferenceAsync(AgentPreferenceKeys.MaxConcurrentAgents,
        Normalize(update.MaxConcurrentAgentsText));
    await PersistPreferenceAsync(AgentPreferenceKeys.DefaultModel,
        Normalize(update.DefaultModelText));
    await PersistPreferenceAsync(AgentPreferenceKeys.WatchdogTickInterval,
        Normalize(update.WatchdogTickText));
    await PersistPreferenceAsync(AgentPreferenceKeys.WatchdogIdleThreshold,
        Normalize(update.WatchdogIdleText));
    await PersistPreferenceAsync(AgentPreferenceKeys.WatchdogMaxWrapUpAttempts,
        Normalize(update.WatchdogWrapUpText));
    await PersistPreferenceAsync(AgentPreferenceKeys.RootTurnIdle,
        Normalize(update.RootTurnIdleText));
    await PersistPreferenceAsync(AgentPreferenceKeys.OpenRouterBaseUrl,
        Normalize(update.OpenRouterBaseUrlText));
    await PersistPreferenceAsync(AgentPreferenceKeys.ComputerUseEnabled,
        update.ComputerUse ? "true" : "false");
    // The compaction handoff is a LIVE preference (issue #234): the composition reads it at
    // every compaction, so a save applies from the next compaction without a container
    // rebuild. Absent means off, so 'false' is written as a delete rather than a value.
    await PersistPreferenceAsync(SessionHandoffPreferences.Key,
        update.CompactionHandoff ? "true" : null);
    ConfiguredCompactionHandoff = update.CompactionHandoff;
  }

  /// <summary>Persists the two scoped settings of a settings-modal update: the
  ///     compaction summarizer (per selected tab's provider + workspace) and the
  ///     skill-registry default target. Absent values delete their key.</summary>
  private void PersistScopedSettings(SettingsUpdate update)
  {
    // Compaction summarizer is per selected tab's (provider, workspace): unset means
    // Automatic — the cheapest capable catalog entry resolves at compaction time.
    if (update.CompactionWorkspaceKey is { } workspaceKey)
    {
      string compactionKey = CompactionModelResolver.PreferenceKey(PreferredProviderId, workspaceKey);
      _ = update.CompactionModelId is null
          ? _preferences?.DeleteAsync(compactionKey)
          : _preferences?.SetAsync(compactionKey, update.CompactionModelId);
    }

    // Skill-registry default target (plan #29 T12): absent text deletes the key
    // (unset), 'global'/'workspace' store verbatim; the loader strict-parses on read.
    if (update.SkillRegistryDefaultTarget is { } registryTarget)
    {
      _ = registryTarget.Length == 0
          ? _preferences?.DeleteAsync(AgentPreferenceKeys.SkillRegistryDefaultTarget)
          : _preferences?.SetAsync(AgentPreferenceKeys.SkillRegistryDefaultTarget, registryTarget);
    }
  }

  /// <summary>Live management-key push: every open session's carrier takes the saved key
  ///     immediately — the frozen OpenRouterConfiguration snapshot forced a session
  ///     restart to pick a key up (2026-10-08). The openrouter_management tool reads
  ///     the carrier at dispatch, so the NEXT call uses the key. Sessions without a
  ///     carrier (headless stubs) skip the push silently.</summary>
  private void PushLiveManagementKey(string? managementKey)
  {
    foreach (AgentTabViewModel tab in Tabs)
    {
      tab.Container.Services.GetService<OpenRouterManagementKeyCarrier>()?.Current = managementKey;
    }
  }

  /// <summary>Re-derives the offered providers from the rebuilt settings and moves the
  ///     pre-selection onto the first configured one when the current pick vanished.</summary>
  private void RefreshProviderSurface(AgentSettings settings)
  {
    AvailableProviders = ProvidersFrom(settings);
    if (!AvailableProviders.Any(p => p.Id == PreferredProviderId) && AvailableProviders.Count > 0)
    {
      PreferredProviderId = AvailableProviders[0].Id;
    }
  }

  private static string? Normalize(string? key)
  {
    string trimmed = key?.Trim() ?? string.Empty;
    return trimmed.Length == 0 ? null : trimmed;
  }

  /// <summary>Rebuilds the non-secret settings from a settings-modal update when no
  ///     preference store exists (test seam) — the same strict binders
  ///     <see cref="AgentSettingsLoader.LoadAsync"/> runs and the same shared
  ///     preference-key error naming, so there is exactly one interpretation of
  ///     the persisted text either way.</summary>
  private static AgentSettings BindFromUpdate(SettingsUpdate update)
  {
    SubAgentOptions subAgents;
    WatchdogSettings? watchdog;
    try
    {
      subAgents = SubAgentConfiguration.Bind(
          Normalize(update.DefaultModelText),
          Normalize(update.MaxConcurrentAgentsText)
              ?? AgentSettingsDefaults.MaxConcurrentAgents.ToString(CultureInfo.InvariantCulture),
          Normalize(update.RootTurnIdleText));
      watchdog = SubAgentConfiguration.BindWatchdog(
          Normalize(update.WatchdogTickText), Normalize(update.WatchdogIdleText),
          Normalize(update.WatchdogWrapUpText));
    }
    catch (InvalidOperationException ex)
    {
      // The same surfaced names as the loader path — one shared translation
      // (AgentSettingsLoader.WithPreferenceKeyName): the binders' retired
      // config-path key names never leak through this seam either.
      throw AgentSettingsLoader.WithPreferenceKeyName(ex);
    }
    return new AgentSettings(
        new OpenRouterSettings(null, AgentSettingsLoader.BindBaseUrl(Normalize(update.OpenRouterBaseUrlText),
#pragma warning disable S1075 // Anchored provider default; the loader's own seam carries the same named decision.
            AgentPreferenceKeys.OpenRouterBaseUrl, "https://openrouter.ai")),
#pragma warning restore S1075
        subAgents,
        Watchdog: watchdog);
  }

  /// <summary>Writes one key preference: set (protected) when configured, delete when
  ///     cleared. Best effort — a failed write logs to stderr and never fails the save.
  ///     Plaintext keys never reach durable storage: without a protector a key save is
  ///     skipped with a note instead of stored raw.</summary>
  private async Task PersistApiKeyAsync(string preferenceKey, string? apiKey)
  {
    if (_preferences is null)
    {
      return; // test seam: nothing is remembered
    }

    if (apiKey is not null && _keyProtector is null)
    {
      await Console.Error.WriteLineAsync($"no API key protector configured; '{preferenceKey}' not persisted");
      return;
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = apiKey is null
          ? await _preferences.DeleteAsync(preferenceKey)
          : await _preferences.SetAsync(preferenceKey, _keyProtector!.Protect(apiKey));
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"api key preference write failed for '{preferenceKey}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"api key preference write failed for '{preferenceKey}': {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Writes one plaintext preference (never a secret — secrets go through the
  ///     key protector). A null value deletes the preference (blank clears). Best
  ///     effort, same named decision as the key preferences.</summary>
  private async Task PersistPreferenceAsync(string preferenceKey, string? value)
  {
    if (_preferences is null)
    {
      return; // test seam: nothing is remembered
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = value is null
          ? await _preferences.DeleteAsync(preferenceKey)
          : await _preferences.SetAsync(preferenceKey, value);
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}': {ex.Message}");
    }
#pragma warning restore CA1031
  }

  private static List<ProviderOption> ProvidersFrom(AgentSettings settings)
  {
    List<ProviderOption> providers = [];
    if (settings.HasOpenRouter)
    {
      providers.Add(new ProviderOption(Providers.OpenRouter, Providers.DisplayName(Providers.OpenRouter)));
    }
    return providers;
  }

  /// <summary>Opens a new agent tab over <paramref name="workspaceRoot"/>, wired
  ///     exclusively for <paramref name="providerName"/> for the tab's whole lifetime.
  ///     Fails with a structured error when the session cannot be created; the shell
  ///     surfaces it. Reopening an already-open (directory, provider) pair selects its
  ///     existing tab instead — the same directory may be open under both providers.</summary>
  public async Task<Result<AgentTabViewModel>> OpenAgentAsync(string workspaceRoot, string providerName)
  {
    // The root is VALIDATED, not trusted: a blank or unresolvable path must
    // fail with a NAMED error, never an exception. Path.GetFullPath throws on
    // both (ArgumentNullException / ArgumentException), and this method runs
    // inside the launch handler - an escaping exception took the whole
    // process down on 2026-10-20.
    Result<string> resolved = ResolveWorkspaceRoot(workspaceRoot);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<AgentTabViewModel>(resolved.Error);
    }

    string full = resolved.Value;
    AgentTabViewModel? existing = Tabs.FirstOrDefault(t =>
        string.Equals(t.Container.WorkspaceRoot, full, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.Container.ProviderName, providerName, StringComparison.Ordinal));
    if (existing is not null)
    {
      SelectedTab = existing;
      return Result.Success(existing);
    }

    IsOpeningAgent = true;
    try
    {
      // Session construction builds a DI container and persists the root row —
      // core work that never belongs on the UI thread. Context flow is suppressed
      // alongside the thread switch, same reasoning as for OffUiThread in DesktopHost:
      // Task.Run alone still flows the caller's SynchronizationContext.
      Task<Result<AgentSession>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _createSession(full, providerName));
      }

      Result<AgentSession> created = await scheduled;
      return created.IsSuccess
          ? Result.Success(await AttachSessionAsync(created.Value))
          : Result.Failure<AgentTabViewModel>(created.Error);
    }
    // Named decision (CA1031): a failed open is INFORMATION, not a crash. This method
    // runs on the UI dispatcher, where an escaping exception tears the whole process
    // down (the 2026-10-21 crash: a NullReferenceException from the host lookup
    // killed the app on workspace open). Every fault becomes a named error the shell
    // surfaces in a dialog.
#pragma warning disable CA1031, S2486 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031, S2486
    {
      return Result.Failure<AgentTabViewModel>(
          new DomainError("SessionOpenFailed", "the workspace could not be opened: " + ex.Message));
    }
    finally
    {
      IsOpeningAgent = false;
    }
  }

  /// <summary>Applies the launch dialog's file configuration (E moved out of
  ///     Settings): persists the confirmed session-file and skill-directory lists
  ///     to the SAME preference keys the session factory reads at container build,
  ///     so the NEXT open of any workspace carries them. The lists carry FULL
  ///     state — an empty list deletes its key (nothing configured), a non-empty
  ///     one serializes verbatim. Best effort, the same named decision as every
  ///     other preference write. Call BEFORE OpenAgentAsync so the new session's
  ///     container build reads the just-saved lists.
  ///     <para>The workspace root is VALIDATED here, not trusted: the workspace-scoped
  ///     keys are derived from it, and Path.GetFullPath throws on a null or blank
  ///     path (the 2026-10-20 crash: an ArgumentNullException inside the dialog's
  ///     async-void handler took the whole process down). A missing root is a NAMED
  ///     failure, never an exception, and it persists NOTHING.</para></summary>
  public async Task<Result<bool>> ApplyLaunchFilesAsync(NewAgentChoice choice)
  {
    ArgumentNullException.ThrowIfNull(choice);

    Result<string> root = ResolveWorkspaceRoot(choice.WorkspaceRoot);
    if (!root.IsSuccess)
    {
      return Result.Failure<bool>(root.Error);
    }

    if (_preferences is null)
    {
      return Result.Success(true); // test seam: nothing is remembered
    }

    await PersistFileListAsync(SessionFilePreferences.GlobalKey, choice.GlobalFiles,
        SessionFilePreferences.Serialize);
    await PersistFileListAsync(
        SessionFilePreferences.WorkspaceKey(root.Value),
        choice.WorkspaceFiles, SessionFilePreferences.Serialize);
    await PersistFileListAsync(SkillDirectoryPreferences.GlobalKey, choice.GlobalSkillDirectories,
        SkillDirectoryPreferences.Serialize);
    await PersistFileListAsync(
        SkillDirectoryPreferences.WorkspaceKey(root.Value),
        choice.WorkspaceSkillDirectories, SkillDirectoryPreferences.Serialize);
    // Trusted directories (issue #96) are WORKSPACE-scoped: an approval says 'in THIS
    // workspace the agent may reach this external root', and a global list would widen
    // every other workspace's reach the moment one approves something.
    await PersistTrustedDirectoriesAsync(TrustedDirectoryPreferences.WorkspaceKey(root.Value),
        choice.TrustedDirectories ?? []);

    return Result.Success(true);
  }

  /// <summary>Resolves the launch choice's workspace root to its full path, or
  ///     fails with a NAMED error. A null or blank root is refused outright
  ///     (InvalidWorkspace) — it can never reach Path.GetFullPath, which throws on
  ///     both. A path that cannot be resolved (an embedded null character, a
  ///     malformed path) is refused the same way, with the reason in the message.</summary>
  private static Result<string> ResolveWorkspaceRoot(string? workspaceRoot)
  {
    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return Result.Failure<string>(new DomainError("InvalidWorkspace",
          "workspace root must be a non-empty directory path."));
    }

    try
    {
      return Result.Success(Path.GetFullPath(workspaceRoot));
    }
    catch (Exception ex) when (
        ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return Result.Failure<string>(new DomainError("InvalidWorkspace",
          "workspace root could not be resolved: " + ex.Message));
    }
  }

  /// <summary>Writes the trusted-directory list (issue #96): an empty list deletes the key
  ///     (nothing approved), a non-empty one serializes verbatim. Best effort, the same
  ///     named decision as every other preference write - a failed write logs to stderr and
  ///     never fails the launch, so the worst case is a session opened without the approval
  ///     the user just made rather than a lost workspace.</summary>
  private async Task PersistTrustedDirectoriesAsync(string preferenceKey,
      IReadOnlyList<TrustedDirectoryEntry> entries)
  {
    IAppPreferenceStore preferences = _preferences
        ?? throw new InvalidOperationException("PersistTrustedDirectoriesAsync requires a preference store.");
    // Named decision (CA1031): preference persistence must not take the launch down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = entries.Count == 0
          ? await preferences.DeleteAsync(preferenceKey)
          : await preferences.SetAsync(preferenceKey, TrustedDirectoryPreferences.Serialize(entries));
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}': {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Writes one file/directory list preference: an empty list deletes the
  ///     key (nothing configured), a non-empty one serializes verbatim. Best
  ///     effort — a failed write logs to stderr and never fails the launch
  ///     (named decision, CA1031).</summary>
  private async Task PersistFileListAsync(string preferenceKey,
      IReadOnlyList<SessionFileEntry> entries, Func<IReadOnlyList<SessionFileEntry>, string> serialize)
  {
    IAppPreferenceStore preferences = _preferences
        ?? throw new InvalidOperationException("PersistFileListAsync requires a preference store.");
    // Named decision (CA1031): preference persistence must not take the launch down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool landed = entries.Count == 0
          ? await preferences.DeleteAsync(preferenceKey)
          : await preferences.SetAsync(preferenceKey, serialize(entries));
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"preference write failed for '{preferenceKey}': {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Resumes a persisted root session by id: its transcript replays into the
  ///     tab, so the conversation continues where it stopped. A session already open in
  ///     a tab is selected, never double-resumed. The provider and workspace come from
  ///     the persisted record; a fresh open stays the only way to start a new session
  ///     for a directory. Fails with the factory's structured error; the shell surfaces
  ///     it.</summary>
  public async Task<Result<AgentTabViewModel>> ResumeSessionAsync(AgentId sessionId)
  {
    AgentTabViewModel? existing = Tabs.FirstOrDefault(t => t.Container.RootId == sessionId);
    if (existing is not null)
    {
      SelectedTab = existing;
      return Result.Success(existing);
    }

    IsOpeningAgent = true;
    try
    {
      // Container build + transcript hydration — off the UI thread, context flow
      // suppressed, exactly like a fresh open.
      Task<Result<AgentSession>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _resumeSession(sessionId));
      }

      Result<AgentSession> resumed = await scheduled;
      return resumed.IsSuccess
          ? Result.Success(await AttachSessionAsync(resumed.Value))
          : Result.Failure<AgentTabViewModel>(resumed.Error);
    }
    // Named decision (CA1031): a failed resume is INFORMATION, not a crash - the same
    // rule as a fresh open, for the same reason (the UI dispatcher has no handler).
#pragma warning disable CA1031, S2486 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031, S2486
    {
      return Result.Failure<AgentTabViewModel>(
          new DomainError("SessionResumeFailed", "the session could not be resumed: " + ex.Message));
    }
    finally
    {
      IsOpeningAgent = false;
    }
  }

  /// <summary>Starts a NEW session in <paramref name="tab"/>'s workspace, IN PLACE (issue
  ///     #234): the tab is not added beside the old one — its session is replaced, so the
  ///     user sees one tab whose conversation restarted and whose status bar names a new
  ///     session id. The old session closes gracefully first (Completed, so the Sessions
  ///     dialog can resume it) and its container is disposed; its host process dies with it.
  ///     <para>REFUSE-WHEN-BUSY: a turn in flight is a refusal, not a race. Replacing a
  ///     session mid-turn would dispose the container under the running turn, and the
  ///     transcript the user is reading would be destroyed while the model was still
  ///     producing it.</para>
  ///     <para><paramref name="handoffSummary"/> seeds the new conversation (the compaction
  ///     handoff); <paramref name="prompt"/> is its first turn — EMPTY means seed-only, no
  ///     turn is fired (a handoff continues from the summary, it does not re-ask).</para>
  ///     <para>Must run on the UI thread: it mutates <see cref="Tabs"/>.</para></summary>
  public async Task<Result<AgentTabViewModel>> StartNewSessionAsync(AgentTabViewModel tab,
      string? prompt, string? handoffSummary = null)
  {
    ArgumentNullException.ThrowIfNull(tab);
    if (!Tabs.Contains(tab))
    {
      return Result.Failure<AgentTabViewModel>(new DomainError("TabClosed",
          "that tab is no longer open; no session was started."));
    }

    if (tab.ViewModel.IsBusy)
    {
      return Result.Failure<AgentTabViewModel>(new DomainError("TurnBusy",
          "a turn is running in that tab; stop it before starting a new session."));
    }

    IReadOnlyList<Message>? seed = SessionHandoffSeed.Build(handoffSummary);
    SeededSessionCreator? creator = _createSeededSession;
    if (seed is not null && creator is null)
    {
      return Result.Failure<AgentTabViewModel>(new DomainError("SeedUnsupported",
          "this host cannot seed a new session's conversation; the handoff summary would be lost."));
    }

    string workspaceRoot = tab.Container.WorkspaceRoot;
    string providerName = tab.Container.ProviderName;
    int index = Tabs.IndexOf(tab);

    // Container build + root-row persistence — core work that never belongs on the UI
    // thread, exactly like a fresh open (context flow suppressed for the same reason).
    Task<Result<AgentSession>> scheduled;
    using (ExecutionContext.SuppressFlow())
    {
      scheduled = Task.Run(() => creator is not null && seed is not null
          ? creator(workspaceRoot, providerName, seed)
          : _createSession(workspaceRoot, providerName));
    }

    Result<AgentSession> created;
    try
    {
      created = await scheduled;
    }
    // Named decision (CA1031): a failed launch is INFORMATION, not a crash — this path is
    // reached from the UI dispatcher and from the model's new_session tool, where an
    // escaping exception has no handler at all.
#pragma warning disable CA1031, S2486 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031, S2486
    {
      return Result.Failure<AgentTabViewModel>(new DomainError("SessionLaunchFailed",
          "the new session could not be started: " + ex.Message));
    }

    if (!created.IsSuccess)
    {
      // The factory's own typed code travels through verbatim (SessionLaunchFailed only
      // wraps a thrown fault, never a refused Result).
      return Result.Failure<AgentTabViewModel>(created.Error);
    }

    return Result.Success(await ReplaceSessionAsync(tab, created.Value, index, prompt));
  }

  /// <summary>Replaces the PRIMARY CELL of <paramref name="tab"/> with a cell over
  ///     <paramref name="session"/> (issue #234 + #307 D2): the old session completes
  ///     gracefully and its container is disposed, the new session takes the SAME cell, and —
  ///     when <paramref name="prompt"/> is non-empty — its first turn fires.
  ///     <para>#307 changed what 'in place' means. Before the grid, replacing the tab was the
  ///     only way to restart a conversation, so `/new` removed the tab and attached a new one
  ///     at the same index. Now the tab is the WORKSPACE and holds a grid: a sequential
  ///     advance continues in its own cell, leaving the rail and any sibling agents alone.
  ///     A PARALLEL dispatch is the case that adds a cell, and that is <see cref="AddCellAsync"/>.</para>
  ///     <para>ORDER MATTERS: the replacement is attached and wired BEFORE the old session is
  ///     torn down. Teardown disposes a container and kills its host process, and the new
  ///     session's own host start must not be racing that teardown inside the same cell; more
  ///     importantly a teardown fault must never leave the tab with no cell at all.</para>
  ///     <para>Must run on the UI thread.</para></summary>
  private async Task<AgentTabViewModel> ReplaceSessionAsync(AgentTabViewModel tab,
      AgentSession session, int index, string? prompt)
  {
    // A tab that vanished while the container was building is not resurrected: the new
    // session is attached as its own tab so the work is not lost.
    if (!Tabs.Contains(tab))
    {
      return await AttachSessionAsync(session, Math.Clamp(index, 0, Tabs.Count));
    }

    AgentSessionViewModel retiredVm = tab.ViewModel;
    AgentSession retiredSession = tab.Container;
    WorkbenchViewModel workbench = tab.Workbench;
    // Self-referencing sink hook, the same pattern as the attach path: the view-model is
    // captured after construction so its own sink marshals its events onto the UI thread.
    AgentSessionViewModel? freshVmRef = null;
    AgentSessionViewModel freshVm = BuildSessionViewModel(session, workbench,
        () => freshVmRef ??
            throw new InvalidOperationException("session view-model not initialized"),
        id => freshVmRef!.Status.ModelId = id,
        () => tab);
    freshVmRef = freshVm;
    session.NoticeSink = message => Avalonia.Threading.Dispatcher.UIThread.Post(
        () => freshVmRef?.AddSystemNotice(message));
    session.DeliverPendingNotices();
    await RestorePinnedModelAsync(session, freshVm);
    await RestoreSessionPreferencesAsync(session, freshVm);
    await freshVm.SeedUsageAsync().ConfigureAwait(true);
    freshVm.Transcript.Restore(session.Conversation.Messages);

    tab.ReplacePrimaryCell(session, freshVm);
    AttachTabWiring(session, tab);
    await CloseReplacedSessionAsync(retiredVm, retiredSession);

    if (!string.IsNullOrWhiteSpace(prompt))
    {
      // Fire the first turn and DO NOT await it here: the caller (the slash command path,
      // or the host's session-request) must return so the UI keeps painting the stream. The
      // view-model owns the turn task and surfaces its own faults.
      _ = freshVm.SubmitAsync(prompt);
    }

    return tab;
  }

  /// <summary>Adds a NEW agent cell to <paramref name="tab"/>'s grid (issue #307 D2): the
  ///     parallel-dispatch shape, as opposed to a sequential advance, which replaces the
  ///     primary cell in place.
  ///     <para>Both shapes must be deliberate. A chain is a SEQUENCE of agents, so its advance
  ///     continues in one cell; several agents working AT ONCE each need their own, and adding
  ///     one must never disturb the agents already running beside it.</para>
  ///     <para>This is the seam a parallel dispatch uses. Nothing calls it yet: parallel
  ///     coordination is #314, which ships last. It exists now because D2 is a decision about
  ///     the grid's shape, and a grid that could only ever hold one agent would have to be
  ///     re-cut to admit the second.</para>
  ///     <para>Must run on the UI thread.</para></summary>
  public async Task<Result<AgentCellViewModel>> AddCellAsync(AgentTabViewModel tab, string? prompt)
  {
    ArgumentNullException.ThrowIfNull(tab);
    if (!Tabs.Contains(tab))
    {
      return Result.Failure<AgentCellViewModel>(new DomainError("TabClosed",
          "that tab is no longer open; no agent was added."));
    }

    string workspaceRoot = tab.Container.WorkspaceRoot;
    string providerName = tab.Container.ProviderName;
    Task<Result<AgentSession>> scheduled;
    using (ExecutionContext.SuppressFlow())
    {
      scheduled = Task.Run(() => _createSession(workspaceRoot, providerName));
    }

    Result<AgentSession> created;
    try
    {
      created = await scheduled;
    }
    // Named decision (CA1031): a failed launch is INFORMATION, not a crash - the same rule the
    // replace path follows, for the same reason (the UI dispatcher has no handler).
#pragma warning disable CA1031, S2486 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031, S2486
    {
      return Result.Failure<AgentCellViewModel>(new DomainError("SessionLaunchFailed",
          "the new agent could not be started: " + ex.Message));
    }

    if (!created.IsSuccess)
    {
      return Result.Failure<AgentCellViewModel>(created.Error);
    }

    AgentSession session = created.Value;
    WorkbenchViewModel workbench = tab.Workbench;
    AgentSessionViewModel? cellVmRef = null;
    AgentSessionViewModel cellVm = BuildSessionViewModel(session, workbench,
        () => cellVmRef ??
            throw new InvalidOperationException("session view-model not initialized"),
        id => cellVmRef!.Status.ModelId = id,
        () => tab);
    cellVmRef = cellVm;
    session.NoticeSink = message => Avalonia.Threading.Dispatcher.UIThread.Post(
        () => cellVmRef?.AddSystemNotice(message));
    session.DeliverPendingNotices();
    await RestorePinnedModelAsync(session, cellVm);
    await RestoreSessionPreferencesAsync(session, cellVm);
    await cellVm.SeedUsageAsync().ConfigureAwait(true);
    cellVm.Transcript.Restore(session.Conversation.Messages);

    AgentCellViewModel cell = tab.AddCell(session, cellVm);
    AttachTabWiring(session, tab);

    if (!string.IsNullOrWhiteSpace(prompt))
    {
      _ = cellVm.SubmitAsync(prompt);
    }

    return Result.Success(cell);
  }

  /// <summary>Retires a replaced cell's session: completes it gracefully (so the Sessions
  ///     dialog can resume it) and disposes its container, which kills its host process.
  ///     The cell is already swapped, so the tab is never left without one. Best effort by
  ///     named decision — a teardown fault must never fail the session the user just started.
  ///     <para>Issue #307: this takes the retired session and view-model directly rather than
  ///     the whole tab, because the tab SURVIVES a cell swap - its rail and its sibling cells
  ///     are not part of the teardown.</para></summary>
  private async Task CloseReplacedSessionAsync(AgentSessionViewModel retiredVm, AgentSession retired)
  {
    _sessionClosed?.Invoke(retired.RootId);
#pragma warning disable CA1031 // Named decision: teardown is best effort, like CloseTabAsync.
    try
    {
      await retiredVm.ShutdownAsync();
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
#pragma warning disable CA1031 // Named decision: a disposed container must not fail the new session.
    try
    {
      await retired.Services.DisposeAsync();
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
  }

  /// <summary>Handles one host session-request (issue #234) for a tab: the new_session tool
  ///     or a compaction handoff asked for a session in this tab's workspace. The HOST IS
  ///     BLOCKED awaiting the answer, so the request is answered with the new session's id
  ///     as soon as the container exists — BEFORE the tab swap, which waits for the current
  ///     turn to go idle (the host cannot swap its own tab mid-turn).
  ///     <para>REFUSALS ARE ANSWERED TOO: every failure path replies with its typed code,
  ///     because the unanswered alternative is a tool waiting out its own budget for an
  ///     answer that was never coming.</para>
  ///     <para>ONE REPLACEMENT PER TAB: a second request while one is pending is refused
  ///     (SessionRequestPending) — two launches racing for one tab slot would leave one
  ///     session orphaned in no tab at all, alive and unreachable.</para>
  ///     <para>Must run on the UI thread (the view-model marshals it there).</para></summary>
  public async Task HandleSessionRequestAsync(AgentTabViewModel tab, SessionRequestPayload request)
  {
    ArgumentNullException.ThrowIfNull(tab);
    ArgumentNullException.ThrowIfNull(request);

    AgentSessionViewModel sessionVm = tab.ViewModel;
    IRootTurnChannel? channel = sessionVm.RootTurnChannel;
    if (channel is null)
    {
      // No wire, no answer: this session's host cannot be waiting (a headless/in-process
      // session has no channel at all), so the request is a wiring fault reported locally.
      sessionVm.AddSystemNotice("Error [SessionRequestUnroutable]: this session has no host channel; the new session was not started.");
      return;
    }

    if (!Tabs.Contains(tab))
    {
      Answer(channel, request, failure: new DomainError("TabClosed",
          "the tab that requested a new session is no longer open."));
      return;
    }

    if (_pendingReplacements.ContainsKey(tab))
    {
      Answer(channel, request, failure: new DomainError("SessionRequestPending",
          "a previous new-session request for this tab is still being applied."));
      return;
    }

    Result<AgentSession> created;
    try
    {
      created = await CreateForSessionRequestAsync(tab, request);
    }
    // Named decision (CA1031): the requesting tool has a budget, not a stack — a launch
    // fault must be ANSWERED, never thrown into the event pump.
#pragma warning disable CA1031, S2486 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031, S2486
    {
      created = Result.Failure<AgentSession>(new DomainError("SessionLaunchFailed",
          "the new session could not be started: " + ex.Message));
    }

    if (!created.IsSuccess)
    {
      Answer(channel, request, failure: created.Error);
      return;
    }

    // ANSWER FIRST: the id is the thing the blocked tool is waiting for, and the tab swap
    // must not delay it.
    Answer(channel, request, sessionId: created.Value.RootId.ToString());
    _pendingReplacements[tab] = created.Value;

    // The swap waits for the CURRENT turn to go idle. A handoff request is emitted BEFORE
    // the host settles its turn, so swapping now would dispose the container the running
    // turn is still writing through.
    await WaitForIdleAndReplaceAsync(tab, created.Value, request).ConfigureAwait(true);
  }

  /// <summary>The tab's session-request creation: seeded when the request carries a
  ///     handoff summary, plain otherwise. Runs off the UI thread through the same
  ///     suppressed-context schedule every other container build uses.</summary>
  private async Task<Result<AgentSession>> CreateForSessionRequestAsync(AgentTabViewModel tab,
      SessionRequestPayload request)
  {
    string workspaceRoot = tab.Container.WorkspaceRoot;
    string providerName = tab.Container.ProviderName;
    IReadOnlyList<Message>? seed = SessionHandoffSeed.Build(request.HandoffSummary);
    SeededSessionCreator? creator = _createSeededSession;
    if (seed is not null && creator is null)
    {
      return Result.Failure<AgentSession>(new DomainError("SeedUnsupported",
          "this host cannot seed a new session's conversation; the handoff summary would be lost."));
    }

    Task<Result<AgentSession>> scheduled;
    using (ExecutionContext.SuppressFlow())
    {
      scheduled = Task.Run(() => creator is not null && seed is not null
          ? creator(workspaceRoot, providerName, seed)
          : _createSession(workspaceRoot, providerName));
    }

    return await scheduled;
  }

  /// <summary>Awaits the requesting tab's turn going idle, then swaps. The wait is the
  ///     window between the answer and the replacement: the host emitted the request from
  ///     INSIDE its turn, so the turn is still running here. The tab is re-checked after the
  ///     wait — it may have been closed, and a pending replacement for a gone tab must not
  ///     resurrect it.</summary>
  private async Task WaitForIdleAndReplaceAsync(AgentTabViewModel tab, AgentSession session,
      SessionRequestPayload request)
  {
    try
    {
      await tab.ViewModel.WaitForTurnAsync().ConfigureAwait(true);
    }
    // Named decision (CA1031): a faulted turn is already surfaced in the transcript; the
    // replacement it requested must still land, or the session the host was told to
    // continue in would exist with no tab at all.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031

    int index = Tabs.IndexOf(tab);
    if (index < 0)
    {
      // The tab closed while the turn finished: the session it asked for has no home.
      // Disposed, not leaked — it has no tab, no host watcher, and nothing that would
      // ever reach it.
      await DisposeOrphanedSessionAsync(session).ConfigureAwait(true);
      return;
    }

    try
    {
      _ = await ReplaceSessionAsync(tab, session, index, request.Prompt).ConfigureAwait(true);
    }
    // Named decision (CA1031): a failed replacement surfaces as a notice — the turn is
    // already over and there is no tool left to report to.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031
    {
      tab.ViewModel.AddSystemNotice($"Error [SessionReplaceFailed]: the new session could not be opened in this tab: {ex.Message}");
      await DisposeOrphanedSessionAsync(session).ConfigureAwait(true);
    }
    finally
    {
      _ = _pendingReplacements.Remove(tab);
    }
  }

  /// <summary>Disposes a session that will never reach a tab (its requesting tab closed, or
  ///     its replacement failed). Best effort: it owns a host process, so a leaked one
  ///     outlives the app.</summary>
  private static async Task DisposeOrphanedSessionAsync(AgentSession session)
  {
#pragma warning disable CA1031 // Named decision: an orphan's teardown must not fault the caller.
    try
    {
      await session.Services.DisposeAsync();
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
  }

  /// <summary>Sends one session-request's answer (issue #234): exactly one of a session id
  ///     or a typed failure. Every path funnels here so no request can be left unanswered.</summary>
  private static void Answer(IRootTurnChannel channel, SessionRequestPayload request,
      string? sessionId = null, DomainError? failure = null)
      => channel.SendSessionResponse(request.RequestId, sessionId, failure?.Code, failure?.Message);

  /// <summary>Wires a created (fresh or resumed) session into a new tab: session
  ///     view-model with the self-referencing stream sink, per-workspace model/effort
  ///     restore BEFORE the tab can take a turn, persisted-transcript replay (a no-op
  ///     for a fresh session — its conversation is empty), and
  ///     tab attach. Must run on the UI thread.</summary>
  /// <summary>Maps the session's catalog to the autocomplete popup's options
  ///     (spec #28 task 5). The composite catalog's ListAsync is async while the
  ///     popup's source seam is sync, so this resolves synchronously — the
  ///     documented sync-over-async seam constraint (the SkillsListingPromptProvider
  ///     Build() precedent). Reachable only when the session wired the invocation
  ///     core; a catalog failure yields an empty list (the popup shows nothing).</summary>
  private static IReadOnlyList<SkillOption> SkillOptionsOf(AgentSession session)
  {
    ISkillCatalog catalog = session.Services.GetRequiredService<ISkillCatalog>();
    Result<IReadOnlyList<SkillDefinition>> listed = catalog.ListAsync().GetAwaiter().GetResult();
    if (!listed.IsSuccess)
    {
      return [];
    }

    // The session COMMAND rides the same popup (issue #234): it is listed alongside the
    // skills but is not one — IsCommand sorts it after manual skills and before plain
    // ones, and the row renders the distinct [command] badge.
    return [.. listed.Value.Select(s => new SkillOption(s.Name, s.Description, s.Manual)),
        AgentSessionViewModel.NewSessionCommandOption];
  }

  /// <summary>Attaches a session as a tab at the END of the strip (the open/resume path).</summary>
  private Task<AgentTabViewModel> AttachSessionAsync(AgentSession session)
      => AttachSessionAsync(session, Tabs.Count);

  /// <summary>The indexed attach (issue #234): the in-place replacement inserts the new
  ///     tab where the old one was, so the strip does not reshuffle under the user. An index
  ///     outside the collection is clamped — a tab that vanished while a container was
  ///     building must not fault the attach.</summary>
  private async Task<AgentTabViewModel> AttachSessionAsync(AgentSession session, int index)
  {    // Self-referencing sink hook, the same pattern as the pre-tab window wiring:
    // the VM is captured after construction so its own sink marshals its events
    // onto the UI thread. An injected shell-level sink (tests) takes precedence.
    AgentSessionViewModel? sessionVmRef = null;
    // Out-of-band supervisory notices (host health, orphan repair) marshal onto the UI
    // thread — transcript mutation is UI-thread-only — and land as regular notices.
    session.NoticeSink = message => Avalonia.Threading.Dispatcher.UIThread.Post(
        () => sessionVmRef?.AddSystemNotice(message));
    AgentTabViewModel? attachedTab = null;
    // Issue #307 D6: the rail is the TAB's, so the shell builds ONE per tab and hands it to
    // every session view-model in it. A cell swap (a chain advance, /new) then rebuilds the
    // session without rebuilding a pane the user is reading.
    WorkbenchViewModel workbench = BuildWorkbench(session);
    AgentSessionViewModel sessionVm = BuildSessionViewModel(session, workbench,
        () => sessionVmRef ??
            throw new InvalidOperationException("session view-model not initialized"),
        id => sessionVmRef!.Status.ModelId = id,
        () => attachedTab);
    sessionVmRef = sessionVm;
    // Notices from construction (issue #317 D5/D6): a workspace that moved or was copied is
    // discovered before any sink existed. Deliver them now that one does - the user must learn
    // why their chains are where they are, and a notice nobody delivered is a notice nobody saw.
    session.DeliverPendingNotices();

    await RestorePinnedModelAsync(session, sessionVm);
    await RestoreSessionPreferencesAsync(session, sessionVm);
    // The cost seed runs before any turn can run in this tab (issue #24).
    await sessionVm.SeedUsageAsync().ConfigureAwait(true);

    // Resume replay: the persisted transcript (already hydrated into the session's
    // conversation) renders into the transcript view. A fresh session replays nothing.
    sessionVm.Transcript.Restore(session.Conversation.Messages);

    AgentTabViewModel tab = new(session, sessionVm, workbench);
    // Publish the tab to the view-model's new-session seams (issue #234): both resolve it
    // lazily because the tab is constructed FROM this view-model.
    attachedTab = tab;
    AttachTabWiring(session, tab);
    Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
    _sessionOpened?.Invoke(session);
    SelectedTab = tab;

    await PersistProviderPreferenceAsync(session.ProviderName).ConfigureAwait(false);
    return tab;
  }

  /// <summary>Builds ONE workbench rail for a workspace tab (issue #307 D6). The rail is
  ///     workspace-scoped - plans, git, PRs, and the database are the workspace's, not any one
  ///     agent's - so it is built per TAB and shared by every cell in it.
  ///     <para>Every view renders its own unavailable state when its seam is null, so a host
  ///     without one of these services gets a working rail rather than a blank pane.</para></summary>
  private static WorkbenchViewModel BuildWorkbench(AgentSession session) => new(
      new PlansViewViewModel(
          session.Services.GetService<PlanService>(),
          session.Services.GetService<IChangeSignal>(),
          render => Avalonia.Threading.Dispatcher.UIThread.Post(render),
          events: session.Services.GetService<IAgentEvents>()),
      // The git view watches the WORKSPACE ROOT (never .git): working-tree edits are the thing
      // it exists to show, and they never touch .git.
      new GitViewViewModel(
          session.WorkspaceRoot,
          session.Services.GetService<IGitQueryAccess>(),
          render => Avalonia.Threading.Dispatcher.UIThread.Post(render)),
      // The Progress view's accordion is the tab's too: it lists the WORKSPACE's child agents,
      // which every cell contributes to, so it cannot belong to one cell.
      BuildSubagentPanel(session),
      // The GitHub view (issue #10) polls the gh CLI on a long interval and ONLY while
      // visible: each read is a subprocess, and no push channel exists (design D6).
      new PullRequestPaneViewModel(
          session.WorkspaceRoot,
          session.Services.GetService<IGitHubAccess>(),
          render => Avalonia.Threading.Dispatcher.UIThread.Post(render)));

  /// <summary>The tab's live-progress accordion, subscribed to the session's child-event
  ///     stream. Events arrive on stream threads, so the marshal posts each application onto
  ///     the UI thread (the 884fc61 crash fix).</summary>
  private static SubagentPanelViewModel BuildSubagentPanel(AgentSession session)
  {
    SubagentPanelViewModel panel = new(session.Services.GetService<IAgentStore>());
    panel.Attach(session.Services.GetService<IAgentEvents>(),
        apply => evt => Avalonia.Threading.Dispatcher.UIThread.Post(() => apply(evt)));
    return panel;
  }

  /// <summary>Builds the session view-model with its self-referencing stream sink and
  ///     status hook. Both resolve the view-model lazily through the supplied accessors,
  ///     so the instance is captured after construction.
  ///     <para>The new-session seams (issue #234) are bound to the TAB the view-model ends
  ///     up in, and the tab does not exist yet — so both resolve it lazily through the same
  ///     accessor pattern, and both are no-ops until the tab is attached.</para></summary>
  private AgentSessionViewModel BuildSessionViewModel(AgentSession session,
      WorkbenchViewModel workbench, Func<AgentSessionViewModel> currentViewModel,
      Action<string> updateStatusModel, Func<AgentTabViewModel?> currentTab)
  {
    return new AgentSessionViewModel(
        // TurnRunner puts ct second; SendMessageCommandHandler.Handle keeps it last
        // (CA1068) — adapt the parameter order at the call site.
        (command, ct, callbacks, onNotice) => session.Handler.Handle(command, callbacks, onNotice, ct),
        session.Lifecycle,
        session.RootId,
        session.Conversation,
        Providers.DisplayName(session.ProviderName),
        session.ModelId,
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = session.WorkspaceRoot,
          SystemPrompt = session.SystemPrompt,
          UiStreamSink = _streamSink ?? (evt => currentViewModel()
              .ApplyUiStreamEventOnUIThreadAsync(evt)),
          Inbox = session.Inbox,
          ChildRuntime = session.ChildRuntime,
          StatusModelUpdater = updateStatusModel,
          ModelPreferences = session.Preferences,
          CommandRunner = session.CommandRunner,
          SkillInvocation = session.SkillInvocation,
          SkillCatalogSource = session.SkillInvocation is null
              ? null
              : () => SkillOptionsOf(session),
          // Issue #234: the /new slash command and the host's session-request both start a
          // session in THIS tab's workspace, in place. Both refuse while the tab is unknown
          // (the view-model is built before its tab exists).
          NewSessionCommand = prompt => currentTab() is { } newSessionTab
              ? StartNewSessionAsync(newSessionTab, prompt)
              : Task.CompletedTask,
          SessionRequestHandler = request => currentTab() is { } requestTab
              ? HandleSessionRequestAsync(requestTab, request)
              : Task.CompletedTask,
          ChildEvents = session.Services.GetService<IAgentEvents>(),
          AgentStore = session.Services.GetService<IAgentStore>(),
          // Spec D6: the app runs NO agent work. A container built with the wire runtime
          // (every production session) resolves the channel here and every turn travels to
          // this conversation's host; a headless/test container that opted into the
          // in-process runtime resolves none, and only then does the turn run locally.
          RootTurnChannel = session.Services.GetService<IRootTurnChannel>(),
          // Issue #24: the status bar's session cost is seeded from the persisted usage
          // rollup at tab open, so a resumed session starts from its real spend.
          UsageQueries = session.Services.GetService<IUsageQueries>(),
          // Issue #8: the workbench's Plans view reads plans through the session's own
          // store and detects the ChildHost's writes through the cross-process change
          // signal. A container missing either (a headless stub) renders the view's
          // unavailable state, never a blank pane.
          Plans = session.Services.GetService<PlanService>(),
          ChangeSignal = session.Services.GetService<IChangeSignal>(),
          // The INSTANT path for the Plans view: plan-change facts ride the session's
          // event stream, which the ChildHost forwards over the wire.
          Events = session.Services.GetService<IAgentEvents>(),
          // Issue #9: the workbench's Git view reads through the same read-only git
          // seam the statusline's branch watcher uses.
          Git = session.Services.GetService<IGitQueryAccess>(),
          // Issue #10: the GitHub view drives the gh CLI (design D6) - no API client and no
          // token storage, so a container without the seam renders the view's own unavailable
          // state instead of a blank pane.
          GitHub = session.Services.GetService<IGitHubAccess>(),
          // Issue #307 D6: the tab's own rail, shared by every cell in it.
          Workbench = workbench,
        });
  }

  /// <summary>Shows the record's pinned model in the status bar immediately. A session
  ///     whose record carries a real pin (a previous selection, or a user's picker
  ///     choice that already served a turn) must display it at once: the selection
  ///     notice fires only on the turn that CHANGES the pin, so a resumed or
  ///     second-turn session would otherwise display the bootstrap fallback forever.
  ///     Best effort — a store miss keeps the bootstrap id.</summary>
  private static async Task RestorePinnedModelAsync(AgentSession session, AgentSessionViewModel sessionVm)
  {
    // Named decision (CA1031): a status restore must never fail the session open.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<AgentRecord> pinned = await session.Services.GetRequiredService<IAgentStore>()
          .GetAsync(session.RootId, CancellationToken.None);
      if (pinned.IsSuccess
          && pinned.Value.ModelUsed is { } used
          && used != AgentRecord.UnassignedModel
          && used != Providers.FallbackModelId(session.ProviderName))
      {
        // The pin is what ACTUALLY served, so the session continues with it - and the
        // live PREFERENCE must name it, or the resolver would serve the session default
        // while the status bar showed the pin (2026-10-22: the display must never claim
        // a model the turn does not run on). A persisted live choice is restored after
        // this and still wins.
        if (session.Preferences is { } preferences)
        {
          preferences.ModelId = used;
        }

        sessionVm.Status.ModelId = used;
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"status model restore failed: {ex.Message}");
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Restores the per-workspace model, effort, sampling, and OpenRouter
  ///     settings BEFORE the tab can take a turn: they all ride the session
  ///     preferences, so the first turn resolves straight to them — no selection runs.
  ///     A stale persisted model id is not re-validated — that would crawl the whole
  ///     OpenRouter catalog on every open — it surfaces as a provider error on the
  ///     next turn and the user re-picks.</summary>
  private async Task RestoreSessionPreferencesAsync(AgentSession session, AgentSessionViewModel sessionVm)
  {
    if (session.Preferences is not { } preferences)
    {
      return;
    }

    ModelVariantChoice? restoredChoice = await ReadModelChoiceAsync(session.ProviderName, session.WorkspaceRoot);
    if (restoredChoice is not null)
    {
      // The pair restores together (issue #296, D7/B1): a reopened workspace serves the same
      // model ON the same provider, and the status line names both.
      preferences.ModelId = restoredChoice.ModelId;
      preferences.ModelVariant = restoredChoice.Variant == ModelVariant.Standard
          ? null
          : restoredChoice.Variant;
      sessionVm.Status.ModelId = restoredChoice.ModelId;
      sessionVm.Status.ServingVariant = restoredChoice.Variant == ModelVariant.Standard
          ? string.Empty
          : ModelVariants.Describe(restoredChoice.Variant);
    }

    ReasoningEffort? restoredEffort = await ReadEffortChoiceAsync(session.ProviderName, session.WorkspaceRoot);
    if (restoredEffort is { } effort)
    {
      preferences.ReasoningEffort = effort;
      sessionVm.Status.Effort = EffortLevels.DisplayName(effort);
    }

    // Restored sampling knobs and serialized OpenRouter settings ride the same
    // live preferences: the first turn resolves straight to them, no re-pick.
    IReadOnlyDictionary<string, string>? restoredKnobs =
        await ReadSamplingPrefsAsync(session.ProviderName, session.WorkspaceRoot);
    if (restoredKnobs is { } knobs)
    {
      ApplyRestoredKnobs(preferences, knobs);
    }

    string? restoredSettings = await ReadProviderSettingsAsync(session.ProviderName, session.WorkspaceRoot);
    if (restoredSettings is not null)
    {
      preferences.ProviderSettings = restoredSettings;
    }
  }

  /// <summary>Subscribes the tab's live surfaces: the unread-steering badge (W4.4 —
  ///     deliver raises, drain/settle clear; a subscription, never a poll) and the
  ///     statusline's git branch watcher (issue 1 — event-driven on .git changes; a
  ///     session without the git seam, none in production, skips it).</summary>
  private static void AttachTabWiring(AgentSession session, AgentTabViewModel tab)
  {
    tab.AttachBadge(session.Services.GetService<IAgentEvents>());
    tab.AttachBranchWatcher(session.Services.GetService<IGitQueryAccess>());
  }

  /// <summary>Menu-bar entry point: raises the Sessions request. The view shows the
  ///     Sessions dialog and calls <see cref="ResumeSessionAsync"/> with the pick.</summary>
  public void RequestOpenSessions() => SessionsRequested?.Invoke(this, EventArgs.Empty);

  /// <summary>Loads the Sessions catalog for the dialog, or null when the host wired no
  ///     shared store (the dialog is then not shown).</summary>
  public Func<CancellationToken, Task<Result<IReadOnlyList<SessionCatalogEntry>>>>? SessionCatalogLoader
      => _sessionCatalog is null ? null : ct => _sessionCatalog.ListAsync(ct);

  /// <summary>The selected tab's MCP seams (issue #106): the store the dialog's form
  ///     writes through, the SHARED pooled access its status view reads (the same
  ///     object the session's mcp tool dispatches through), and the workspace id the
  ///     store scopes rows by. Null when no tab is selected (the menu entry is hidden
  ///     then) or the host wired no MCP store (headless stubs).</summary>
  public Func<(IMcpServerStore Store, IMcpServerAccess Access, string WorkspaceId)>? SelectedMcpServersLoader
  {
    get
    {
      IMcpServerStore? store = SelectedTab?.Container.Services.GetService<IMcpServerStore>();
      IMcpServerAccess? access = SelectedTab?.Container.Services.GetService<IMcpServerAccess>();
      return store is null || access is null
          ? null
          : () => (store, access, SelectedTab!.Container.Services
              .GetRequiredService<IWorkspaceContext>().WorkspaceId);
    }
  }

  /// <summary>The ids of the sessions currently open in tabs — the Sessions dialog
  ///     greys these out, since resuming an open session would fork it.</summary>
  public IReadOnlySet<AgentId> OpenSessionIds =>
      Tabs.Select(t => t.Container.RootId).ToHashSet();

  /// <summary>Remembers the chosen provider as the next dialog's default. Best effort:
  ///     the preference only seeds a default, so a failed write never fails the open —
  ///     it logs to stderr instead (named decision, CA1031 scope kept tight).</summary>
  private async Task PersistProviderPreferenceAsync(string providerName)
  {
    if (_preferences is null)
    {
      return;
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      bool persisted = await _preferences.SetAsync(Providers.PreferenceKey, providerName);
      if (!persisted)
      {
        await Console.Error.WriteLineAsync($"provider preference write failed for '{providerName}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"provider preference write failed for '{providerName}': {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Composite preference key of the per-workspace model + provider choice: the
  ///     same directory may be open under both providers, and their lineups differ, so the
  ///     key is scoped to the (provider, workspace) pair. Keys are only ever read back
  ///     verbatim — never parsed — so path colons need no escaping.
  ///     <para>THE PAIR LIVES UNDER ITS OWN KEY (issue #296, D7). The former
  ///     <c>model_choice</c> key held a bare model id; it is still READ (the model comes back
  ///     and the provider stays unset) and is dropped on the next commit, so no migration
  ///     pass is needed and no session silently gains a provider it never chose.</para></summary>
  private static string ModelChoiceKey(string providerName, string workspaceRoot)
      => $"model_provider_choice:{providerName}:{workspaceRoot}";

  /// <summary>The retired key that stored a bare model id. Read-only now: a value found here
  ///     restores the MODEL and leaves the provider unset (issue #296, D7's legacy rule),
  ///     because a provider the user never chose must never be invented for them.</summary>
  private static string LegacyModelChoiceKey(string providerName, string workspaceRoot)
      => $"model_choice:{providerName}:{workspaceRoot}";

  /// <summary>Reads the per-workspace model + routing-variant choice, or null when unset (or
  ///     the store is unavailable - a failed read degrades to the session default, never fails
  ///     the open; named decision, CA1031).
  ///     <para>THE LEGACY KEY IS THE FALLBACK, NOT A SECOND SOURCE: a workspace that has not
  ///     committed a choice since this change still restores its MODEL from the retired key. The
  ///     codec drops the retired provider member and reads a variant suffix on the id, so an old
  ///     row comes back as a model on Standard - never a resurrected pin, and never a silently
  ///     dropped model.</para></summary>
  private async Task<ModelVariantChoice?> ReadModelChoiceAsync(string providerName, string workspaceRoot)
  {
    if (_preferences is null)
    {
      return null;
    }

    // Named decision (CA1031): preference reads must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string? stored = await _preferences.GetAsync(ModelChoiceKey(providerName, workspaceRoot))
          ?? await _preferences.GetAsync(LegacyModelChoiceKey(providerName, workspaceRoot));
      if (stored is null)
      {
        return null;
      }

      Result<ModelVariantChoice> parsed = ModelVariantChoice.Parse(stored);
      return parsed.IsSuccess ? parsed.Value : null;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"model choice preference read failed: {ex.Message}");
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>Persists (or, for the default choice, clears) the per-workspace model + routing-
  ///     variant choice. Best effort - a failed write logs to stderr and never fails the pick
  ///     (named decision, CA1031; same reasoning as the provider preference).
  ///     <para>THE RETIRED KEY IS DROPPED ON EVERY COMMIT: leaving it behind would let a
  ///     cleared choice come back from the legacy value on the next open, which is the silent
  ///     resurrection the null-deletes contract exists to prevent.</para></summary>
  private async Task PersistModelChoiceAsync(string providerName, string workspaceRoot,
      string? modelId, ModelVariant? variant)
  {
    if (_preferences is null)
    {
      return;
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string key = ModelChoiceKey(providerName, workspaceRoot);
      bool landed = modelId is null
          ? await _preferences.DeleteAsync(key)
          : await _preferences.SetAsync(key,
              new ModelVariantChoice(modelId, variant ?? ModelVariant.Standard).Serialize());
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"model choice preference write failed for '{key}'");
      }

      _ = await _preferences.DeleteAsync(LegacyModelChoiceKey(providerName, workspaceRoot));
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"model choice preference write failed: {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Composite preference key of the per-workspace effort choice: the same
  ///     directory may be open under both providers, so the key is scoped to the
  ///     (provider, workspace) pair. The key itself is only ever read verbatim — never
  ///     parsed — so path colons need no escaping; the stored value (an enum name) is
  ///     what the read path parses.</summary>
  private static string EffortChoiceKey(string providerName, string workspaceRoot)
      => $"effort_choice:{providerName}:{workspaceRoot}";

  /// <summary>Reads the per-workspace effort choice, or null when unset. A stale or
  ///     corrupt stored value degrades to null (the provider default) — it is never
  ///     coerced to a level. Also null when the store is unavailable — a failed read
  ///     never fails the open (named decision, CA1031).</summary>
  private async Task<ReasoningEffort?> ReadEffortChoiceAsync(string providerName, string workspaceRoot)
  {
    if (_preferences is null)
    {
      return null;
    }

    // Named decision (CA1031): preference reads must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string? stored = await _preferences.GetAsync(EffortChoiceKey(providerName, workspaceRoot));
      return Enum.TryParse(stored, ignoreCase: true, out ReasoningEffort effort)
          ? effort
          : null;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"effort choice preference read failed: {ex.Message}");
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>Persists (or, for the model default, clears) the per-workspace effort
  ///     choice. Best effort — a failed write logs to stderr and never fails the pick
  ///     (named decision, CA1031; same reasoning as the provider preference).</summary>
  private async Task PersistEffortChoiceAsync(string providerName, string workspaceRoot, ReasoningEffort? effort)
  {
    if (_preferences is null)
    {
      return;
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string key = EffortChoiceKey(providerName, workspaceRoot);
      bool landed = effort is null
          ? await _preferences.DeleteAsync(key)
          : await _preferences.SetAsync(key, effort.Value.ToString());
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"effort choice preference write failed for '{key}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"effort choice preference write failed: {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Composite preference key of the per-workspace sampling knobs: the
  ///     same directory may be open under both providers, so the key is scoped to
  ///     the (provider, workspace) pair — the ModelChoiceKey derivation style. The
  ///     value is the JSON map of knob-name to entered text (empty text = unset);
  ///     keys are only ever read back verbatim — never parsed for structure.</summary>
  private static string SamplingPrefsKey(string providerName, string workspaceRoot)
      => $"sampling_prefs:{providerName}:{workspaceRoot}";

  /// <summary>Composite preference key of the per-workspace serialized OpenRouter
  ///     request settings (same scoping rule). Null value deletes the key.</summary>
  private static string ProviderSettingsKey(string providerName, string workspaceRoot)
      => $"provider_settings:{providerName}:{workspaceRoot}";

  /// <summary>Applies restored knob texts onto the live preferences: only the
  ///     ten typed knobs parse here; temperature and maxTokens live in the map
  ///     for the settings window's prefill and are validated/typed on the next
  ///     save (a corrupt stored value degrades to unset — never coerced).</summary>
  private static void ApplyRestoredKnobs(SessionModelPreferences preferences, IReadOnlyDictionary<string, string> knobs)
  {
    ApplySamplingKnobs(preferences, knobs);
    ApplyPenaltyKnobs(preferences, knobs);
    ApplyOutputKnobs(preferences, knobs);
  }

  /// <summary>Samples-per-token knobs: temperature, the two token caps, and the two
  ///     nucleus/pool cutoffs. A corrupt stored value degrades to unset.</summary>
  private static void ApplySamplingKnobs(SessionModelPreferences preferences, IReadOnlyDictionary<string, string> knobs)
  {
    preferences.Temperature = ParseFloat(knobs, "temperature") ?? preferences.Temperature;
    preferences.MaxTokens = ParseInt(knobs, "maxTokens") ?? preferences.MaxTokens;
    preferences.TopP = ParseFloat(knobs, "topP") ?? preferences.TopP;
    preferences.TopK = ParseInt(knobs, "topK") ?? preferences.TopK;
    preferences.MinP = ParseFloat(knobs, "minP") ?? preferences.MinP;
    preferences.TopA = ParseFloat(knobs, "topA") ?? preferences.TopA;
  }

  /// <summary>Penalty knobs: the frequency, presence, and repetition scales.</summary>
  private static void ApplyPenaltyKnobs(SessionModelPreferences preferences, IReadOnlyDictionary<string, string> knobs)
  {
    preferences.FrequencyPenalty = ParseFloat(knobs, "frequencyPenalty") ?? preferences.FrequencyPenalty;
    preferences.PresencePenalty = ParseFloat(knobs, "presencePenalty") ?? preferences.PresencePenalty;
    preferences.RepetitionPenalty = ParseFloat(knobs, "repetitionPenalty") ?? preferences.RepetitionPenalty;
  }

  /// <summary>Determinism and output-shape knobs: the seed, the verbosity level, and the
  ///     parallel-tool-calls switch.</summary>
  private static void ApplyOutputKnobs(SessionModelPreferences preferences, IReadOnlyDictionary<string, string> knobs)
  {
    preferences.Seed = ParseInt(knobs, "seed") ?? preferences.Seed;
    preferences.Verbosity = ParseVerbosity(knobs) ?? preferences.Verbosity;
    preferences.ParallelToolCalls = ParseBool(knobs, "parallelToolCalls") ?? preferences.ParallelToolCalls;
  }

  private static float? ParseFloat(IReadOnlyDictionary<string, string> knobs, string name)
      => knobs.TryGetValue(name, out string? text) && float.TryParse(text, CultureInfo.InvariantCulture, out float value)
          ? value
          : null;

  private static int? ParseInt(IReadOnlyDictionary<string, string> knobs, string name)
      => knobs.TryGetValue(name, out string? text) && int.TryParse(text, CultureInfo.InvariantCulture, out int value)
          ? value
          : null;

  private static bool? ParseBool(IReadOnlyDictionary<string, string> knobs, string name)
      => knobs.TryGetValue(name, out string? text) && bool.TryParse(text, out bool value) ? value : null;

  private static VerbosityLevel? ParseVerbosity(IReadOnlyDictionary<string, string> knobs)
  {
    // The stored text is the settings window's lowercase wire spelling (low/medium/
    // high/xhigh/max); parse case-insensitively so any casing round-trips. Unknown
    // or corrupt values degrade to unset — never coerced (named decision).
    return knobs.TryGetValue("verbosity", out string? text)
        && Enum.TryParse(text, ignoreCase: true, out VerbosityLevel value)
        ? value
        : null;
  }

  /// <summary>Loads the selected tab's persisted sampling knob texts for the
  ///     Model Settings window's prefill (null when no tab or nothing stored).
  ///     </summary>
  public async Task<IReadOnlyDictionary<string, string>?> ReadSamplingPrefsForSelectedTabAsync()
  {
    AgentTabViewModel? tab = SelectedTab;
    return tab is null
        ? null
        : await ReadSamplingPrefsAsync(tab.Container.ProviderName, tab.Container.WorkspaceRoot);
  }

  /// <summary>Persists (or, for the all-unset map, clears) the per-workspace
  ///     sampling knob texts. Best effort — a failed write logs to stderr and
  ///     never fails the save (named decision, CA1031).</summary>
  private async Task PersistSamplingPrefsAsync(string providerName, string workspaceRoot, string? knobPayload)
  {
    if (_preferences is null)
    {
      return;
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string key = SamplingPrefsKey(providerName, workspaceRoot);
      bool landed = knobPayload is null
          ? await _preferences.DeleteAsync(key)
          : await _preferences.SetAsync(key, knobPayload);
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"sampling prefs write failed for '{key}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"sampling prefs write failed: {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Persists (or clears) the per-workspace serialized OpenRouter request
  ///     settings. Best effort, same named decision.</summary>
  private async Task PersistProviderSettingsAsync(string providerName, string workspaceRoot, string? settingsJson)
  {
    if (_preferences is null)
    {
      return;
    }

    // Named decision (CA1031): preference persistence must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string key = ProviderSettingsKey(providerName, workspaceRoot);
      bool landed = settingsJson is null
          ? await _preferences.DeleteAsync(key)
          : await _preferences.SetAsync(key, settingsJson);
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"provider settings write failed for '{key}'");
      }
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"provider settings write failed: {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Reads the persisted sampling knob texts, or null when unset (or the
  ///     store is unavailable, or the payload is corrupt — a failed read degrades
  ///     to unset, never fails the open; named decision, CA1031).</summary>
  private async Task<IReadOnlyDictionary<string, string>?> ReadSamplingPrefsAsync(string providerName, string workspaceRoot)
  {
    if (_preferences is null)
    {
      return null;
    }

    // Named decision (CA1031): preference reads must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string? stored = await _preferences.GetAsync(SamplingPrefsKey(providerName, workspaceRoot));
      return stored is null
          ? null
          : JsonSerializer.Deserialize<Dictionary<string, string>>(stored);
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"sampling prefs read failed: {ex.Message}");
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>Reads the persisted serialized OpenRouter request settings and parses
  ///     it through the ACL's typed parser, or null when unset. Malformed stored JSON
  ///     degrades to unset — the same corrupt-to-unset named decision the knob map
  ///     and the effort choice apply — so a corrupt payload never reaches a turn
  ///     (the provider would fail the whole request over it); a failed read likewise
  ///     degrades to unset and never fails the open (named decision, CA1031).
  ///     </summary>
  private async Task<string?> ReadProviderSettingsAsync(string providerName, string workspaceRoot)
  {
    if (_preferences is null)
    {
      return null;
    }

    // Named decision (CA1031): preference reads must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      string? stored = await _preferences.GetAsync(ProviderSettingsKey(providerName, workspaceRoot));
      if (stored is null)
      {
        return null;
      }

      // Parse throws JsonException on malformed storage; the model-facing wire path
      // fails the whole request over such a payload, so restore refuses it up front.
      return OpenRouterRequestSettings.Parse(stored) is { } parsed
          ? OpenRouterRequestSettings.Serialize(parsed)
          : null;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"provider settings read failed: {ex.Message}");
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>Closes a tab: completes EVERY cell's root session gracefully (best effort),
  ///     then removes the tab and disposes every container. Selection falls to the last
  ///     remaining tab; closing the final tab leaves the empty shell with the menu bar.
  ///     <para>Issue #307: a tab holds 1..N agents, so closing it must retire all of them or
  ///     the ones past the primary would keep a live host process with no surface. The tab's
  ///     own rail is disposed here too - it is the tab's, not any cell's (D6).</para></summary>
  public async Task CloseTabAsync(AgentTabViewModel tab)
  {
    ArgumentNullException.ThrowIfNull(tab);
    if (!Tabs.Contains(tab))
    {
      return;
    }

    // Snapshot BEFORE teardown: a cell swap during shutdown must not change what is closed.
    List<AgentCellViewModel> cells = [.. tab.Cells];
    foreach (AgentCellViewModel cell in cells)
    {
      // After the guard: a stale double-close must not detach a NEWER watchdog bound to
      // the same root id (a closed session becomes resumable, so the id can be live again).
      _sessionClosed?.Invoke(cell.Container.RootId);

      // Named decision (CA1031): teardown is best effort — a failing persistence
      // write must not prevent the tab from closing.
      try
      {
        await cell.ViewModel.ShutdownAsync();
      }
#pragma warning disable CA1031 // Do not catch general exception types
      catch { /* teardown never throws */ }
#pragma warning restore CA1031
    }

    tab.Badge?.Dispose();
    tab.DetachBranchWatcher();
    // Issue #307 D6: the rail belongs to the tab, so the tab's close is what stops its
    // pollers. A cell's shutdown deliberately leaves a SHARED rail running.
    tab.Workbench.Dispose();
    _ = Tabs.Remove(tab);
    SelectedTab = Tabs.LastOrDefault();

    foreach (AgentCellViewModel cell in cells)
    {
      await cell.Container.Services.DisposeAsync();
    }
  }

  /// <summary>Synchronous fire-and-forget close used by the tab header's close button.
  ///     Teardown errors are swallowed inside <see cref="CloseTabAsync"/>.</summary>
  public void CloseTab(AgentTabViewModel tab) => _ = CloseTabAsync(tab);

  /// <summary>Shuts the WHOLE app down (issue #274): every open tab's session is stopped
  ///     and every session container is disposed, which is what kills the processes those
  ///     sessions own - the conversation host, the computer-use broker, MCP stdio servers,
  ///     and any exec-spawned tree still alive.
  ///     <para>WHY THIS EXISTS. Closing the window used to run only the graceful session
  ///     completion pass; container disposal lived in the TAB-close paths, so the app
  ///     exited with its containers undisposed and every host process survived it.
  ///     <see cref="RemoteHostSupervisor.DisposeAsync"/> never ran.</para>
  ///     <para>CLOSING IS A DECISION TO KILL EVERYTHING (the user's ruling on #274): an
  ///     in-flight turn is INTERRUPTED rather than awaited, because a close that waits on a
  ///     long turn reads as a hang. The transcript up to the last safe point is already
  ///     persisted, and the root row is still completed gracefully, so the conversation
  ///     stays resumable from the Sessions catalog.</para>
  ///     <para>Best effort per tab, matching every other teardown path: one session's
  ///     failure must never leave the others running. Must run on the UI thread.</para></summary>
  public async Task ShutdownAllAsync()
  {
    List<AgentTabViewModel> tabs = [.. Tabs];
    foreach (AgentTabViewModel tab in tabs)
    {
      // Stop the turn FIRST: the container disposal below tears down the transport the
      // turn is streaming over, and an interrupted turn settles cleanly where a severed
      // one would fault. RequestStop is a no-op when the tab is idle.
      try
      {
        tab.ViewModel.RequestStop();
      }
#pragma warning disable CA1031 // Named decision: teardown is best effort, like CloseTabAsync.
      catch
#pragma warning restore CA1031
      {
        // Swallowed deliberately: see the named decision above.
      }

      // The tab-close path already owns 'complete the session, dispose the container'.
      // Reusing it means app shutdown and tab close can never drift apart.
      await CloseTabAsync(tab).ConfigureAwait(true);
    }
  }

  /// <summary>Single-session convenience: a shell whose only tab opens over a
  ///     pre-built session (used by hosts/tests that compose the session themselves).</summary>
  public static async Task<MainViewModel> ForPrebuiltSessionAsync(AgentSession session,
      Func<UiStreamEvent, Task>? uiStreamSink = null)
  {
    ArgumentNullException.ThrowIfNull(session);
    ProviderOption option = new(session.ProviderName, Providers.DisplayName(session.ProviderName));
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Success(session)),
        new MainViewModelOptions
        {
          AvailableProviders = [option],
          PreferredProviderId = session.ProviderName,
          UiStreamSink = uiStreamSink,
        });
    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(session.WorkspaceRoot, session.ProviderName);
    return !opened.IsSuccess
        ? throw new InvalidOperationException($"prebuilt session failed to open: [{opened.Error.Code}] {opened.Error.Message}")
        : vm;
  }
}
