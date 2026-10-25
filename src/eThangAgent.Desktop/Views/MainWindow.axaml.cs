using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Desktop.Views;

/// <summary>The main window shell: a thin left icon rail (labels are hover
///     tooltips) plus a tab host with one tab per open agent. 'Open Workspace' shows
///     the new-agent dialog (provider dropdown plus
///     workspace picker); the chosen pair opens as a new tab wired exclusively for
///     that provider. Opening another directory (or the same directory under the
///     other provider) opens another tab — each backed by its own isolated session
///     container.</summary>
internal partial class MainWindow : Window
{
  private readonly MainViewModel? _vm;

  // Window-close teardown state (issue #274): the first close request cancels itself, runs
  // the app-wide shutdown, then closes for real. These two flags are what keep a re-entrant
  // or repeated close from running the teardown twice or exiting mid-teardown.
  private bool _shutdownRunning;
  private bool _shutdownComplete;

  public MainWindow() => InitializeComponent();

  public MainWindow(MainViewModel vm) : this()
  {
    ArgumentNullException.ThrowIfNull(vm);
    _vm = vm;
    DataContext = vm;

    // The menu button asks the view to show the new-agent modal (UI-affine); the
    // chosen provider/workspace pair comes back through OpenAgentAsync. The Sessions
    // button shows the sessions dialog the same way, its pick flowing through
    // ResumeSessionAsync. The gear button shows the settings modal the same way, and
    // its result flows through ApplySettingsAsync. The Model Settings button shows
    // the selected tab's settings window — model choice, effort, sampling knobs,
    // provider routing — with its save flowing through ApplySamplingSettingsAsync.
    vm.OpenAgentRequested += async (_, _) => await ShowNewAgentDialogAsync();
    vm.SessionsRequested += async (_, _) => await ShowSessionsDialogAsync();
    vm.SettingsRequested += async (_, _) => await ShowSettingsDialogAsync();
    vm.ModelSettingsRequested += async (_, _) => await ShowModelSettingsDialogAsync();
    vm.McpServersRequested += async (_, _) => await ShowMcpServersDialogAsync();
  }

  /// <summary>Shows the Sessions dialog (persisted root sessions, already-open ones
  ///     greyed) and resumes the pick. A cancelled dialog is a no-op. Failures surface
  ///     as a dialog.</summary>
  private async Task ShowSessionsDialogAsync()
  {
    Func<CancellationToken, Task<Result<IReadOnlyList<SessionCatalogEntry>>>>? loader =
        _vm!.SessionCatalogLoader;
    if (loader is null)
    {
      await ShowOpenFailedAsync("The session catalog is unavailable in this host.");
      return;
    }

    SessionsWindow dialog = new(loader, () => _vm.OpenSessionIds);
    AgentId? resumed = await dialog.ShowDialog<AgentId?>(this);
    if (resumed is null)
    {
      return; // user cancelled — no-op
    }

    Result<AgentTabViewModel> result = await _vm.ResumeSessionAsync(resumed.Value);
    if (!result.IsSuccess)
    {
      await ShowOpenFailedAsync(result.Error.Message);
    }
  }

