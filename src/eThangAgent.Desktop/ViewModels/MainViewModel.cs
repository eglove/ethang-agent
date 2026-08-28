using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.ViewModels;

internal delegate Task<Result<string>> TurnRunner(
    SendMessageCommand command,
    CancellationToken ct,
    TurnCallbacks? callbacks,
    Action<string>? onNotice = null);

/// <summary>Optional shell configuration for <see cref="MainViewModel"/>. Every member
///     is an optional seam or default override whose null behaves exactly as the
///     former absent parameter: providers fall back to the built-in list, the dialog's
///     pre-selection to the first configured provider, the preference store and key
///     protector remember/persist nothing, the stream sink falls back to the session
///     view-model's own marshaling, and a null settings/factory pair disables settings
///     editing (prebuilt-session hosts own their configuration).</summary>
internal sealed record MainViewModelOptions
{
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
}

/// <summary>Shell-level state for the main window: the left menu bar (Open Agent, the
///     per-tab Model and Effort entries, and the bottom-anchored Settings entry) and
///     the open agent tabs. 'Open Agent' shows the new-agent dialog (provider dropdown
///     plus workspace picker); each tab owns an <see cref="AgentSessionViewModel"/>
///     bound to its own isolated <see cref="AgentSession"/> created through the
///     injected provider-aware session-factory hook. The Model entry shows the selected
///     tab's model picker and the Effort entry its effort picker: confirming applies
///     the choice to the session (from the next turn, root and children alike) and
///     persists it per workspace + provider, so reopening the same directory restores
///     it. The settings modal edits the provider API keys: saving persists them
///     (protected) and rebuilds the factory so future opens use the new keys —
///     already-open tabs keep the credentials they were created with. The shell itself
///     holds no agent state. A static <see cref="ForPrebuiltSessionAsync"/> keeps
///     single-session hosts and tests simple while tabs remain the primary surface.</summary>
internal sealed partial class MainViewModel : ObservableObject
{
  // The delegate reads _sessionFactory at invocation time, so a settings-save rebind
  // reaches future opens without touching this field.
  private readonly Func<string, string, Task<Result<AgentSession>>> _createSession;

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

  /// <summary>Factory rebound via <see cref="AgentSessionFactory.WithSettings"/> when
  ///     saved keys change; the creation delegate always reads the current instance.</summary>
  private AgentSessionFactory? _sessionFactory;

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

  public bool HasTabs => Tabs.Count > 0;

  /// <summary>True when a tab is selected — drives the Model and Effort menu entries'
  ///     visibility (every provider has both pickers).</summary>
  public bool HasSelectedTab => SelectedTab is not null;

  /// <summary>True when at least one provider has a configured API key; gates Open Agent.</summary>
  public bool HasConfiguredProvider => AvailableProviders.Count > 0;

  /// <summary>Keys currently configured — the settings dialog's prefill (null when
  ///     no key is set or settings editing is disabled).</summary>
  public string? ConfiguredOpenRouterKey => _settings?.OpenRouter.ApiKey;

  public string? ConfiguredZaiKey => _settings?.Zai.ApiKey;

  public IRelayCommand OpenAgentCommand { get; }

  public IRelayCommand OpenSettingsCommand { get; }

  public IRelayCommand ChooseModelCommand { get; }

  public IRelayCommand ChooseEffortCommand { get; }

  /// <summary>Raised when the shell wants the new-agent dialog shown.</summary>
  public event EventHandler? OpenAgentRequested;

  /// <summary>Raised when the shell wants the settings modal shown.</summary>
  public event EventHandler? SettingsRequested;

  /// <summary>Raised when the shell wants the selected tab's model picker shown.</summary>
  public event EventHandler? ModelPickerRequested;

  /// <summary>Raised when the shell wants the selected tab's effort picker shown.</summary>
  public event EventHandler? EffortPickerRequested;

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
    if (createSession is null && (options?.Settings is null || options.SessionFactory is null))
    {
      throw new ArgumentException(
          "Either a session-creation delegate or both settings and a session factory are required.",
          nameof(createSession));
    }

    _preferences = options?.Preferences;
    _streamSink = options?.UiStreamSink;
    _settings = options?.Settings;
    _sessionFactory = options?.SessionFactory;
    _keyProtector = options?.ApiKeyProtector;
    _createSession = createSession ?? ((root, provider) =>
        _sessionFactory!.CreateAsync(root, provider, new AvaloniaClarifyChannel(null)));

    // Commands exist before the observable properties: setting those raises the
    // changed hooks, which requery command availability.
    OpenAgentCommand = new RelayCommand(
        () => OpenAgentRequested?.Invoke(this, EventArgs.Empty),
        () => !IsOpeningAgent && HasConfiguredProvider);
    OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    ChooseModelCommand = new RelayCommand(
        () => ModelPickerRequested?.Invoke(this, EventArgs.Empty),
        () => HasSelectedTab);
    ChooseEffortCommand = new RelayCommand(
        () => EffortPickerRequested?.Invoke(this, EventArgs.Empty),
        () => HasSelectedTab);

