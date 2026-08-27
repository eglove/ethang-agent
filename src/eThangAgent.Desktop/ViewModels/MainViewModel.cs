using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

internal delegate Task<Result<string>> TurnRunner(
    SendMessageCommand command,
    CancellationToken ct,
    Action<string>? onContentDelta,
    Action<string>? onReasoningDelta,
    Action? onIterationEnd,
    Action<string, string>? onToolCall,
    Action<string, string>? onToolResult,
    Action<string>? onNotice = null);

/// <summary>Shell-level state for the main window: the left menu bar and the open
///     agent tabs. 'Open Agent' shows the new-agent dialog (provider dropdown plus
///     workspace picker); each tab owns an <see cref="AgentSessionViewModel"/> bound
///     to its own isolated <see cref="AgentSession"/> created through the injected
///     provider-aware session-factory hook. The shell itself holds no agent state.
///     A static <see cref="ForPrebuiltSessionAsync"/> keeps single-session hosts and
///     tests simple while tabs remain the primary surface.</summary>
internal sealed partial class MainViewModel : ObservableObject
{
  private readonly Func<string, string, Task<Result<AgentSession>>> _createSession;

  /// <summary>Optional preference store persisting the last-chosen provider (test seam:
  ///     when null, opens still work and the preference is simply not remembered).</summary>
  private readonly IAppPreferenceStore? _preferences;

  /// <summary>Optional stream-sink override for every opened session (test seam).
  ///     When null, production self-marshaling applies per session view-model.</summary>
  private readonly Func<UiStreamEvent, Task>? _streamSink;

  [ObservableProperty]
  public partial AgentTabViewModel? SelectedTab { get; set; }

  [ObservableProperty]
  public partial bool IsOpeningAgent { get; set; }

  public ObservableCollection<AgentTabViewModel> Tabs { get; } = [];

  public bool HasTabs => Tabs.Count > 0;

  /// <summary>Providers offered by the new-agent dialog: those with a configured key.</summary>
  public IReadOnlyList<ProviderOption> AvailableProviders { get; }

  /// <summary>Provider the new-agent dialog pre-selects (last persisted choice).</summary>
  public string PreferredProviderId { get; }

  public ICommand OpenAgentCommand { get; }

  /// <summary>Raised when the shell wants the new-agent dialog shown.</summary>
  public event EventHandler? OpenAgentRequested;

  public MainViewModel(Func<string, string, Task<Result<AgentSession>>> createSession,
      IReadOnlyList<ProviderOption>? availableProviders = null,
      string? preferredProviderId = null,
      IAppPreferenceStore? preferences = null,
      Func<UiStreamEvent, Task>? uiStreamSink = null)
  {
    _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
    _preferences = preferences;
    _streamSink = uiStreamSink;
    AvailableProviders = availableProviders ?? [new(Providers.OpenRouter, Providers.DisplayName(Providers.OpenRouter))];
    string preferred = preferredProviderId ?? AvailableProviders[0].Id;
    PreferredProviderId = AvailableProviders.Any(p => p.Id == preferred) ? preferred : AvailableProviders[0].Id;
    OpenAgentCommand = new RelayCommand(
        () => OpenAgentRequested?.Invoke(this, EventArgs.Empty),
        () => !IsOpeningAgent);
    Tabs.CollectionChanged += OnTabsChanged;
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
      // alongside the thread switch (same reasoning as DesktopHost.OffUiThread):
      // Task.Run alone still flows the caller's SynchronizationContext.
      Task<Result<AgentSession>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _createSession(full, providerName));
      }

      Result<AgentSession> created = await scheduled;
      if (!created.IsSuccess)
      {
        return Result.Failure<AgentTabViewModel>(created.Error!);
      }

      AgentSession session = created.Value!;

      // Self-referencing sink hook, the same pattern as the pre-tab window wiring:
      // the VM is captured after construction so its own sink marshals its events
      // onto the UI thread. An injected shell-level sink (tests) takes precedence.
      AgentSessionViewModel? sessionVmRef = null;
      AgentSessionViewModel sessionVm = new(
          // TurnRunner puts ct second; SendMessageCommandHandler.Handle keeps it last
          // (CA1068) — adapt the parameter order at the call site.
          (command, ct, onContentDelta, onReasoningDelta, onIterationEnd, onToolCall, onToolResult, onNotice)
              => session.Handler.Handle(command, onContentDelta, onReasoningDelta,
                  onIterationEnd, onToolCall, onToolResult, onNotice, ct),
          session.Lifecycle,
          session.RootId,
          session.Conversation,
          Providers.DisplayName(session.ProviderName),
          session.ModelId,
          session.WorkspaceRoot,
          uiStreamSink: _streamSink ?? (evt => (sessionVmRef ??
              throw new InvalidOperationException("session view-model not initialized"))
              .ApplyUiStreamEventOnUIThreadAsync(evt)),
          inbox: session.Inbox,
          childRuntime: session.ChildRuntime,
          statusModelUpdater: id => sessionVmRef!.Status.ModelId = id);
      sessionVmRef = sessionVm;
      AttachClarifyChannel(sessionVm, session.ClarifyChannel);

      AgentTabViewModel tab = new(session, sessionVm);
      // /exit inside the agent closes its own tab.
      sessionVm.CloseRequested += (_, _) => CloseTab(tab);
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

  /// <summary>Synchronous fire-and-forget close used by view-model internals (e.g.
  ///     /exit). Teardown errors are swallowed inside <see cref="CloseTabAsync"/>.</summary>
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
        [option], session.ProviderName, preferences: null, uiStreamSink: uiStreamSink);
    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(session.WorkspaceRoot, session.ProviderName);
    return !opened.IsSuccess
        ? throw new InvalidOperationException($"prebuilt session failed to open: [{opened.Error!.Code}] {opened.Error.Message}")
        : vm;
  }
}