  /// <summary>Shows the new-agent dialog (provider dropdown + workspace picker +
  ///     the session-file / skill-directory lists configured at launch) and opens
  ///     (or selects) the agent tab. The confirmed file lists persist BEFORE the
  ///     open, so the new session's container build reads them. A cancelled dialog
  ///     is a no-op. Failures surface as a dialog.</summary>
  private async Task ShowNewAgentDialogAsync()
  {
    try
    {
      NewAgentWindow dialog = new(_vm!.AvailableProviders, _vm.PreferredProviderId,
          new LaunchRows
          {
            GlobalFiles = await _vm.GetGlobalSessionFilesAsync(),
            GlobalSkillDirectories = await _vm.GetGlobalSkillDirectoriesAsync(),
          },
          loadWorkspaceRows: async root => (
              await _vm.GetWorkspaceSessionFilesAsync(root),
              await _vm.GetWorkspaceSkillDirectoriesAsync(root),
              await _vm.GetTrustedDirectoriesAsync(root)));
      NewAgentChoice? choice = await dialog.ShowDialog<NewAgentChoice?>(this);
      if (choice is null)
      {
        return; // user cancelled — no-op
      }

      // The launch files are applied FIRST so the new session's container build
      // reads the just-saved lists. A refusal here (a missing workspace root) is
      // a NAMED failure, not an exception: the shell surfaces it and stops -
      // the 2026-10-20 crash was an ArgumentNullException escaping this handler.
      Result<bool> applied = await _vm.ApplyLaunchFilesAsync(choice);
      if (!applied.IsSuccess)
      {
        await ShowOpenFailedAsync(applied.Error.Message);
        return;
      }

      Result<AgentTabViewModel> result = await _vm.OpenAgentAsync(choice.WorkspaceRoot, choice.ProviderId);
      if (!result.IsSuccess)
      {
        await ShowOpenFailedAsync(result.Error.Message);
      }
    }
    // Named decision (CA1031): this handler is an async-void on the dispatcher, so an
    // escaping exception tears the WHOLE process down - the crash amplifier the
    // launch-guard tests name. A failed launch is a DIALOG, never a fault.
#pragma warning disable CA1031, S2486 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031, S2486
    {
      await ShowOpenFailedAsync("The workspace could not be opened: " + ex.Message);
    }
  }

  /// <summary>Shows the settings modal prefilled with the current key. A cancelled
  ///     dialog is a no-op; a confirmed one applies the settings (persist + factory
  ///     rebind) on the shell.</summary>
  private async Task ShowSettingsDialogAsync()
  {
    SettingsWindow dialog = new(new SettingsValues
    {
      OpenRouterKey = _vm!.ConfiguredOpenRouterKey,
      CommitStyle = _vm.ConfiguredCommitStyle,
      CompactionModels = await _vm.GetCompactionOptionsAsync(),
      SelectedCompactionModel = await _vm.GetSelectedCompactionModelAsync(),
      WorkspaceRoot = _vm.SelectedTab?.Container.WorkspaceRoot,
      MaxConcurrentAgentsText = _vm.ConfiguredMaxConcurrentAgents,
      DefaultModelText = _vm.ConfiguredDefaultModel,
      RootTurnIdleText = _vm.ConfiguredRootTurnIdle,
      WatchdogTickText = _vm.ConfiguredWatchdogTick,
      WatchdogIdleText = _vm.ConfiguredWatchdogIdle,
      WatchdogWrapUpText = _vm.ConfiguredWatchdogWrapUp,
      OpenRouterBaseUrlText = _vm.ConfiguredOpenRouterBaseUrl,
      ComputerUse = _vm.ConfiguredComputerUse,
      CompactionHandoff = _vm.ConfiguredCompactionHandoff,
      SkillRegistryDefaultTarget = await _vm.GetSkillRegistryDefaultTargetAsync(),
      OpenRouterManagementKey = _vm.ConfiguredOpenRouterManagementKey,
    });
    SettingsUpdate? update = await dialog.ShowDialog<SettingsUpdate?>(this);
    if (update is null)
    {
      return; // user cancelled — no-op
    }

    await _vm.ApplySettingsAsync(update);
  }

  /// <summary>Shows the Model Settings window for the selected tab: the model
  ///     choice (searchable catalog, auto row where the provider offers one),
  ///     effort, the twelve sampling knobs, and the OpenRouter section (hidden
  ///     elsewhere). A cancelled dialog is a no-op; a confirmed one applies the
  ///     snapshot on the shell (session + per-workspace preferences, the model
  ///     choice included). The window's persistence seam delegates here —
  ///     nothing persists unless save succeeds.</summary>
  private async Task ShowModelSettingsDialogAsync()
  {
    if (_vm!.SelectedTab is not { } tab)
    {
      return; // no selected tab — the menu entry is hidden anyway
    }

    if (tab.Container.Preferences is not { } preferences)
    {
      return; // host composed the session without model preferences — nothing to edit
    }

    ModelSettingsViewModel vm = new(
        preferences,
        tab.Container.ProviderName,
        persist: snapshot => _ = _vm.ApplySamplingSettingsAsync(snapshot),
        await _vm.ReadSamplingPrefsForSelectedTabAsync(),
        loadCatalog: _vm.SelectedTabCatalogLoader,
        currentModelId: preferences.ModelId,
        refreshCatalog: _vm.SelectedTabCatalogRefresher);
    ModelSettingsWindow dialog = new(vm);
    _ = await dialog.ShowDialog<ModelSettingsSnapshot?>(this);
  }