    AvailableProviders = _settings is not null
        ? ProvidersFrom(_settings)
        : options?.AvailableProviders ?? [new(Providers.OpenRouter, Providers.DisplayName(Providers.OpenRouter))];
    string preferred = options?.PreferredProviderId
        ?? (AvailableProviders.Count > 0 ? AvailableProviders[0].Id : Providers.OpenRouter);
    PreferredProviderId = ResolvePreferredProviderId(preferred);
    Tabs.CollectionChanged += OnTabsChanged;
  }

  /// <summary>Guard-style early returns: the requested provider when it is among the
  /// configured ones, else the first configured provider, else the OpenRouter default.</summary>
  private string ResolvePreferredProviderId(string preferred)
  {
    if (AvailableProviders.Any(p => p.Id == preferred))
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
    ChooseModelCommand.NotifyCanExecuteChanged();
    ChooseEffortCommand.NotifyCanExecuteChanged();
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

  /// <summary>Menu-bar entry point: raises the model-picker request. The view shows the
  ///     picker modal and calls <see cref="ApplyModelChoiceAsync"/> with the choice.</summary>
  public void RequestChooseModel() => ModelPickerRequested?.Invoke(this, EventArgs.Empty);

  /// <summary>Menu-bar entry point: raises the effort-picker request. The view shows the
  ///     picker modal and calls <see cref="ApplyEffortChoiceAsync"/> with the choice.</summary>
  public void RequestChooseEffort() => EffortPickerRequested?.Invoke(this, EventArgs.Empty);

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

  /// <summary>Applies a model-picker choice to the selected tab and persists it per
  ///     workspace + provider (best effort — the same named decision as the other
  ///     preferences). Null means auto choice and clears the persisted preference, so
  ///     future opens of the same directory start on automatic resolution.</summary>
  public async Task ApplyModelChoiceAsync(string? modelId)
  {
    AgentTabViewModel? tab = SelectedTab;
    if (tab is null)
    {
      return;
    }

    tab.ViewModel.ApplyModelChoice(modelId);
    await PersistModelChoiceAsync(tab.Container.ProviderName, tab.Container.WorkspaceRoot, modelId);
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

  /// <summary>Applies a settings-modal result: persists the keys (protected) or deletes
  ///     the cleared ones, rebuilds the session factory so future opens use the new
  ///     keys, and refreshes the provider surface. Already-open tabs keep the
  ///     credentials they were created with. The update carries FULL field state, not a
  ///     delta; values are normalized defensively here (trimmed, blank → cleared) — the
  ///     stricter no-internal-whitespace rule is the modal's boundary. A no-op for hosts
  ///     that compose sessions themselves (no settings snapshot). Persistence is best
  ///     effort — the same named decision as the provider preference.</summary>
  public async Task ApplySettingsAsync(SettingsUpdate update)
  {
    ArgumentNullException.ThrowIfNull(update);
    if (_settings is null)
    {
      return;
    }

    string? openRouterKey = Normalize(update.OpenRouterApiKey);
    string? zaiKey = Normalize(update.ZaiApiKey);

    await PersistApiKeyAsync(OpenRouterSettings.PreferenceKey, openRouterKey);
    await PersistApiKeyAsync(ZaiSettings.PreferenceKey, zaiKey);

    _settings = _settings.WithApiKeys(openRouterKey, zaiKey);
    _sessionFactory = _sessionFactory?.WithSettings(_settings);

    AvailableProviders = ProvidersFrom(_settings);
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

  private static List<ProviderOption> ProvidersFrom(AgentSettings settings)
  {
    List<ProviderOption> providers = [];
    if (settings.HasOpenRouter)
    {
      providers.Add(new ProviderOption(Providers.OpenRouter, Providers.DisplayName(Providers.OpenRouter)));
    }
    if (settings.HasZai)
    {
      providers.Add(new ProviderOption(Providers.Zai, Providers.DisplayName(Providers.Zai)));
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
    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return Result.Failure<AgentTabViewModel>(new DomainError("InvalidWorkspace",
          "workspace root must be a non-empty directory path."));
    }

    string full = Path.GetFullPath(workspaceRoot);
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
      if (!created.IsSuccess)
      {
        return Result.Failure<AgentTabViewModel>(created.Error);
      }

      AgentSession session = created.Value;

      // Self-referencing sink hook, the same pattern as the pre-tab window wiring:
      // the VM is captured after construction so its own sink marshals its events
      // onto the UI thread. An injected shell-level sink (tests) takes precedence.
      AgentSessionViewModel? sessionVmRef = null;
      AgentSessionViewModel sessionVm = new(
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
            UiStreamSink = _streamSink ?? (evt => (sessionVmRef ??
                throw new InvalidOperationException("session view-model not initialized"))
                .ApplyUiStreamEventOnUIThreadAsync(evt)),
            Inbox = session.Inbox,
            ChildRuntime = session.ChildRuntime,
            StatusModelUpdater = id => sessionVmRef!.Status.ModelId = id,
            ModelPreferences = session.Preferences,
          });
      sessionVmRef = sessionVm;

      // Restore the per-workspace model and effort choices (if any) BEFORE the tab can
      // take a turn: both ride the session preferences, so the first turn resolves
      // straight to them — no selection runs. A stale persisted model id is not
      // re-validated — that would crawl the whole OpenRouter catalog on every open —
      // it surfaces as a provider error on the next turn and the user re-picks.
      if (session.Preferences is { } preferences)
      {
        string? restoredChoice = await ReadModelChoiceAsync(providerName, full);
        if (restoredChoice is not null)
        {
          preferences.ModelId = restoredChoice;
          sessionVm.Status.ModelId = restoredChoice;
        }

        ReasoningEffort? restoredEffort = await ReadEffortChoiceAsync(providerName, full);
        if (restoredEffort is { } effort)
        {
          preferences.ReasoningEffort = effort;
          sessionVm.Status.Effort = EffortLevels.DisplayName(effort);
        }
      }

      AttachClarifyChannel(sessionVm, session.ClarifyChannel);

      AgentTabViewModel tab = new(session, sessionVm);
      Tabs.Add(tab);
      SelectedTab = tab;

      await PersistProviderPreferenceAsync(providerName).ConfigureAwait(false);
      return Result.Success(tab);
    }
    finally
    {
      IsOpeningAgent = false;
    }
  }

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

  /// <summary>Composite preference key of the per-workspace model choice: the same
  ///     directory may be open under both providers, and their lineups differ, so the
  ///     key is scoped to the (provider, workspace) pair. Keys are only ever read back
  ///     verbatim — never parsed — so path colons need no escaping.</summary>
  private static string ModelChoiceKey(string providerName, string workspaceRoot)
      => $"model_choice:{providerName}:{workspaceRoot}";

  /// <summary>Reads the per-workspace model choice, or null when unset (or the store
  ///     is unavailable — a failed read degrades to the session default, never fails
  ///     the open; named decision, CA1031).</summary>
  private async Task<string?> ReadModelChoiceAsync(string providerName, string workspaceRoot)
  {
    if (_preferences is null)
    {
      return null;
    }

    // Named decision (CA1031): preference reads must not take the shell down.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await _preferences.GetAsync(ModelChoiceKey(providerName, workspaceRoot));
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"model choice preference read failed: {ex.Message}");
      return null;
    }
