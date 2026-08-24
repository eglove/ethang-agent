using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

public delegate Task<Result<string>> TurnRunner(
    SendMessageCommand command,
    CancellationToken ct,
    Action<string>? onContentDelta,
    Action<string>? onReasoningDelta,
    Action? onIterationEnd,
    Action<string, string>? onToolCall,
    Action<string, string>? onToolResult);

/// <summary>Shell-level state for the main window: the left menu bar and the open
///     agent tabs. Each tab owns an <see cref="AgentSessionViewModel"/> bound to its
///     own isolated <see cref="AgentSession"/>; opening a directory creates one via
///     the injected session-factory hook. The shell itself holds no agent state.
///     A static <see cref="ForPrebuiltSessionAsync"/> keeps single-session hosts and
///     tests simple while tabs remain the primary surface.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly Func<string, Task<Result<AgentSession>>> _createSession;

    /// <summary>Optional stream-sink override for every opened session (test seam).
    ///     When null, production self-marshaling applies per session view-model.</summary>
    private readonly Func<UiStreamEvent, Task>? _streamSink;

    [ObservableProperty]
    private AgentTabViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isOpeningAgent;

    public ObservableCollection<AgentTabViewModel> Tabs { get; } = [];

    public bool HasTabs => Tabs.Count > 0;

    public ICommand OpenAgentCommand { get; }

    /// <summary>Raised when the shell wants the platform folder picker shown.</summary>
    public event EventHandler? OpenAgentRequested;

    public MainViewModel(Func<string, Task<Result<AgentSession>>> createSession,
        Func<UiStreamEvent, Task>? uiStreamSink = null)
    {
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _streamSink = uiStreamSink;
        OpenAgentCommand = new RelayCommand(
            () => OpenAgentRequested?.Invoke(this, EventArgs.Empty),
            () => !IsOpeningAgent);
        Tabs.CollectionChanged += OnTabsChanged;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTabs));
        if (SelectedTab is not null && !Tabs.Contains(SelectedTab)) SelectedTab = null;
    }

    /// <summary>Menu-bar entry point: raises the picker request. The view shows the
    ///     folder picker and calls <see cref="OpenAgentAsync"/> with the choice.</summary>
    public void RequestOpenAgent() => OpenAgentRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens a new agent tab over <paramref name="workspaceRoot"/>. Fails with
    ///     a structured error when the session cannot be created; the shell surfaces it.
    ///     Reopening an already-open directory selects its existing tab instead.</summary>
    public async Task<Result<AgentTabViewModel>> OpenAgentAsync(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return Result<AgentTabViewModel>.Failure(new Error("InvalidWorkspace",
                "workspace root must be a non-empty directory path."));

        var full = Path.GetFullPath(workspaceRoot);
        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.Container.WorkspaceRoot, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedTab = existing;
            return Result<AgentTabViewModel>.Success(existing);
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
                scheduled = Task.Run(() => _createSession(full));
            var created = await scheduled;
            if (!created.IsSuccess)
                return Result<AgentTabViewModel>.Failure(created.Error!);
            var session = created.Value!;

            // Self-referencing sink hook, the same pattern as the pre-tab window wiring:
            // the VM is captured after construction so its own sink marshals its events
            // onto the UI thread. An injected shell-level sink (tests) takes precedence.
            AgentSessionViewModel? sessionVmRef = null;
            var sessionVm = new AgentSessionViewModel(
                (command, ct, content, reasoning, iterationEnd, toolCall, toolResult) =>
                    session.Handler.Handle(command, ct, content, reasoning, iterationEnd, toolCall, toolResult),
                session.Lifecycle,
                session.RootId,
                session.Conversation,
                session.ModelId,
                session.WorkspaceRoot,
                uiStreamSink: _streamSink ?? (evt => (sessionVmRef ??
                    throw new InvalidOperationException("session view-model not initialized"))
                    .ApplyUiStreamEventOnUIThreadAsync(evt)));
            sessionVmRef = sessionVm;
            AttachClarifyChannel(sessionVm, session.ClarifyChannel);

            var tab = new AgentTabViewModel(session, sessionVm);
            // /exit inside the agent closes its own tab.
            sessionVm.CloseRequested += (_, _) => CloseTab(tab);
            Tabs.Add(tab);
            SelectedTab = tab;
            return Result<AgentTabViewModel>.Success(tab);
        }
        finally
        {
            IsOpeningAgent = false;
        }
    }

    /// <summary>Closes a tab: completes its root session gracefully (best effort), then
    ///     removes it and disposes its container. Selection falls to the last remaining
    ///     tab; closing the final tab leaves the empty shell with the menu bar.</summary>
    public async Task CloseTabAsync(AgentTabViewModel tab)
    {
        if (!Tabs.Contains(tab)) return;
        try { await tab.ViewModel.ShutdownAsync(); } catch { /* teardown never throws */ }
        Tabs.Remove(tab);
        SelectedTab = Tabs.LastOrDefault();
        await tab.Container.Services.DisposeAsync();
    }

    /// <summary>Synchronous fire-and-forget close used by view-model internals (e.g.
    ///     /exit). Teardown errors are swallowed inside <see cref="CloseTabAsync"/>.</summary>
    public void CloseTab(AgentTabViewModel tab) => _ = CloseTabAsync(tab);

    private void AttachClarifyChannel(AgentSessionViewModel vm, IClarifyChannel channel)
    {
        vm.AttachClarifyChannel(channel);
        // Mirror the pre-tab wiring: the desktop channel resolves its presenter lazily,
        // marshals onto the UI thread, and presents through THIS tab's view-model so
        // each pending question renders inside its own agent tab.
        if (channel is AvaloniaClarifyChannel desktop)
            desktop.SetPresenter(q => PresentOnUIThread(() => vm.PresentClarifyAsync(q)));
    }

    private static async Task<ClarifyViewModel> PresentOnUIThread(
        Func<Task<ClarifyViewModel>> present)
    {
        var tcs = new TaskCompletionSource<ClarifyViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { tcs.SetResult(await present()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task;
    }

    /// <summary>Single-session convenience: a shell whose only tab opens over a
    ///     pre-built session (used by hosts/tests that compose the session themselves).</summary>
    public static async Task<MainViewModel> ForPrebuiltSessionAsync(AgentSession session,
        Func<UiStreamEvent, Task>? uiStreamSink = null)
    {
        var vm = new MainViewModel(
            _ => Task.FromResult(Result<AgentSession>.Success(session)), uiStreamSink);
        var opened = await vm.OpenAgentAsync(session.WorkspaceRoot);
        if (!opened.IsSuccess)
            throw new InvalidOperationException($"prebuilt session failed to open: [{opened.Error!.Code}] {opened.Error.Message}");
        return vm;
    }
}