  /// <summary>Shows the MCP servers dialog (issue #106) over the selected tab's
  ///     own seams: the store the form writes through, the SHARED pooled access the
  ///     status view reads (the same object the mcp tool dispatches through), and
  ///     the workspace id. No tab: structured failure, not silence.</summary>
  private async Task ShowMcpServersDialogAsync()
  {
    if (_vm is not { } vm)
    {
      return; // design-time / headless shell without a view-model
    }

    Func<(IMcpServerStore Store, IMcpServerAccess Access, string WorkspaceId)>? loader =
        vm.SelectedMcpServersLoader;
    if (loader is null)
    {
      await ShowOpenFailedAsync("Open an agent tab before configuring MCP servers.");
      return;
    }

    (IMcpServerStore store, IMcpServerAccess access, string workspaceId) = loader();
    McpServersWindow dialog = new(new McpServersViewModel(store, access, workspaceId));
    await dialog.ShowDialog(this);
  }

  private async Task ShowOpenFailedAsync(string message)
  {
    Window dialog = new()
    {
      Title = "eThang Agent — could not open agent",
      SizeToContent = SizeToContent.WidthAndHeight,
      CanResize = false,
      Content = new StackPanel
      {
        Margin = new Avalonia.Thickness(24),
        Spacing = 16,
        Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                    new Button { Content = "OK" },
                },
      },
    };
    Button okButton = (Button)((StackPanel)dialog.Content).Children[^1];
    okButton.Click += (_, _) => dialog.Close();
    await dialog.ShowDialog(this);
  }

  private void OnCloseTab(object? sender, RoutedEventArgs e)
  {
    if (_vm is null)
    {
      return;
    }

    if (sender is Button { DataContext: AgentTabViewModel tab })
    {
      _ = _vm.CloseTabAsync(tab);
    }
  }

  /// <summary>Tears the app down BEFORE the process exits (issue #274).
  ///     <para>THE ORDERING HAZARD this closes: the previous handler was <c>async void</c> on
  ///     <c>Closed</c>, and <c>ShutdownMode.OnLastWindowClose</c> terminates the process the
  ///     moment the last window closes - so the handler's awaits raced process exit. Worse,
  ///     it only completed each root session gracefully and never disposed the containers,
  ///     so every conversation host process survived the app.</para>
  ///     <para>The close is therefore CANCELLED first, the teardown runs to completion, and
  ///     only then is the window closed for real — the second pass is what lets Avalonia's
  ///     last-window shutdown fire with nothing left running.</para>
  ///     <para>Closing is a decision to kill everything: in-flight turns are interrupted by
  ///     <see cref="MainViewModel.ShutdownAllAsync"/>, not awaited.</para></summary>
  private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
  {
    if (_shutdownComplete)
    {
      return; // the teardown already ran; let this close through
    }

    // Hold the window open while teardown runs. Without this the process exits mid-teardown
    // and the containers are never disposed - the bug this handler exists to fix.
    e.Cancel = true;
    if (_shutdownRunning)
    {
      return; // a second close request while teardown runs is the same request
    }

    _shutdownRunning = true;
    try
    {
      if (_vm is { } vm)
      {
        await vm.ShutdownAllAsync();
      }
    }
    // Named decision (CA1031): shutdown on close is best effort — a failing teardown must
    // never prevent the window from closing. The kernel-level containment (the host's
    // kill-on-close Job Object) is the backstop if a disposal path faults.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync(ex.ToString());
    }
#pragma warning restore CA1031

    _shutdownComplete = true;
    Close();
  }
}