#pragma warning restore CA1031
  }

  /// <summary>Persists (or, for the auto choice, clears) the per-workspace model
  ///     choice. Best effort — a failed write logs to stderr and never fails the pick
  ///     (named decision, CA1031; same reasoning as the provider preference).</summary>
  private async Task PersistModelChoiceAsync(string providerName, string workspaceRoot, string? modelId)
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
          : await _preferences.SetAsync(key, modelId);
      if (!landed)
      {
        await Console.Error.WriteLineAsync($"model choice preference write failed for '{key}'");
      }
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

  /// <summary>Closes a tab: completes its root session gracefully (best effort), then
  ///     removes it and disposes its container. Selection falls to the last remaining
  ///     tab; closing the final tab leaves the empty shell with the menu bar.</summary>
  public async Task CloseTabAsync(AgentTabViewModel tab)
  {
    ArgumentNullException.ThrowIfNull(tab);
    if (!Tabs.Contains(tab))
    {
      return;
    }

    // Named decision (CA1031): teardown is best effort — a failing persistence
    // write must not prevent the tab from closing.
    try
    {
      await tab.ViewModel.ShutdownAsync();
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch { /* teardown never throws */ }
#pragma warning restore CA1031
    _ = Tabs.Remove(tab);
    SelectedTab = Tabs.LastOrDefault();
    await tab.Container.Services.DisposeAsync();
  }

  /// <summary>Synchronous fire-and-forget close used by the tab header's close button.
  ///     Teardown errors are swallowed inside <see cref="CloseTabAsync"/>.</summary>
  public void CloseTab(AgentTabViewModel tab) => _ = CloseTabAsync(tab);

  private static void AttachClarifyChannel(AgentSessionViewModel vm, IClarifyChannel channel)
  {
    // Mirror the pre-tab wiring: the desktop channel resolves its presenter lazily,
    // marshals onto the UI thread, and presents through THIS tab's view-model so
    // each pending question renders inside its own agent tab.
    if (channel is AvaloniaClarifyChannel desktop)
    {
      desktop.SetPresenter(q => PresentOnUIThread(() => vm.PresentClarifyAsync(q)));
    }
  }

  private static async Task<ClarifyViewModel> PresentOnUIThread(
      Func<Task<ClarifyViewModel>> present)
  {
    TaskCompletionSource<ClarifyViewModel> tcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
    {
      // Named decision (CA1031): any presentation fault is forwarded to the TCS so
      // the awaiting channel receives a well-formed failure, never an unobserved one.
      try
      {
        tcs.SetResult(await present());
      }
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
      {
        tcs.SetException(ex);
      }
#pragma warning restore CA1031
    });
    return await tcs.Task;
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
