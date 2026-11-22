using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Covers the startup shutdown-mode seams on <see cref="DesktopHost"/> and the
/// main window's close-workspace handoff.
///
/// Background: Avalonia's default OnLastWindowClose mode killed the app mid-startup — between
/// framework initialization and the main window being shown, the ONLY windows are transient
/// helpers (folder-picker host, dialogs), so closing one shut the process down the instant a
/// workspace was picked or cancelled.
///
/// The full window-close-to-shutdown causal chain cannot run under Avalonia.Headless.XUnit:
/// headless provides no classic-desktop lifetime, and windows shown against a standalone
/// ClassicDesktopStyleApplicationLifetime never register with it (verified: lifetime.Windows
/// stays empty through Show + MainWindow assignment), so no shutdown request can fire either
/// way. The seams are therefore tested at the contract level here; the end-to-end behavior is
/// covered by launching the real app.</summary>
[Trait("Category", "Process")]
public sealed class StartupShutdownModeTests
{
  [AvaloniaFact]
  public void Deferral_Switches_Lifetime_To_Explicit_Shutdown()
  {
    using ClassicDesktopStyleApplicationLifetime lifetime = new();

    // Documents the platform default that caused the startup race.
    Assert.Equal(ShutdownMode.OnLastWindowClose, lifetime.ShutdownMode);

    DesktopHost.DeferShutdownDuringStartup(lifetime);
    Assert.Equal(ShutdownMode.OnExplicitShutdown, lifetime.ShutdownMode);

    DesktopHost.EnableWindowCloseShutdown(lifetime);
    Assert.Equal(ShutdownMode.OnLastWindowClose, lifetime.ShutdownMode);
  }

  /// <summary>The rail's close-workspace entry exists only while a workspace is bound: with no
  ///     bound root there is nothing to close.</summary>
  [AvaloniaFact]
  public void CloseWorkspaceEntry_IsVisibleOnlyWhileAWorkspaceIsBound()
  {
    MainWindow bound = WindowBoundTo(@"C:\work\alpha");
    bound.Show();
    Dispatcher.UIThread.RunJobs();
    Button entry = bound.GetControl<Button>("CloseWorkspaceMenuItem");
    Assert.True(entry.IsVisible);
    Assert.True(entry.IsEffectivelyEnabled);
    Assert.Contains("Close workspace", ToolTip.GetTip(entry) as string, StringComparison.Ordinal);

    MainWindow unbound = WindowBoundTo(null);
    unbound.Show();
    Dispatcher.UIThread.RunJobs();
    Assert.False(unbound.GetControl<Button>("CloseWorkspaceMenuItem").IsVisible,
        "the close-workspace entry must be hidden with no workspace bound");
    Assert.False(((MainViewModel)unbound.DataContext!).CloseWorkspaceCommand.CanExecute(null),
        "the close-workspace command must refuse a shell with no workspace");
  }

  /// <summary>The rail's close-workspace click tears every agent down, announces the request
  ///     once the teardown is over, and leaves the window open: returning to the launcher is the
  ///     host's decision, and a window that closed itself would end the process under
  ///     OnLastWindowClose.</summary>
  [AvaloniaFact]
  public async Task CloseWorkspaceClick_AnnouncesAfterTheTeardown_AndLeavesTheWindowOpen()
  {
    MainWindow window = WindowBoundTo(@"C:\work\alpha");
    window.Show();
    Dispatcher.UIThread.RunJobs();
    MainViewModel vm = (MainViewModel)window.DataContext!;
    _ = await vm.OpenAgentInWorkspaceAsync().ConfigureAwait(true);
    Assert.NotEmpty(vm.Tabs);

    int requested = 0;
    bool announcedWithNothingRunning = false;
    window.CloseWorkspaceRequested += (_, _) =>
    {
      requested++;
      announcedWithNothingRunning = vm.Tabs.Count == 0;
    };

    // A REAL POINTER CLICK: raising Button.ClickEvent directly bypasses the template's own
    // click handling, which is what invokes the bound command.
    Button entry = window.GetControl<Button>("CloseWorkspaceMenuItem");
    Point? center = entry.TranslatePoint(new Point(entry.Bounds.Width / 2, entry.Bounds.Height / 2), window);
    Assert.True(center is not null, "the close-workspace entry must be laid out inside the window");
    window.MouseDown(center.Value, MouseButton.Left);
    window.MouseUp(center.Value, MouseButton.Left);
    PumpUntil(() => requested == 1);

    Assert.Equal(1, requested);
    Assert.True(announcedWithNothingRunning,
        "the request must be announced only once every agent is torn down");
    Assert.Empty(vm.Tabs);
    Assert.True(window.IsVisible, "closing the workspace must not close the window");
  }

  /// <summary>Closing the window after the workspace was closed still closes it: the close
  ///     path is the window's own, and the teardown it runs is the one already finished.</summary>
  [AvaloniaFact]
  public async Task WindowClose_AfterCloseWorkspace_StillClosesTheWindow()
  {
    MainWindow window = WindowBoundTo(@"C:\work\alpha");
    window.Show();
    Dispatcher.UIThread.RunJobs();
    MainViewModel vm = (MainViewModel)window.DataContext!;

    await vm.CloseWorkspaceCommand.ExecuteAsync(null).ConfigureAwait(true);
    Assert.True(window.IsVisible, "closing the workspace must not close the window");

    window.Close();
    Dispatcher.UIThread.RunJobs();

    Assert.False(window.IsVisible, "the window's own close is what ends the process");
  }

  /// <summary>Startup stops at the launcher: the bootstrap builds a launcher window over its own
  ///     recent-workspace store, and no main window exists until a workspace is chosen.</summary>
  [AvaloniaFact]
  public void Bootstrap_ProducesALauncher_AndNoMainWindow()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    DesktopBootstrap boot = Bootstrap(storage, openRouterKey: null);

    LauncherWindow launcher = DesktopHost.CreateLauncherWindow(boot);
    launcher.Show();
    Dispatcher.UIThread.RunJobs();

    LauncherViewModel vm = Assert.IsType<LauncherViewModel>(launcher.DataContext);
    Assert.False(vm.HasConfiguredProvider, "no key is configured, so the launcher must say so");
    Assert.Equal("eThang Agent", launcher.Title);

    // The launcher is the whole of startup: it is a launcher, not a shell, so no workspace is
    // bound and no agent exists yet.
    _ = Assert.IsType<LauncherWindow>(launcher);
    Assert.Empty(vm.Recents);
    launcher.Close();
  }

  /// <summary>A chosen workspace hands off to exactly one main window, bound to that root for its
  ///     whole life and titled with the workspace's folder name.</summary>
  [AvaloniaFact]
  public async Task Handoff_ProducesOneMainWindow_BoundToTheChosenRoot()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    DesktopBootstrap boot = Bootstrap(storage, openRouterKey: "sk-or-v1-abc");
    string root = WorkspaceIdentity.Normalize(@"C:\work\alpha");

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using DesktopSession session = DesktopHost.CreateMainWindow(boot, root);
#pragma warning restore CA2007
    session.Window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(root, session.WorkspaceRoot);
    Assert.Equal("eThang Agent — alpha", session.Window.Title);
    MainViewModel vm = Assert.IsType<MainViewModel>(session.Window.DataContext);
    Assert.Equal(root, vm.WorkspaceRoot);
    Assert.Empty(vm.Tabs);
  }

  /// <summary>Closing a workspace stops its loops and opening a second one starts a fresh set, so
  ///     exactly one set of background loops is ever running.</summary>
  [AvaloniaFact]
  public async Task ClosingAWorkspace_StopsItsLoops_AndASecondWorkspaceStartsAFreshSet()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    DesktopBootstrap boot = Bootstrap(storage, openRouterKey: null);
    string root = WorkspaceIdentity.Normalize(@"C:\work\alpha");

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using DesktopSession first = DesktopHost.CreateMainWindow(boot, root);
#pragma warning restore CA2007
    first.Window.Show();
    Dispatcher.UIThread.RunJobs();

    // The watchdog, the resource governor, the RSS monitor and the retention sweep.
    Assert.Equal(4, first.Loops.Count);
    Assert.False(first.LoopsStopped);

    await first.CloseWorkspaceAsync().WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(first.LoopsStopped, "closing the workspace must cancel its loops");
    foreach (Task loop in first.Loops)
    {
      await loop.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using DesktopSession second = DesktopHost.CreateMainWindow(boot, root);
#pragma warning restore CA2007
    second.Window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(4, second.Loops.Count);
    Assert.False(second.LoopsStopped, "a second workspace must start its own loops");
    Assert.All(first.Loops, loop => Assert.True(loop.IsCompleted,
        "the closed workspace's loops must be finished while the new set runs"));
  }

  /// <summary>The launcher's Settings entry is usable with no workspace open: the prefill carries
  ///     the bootstrap's key state and needs no shell.</summary>
  [AvaloniaFact]
  public void SettingsValuesFrom_CarriesTheBootstrapKeyState_WithNoWorkspaceOpen()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    SettingsValues configured = DesktopHost.SettingsValuesFrom(Bootstrap(storage, "sk-or-v1-abc"));
    Assert.Equal("sk-or-v1-abc", configured.OpenRouterKey);
    Assert.Null(configured.WorkspaceRoot);
    Assert.Equal(CommitStyle.Conventional, configured.CommitStyle);

    SettingsValues unconfigured = DesktopHost.SettingsValuesFrom(Bootstrap(storage, openRouterKey: null));
    Assert.Null(unconfigured.OpenRouterKey);

    // The prefill is enough to open the dialog: it validates and offers Save.
    SettingsWindow window = new(configured);
    window.Show();
    Dispatcher.UIThread.RunJobs();
    SettingsViewModel vm = Assert.IsType<SettingsViewModel>(window.DataContext);
    Assert.Null(vm.ValidationError);
    Assert.True(vm.CanSave);
    Assert.True(vm.IsSignedIn);
    window.Close();
  }

  /// <summary>A launch signalled by another process raises the window only when this session
  ///     serves that workspace: a session bound to alpha raises for alpha and refuses beta, which
  ///     is what makes the request ignorable rather than opening a second window.</summary>
  [AvaloniaFact]
  public async Task SignalledLaunch_RaisesTheWindowOnlyForItsOwnWorkspace()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    DesktopBootstrap boot = Bootstrap(storage, openRouterKey: null);
    string root = WorkspaceIdentity.Normalize(@"C:\work\alpha");

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using DesktopSession session = DesktopHost.CreateMainWindow(boot, root);
#pragma warning restore CA2007
    session.Window.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.True(session.RaiseFor(@"c:\work\alpha\"),
        "a launch for this workspace must raise this window");
    Assert.False(session.RaiseFor(@"C:\work\beta"),
        "a launch for another workspace must be ignored, not raise this window");
    Assert.False(session.RaiseFor("  "), "a blank root names no workspace");
  }

  /// <summary>A save from the launcher's Settings entry lands in the store through the same
  ///     writer the shell uses, so a key entered with no workspace open is in force for the next
  ///     workspace's agents.</summary>
  [AvaloniaFact]
  public async Task LauncherSettingsSave_StoresTheKey_ForTheNextWorkspace()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    MemoryPreferences preferences = new();
    DesktopBootstrap boot = Bootstrap(storage, openRouterKey: null) with { Preferences = preferences };

    PersistedSettings persisted = await new SettingsPersistence(preferences, boot.ApiKeys)
        .ApplyAsync(new SettingsUpdate("  sk-or-v1-abc  ", CommitStyle.Conventional),
            boot.PreferredProviderId)
        .ConfigureAwait(true);

    Assert.Equal("sk-or-v1-abc", persisted.OpenRouterKey);
    Assert.Equal("sk-or-v1-abc",
        await preferences.GetAsync(OpenRouterSettings.PreferenceKey).ConfigureAwait(true));

    // The next workspace's agents read the key off the bootstrap's settings.
    AgentSettings applied = boot.Settings.WithApiKeys(persisted.OpenRouterKey);
    Assert.True(applied.HasOpenRouter);
  }

  /// <summary>Closing the launcher with no workspace open is the process-exit path: the launcher is
  ///     the only window, and shutdown is window-close driven, so the last window closing ends the
  ///     process with the exit code the framework records.</summary>
  [AvaloniaFact]
  public void LauncherClosedWithNoWorkspace_LeavesNoWindow_AndShutdownIsWindowCloseDriven()
  {
    using TestAppDatabase storage = TestAppDatabase.Create();
    DesktopBootstrap boot = Bootstrap(storage, openRouterKey: null);
    using ClassicDesktopStyleApplicationLifetime lifetime = new();

    LauncherWindow launcher = DesktopHost.CreateLauncherWindow(boot);
    DesktopHost.EnableWindowCloseShutdown(lifetime);
    lifetime.MainWindow = launcher;
    launcher.Show();
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(ShutdownMode.OnLastWindowClose, lifetime.ShutdownMode);
    Assert.True(launcher.IsVisible);

    launcher.Close();
    Dispatcher.UIThread.RunJobs();

    Assert.False(launcher.IsVisible, "closing the launcher must close its window");
    Assert.Same(launcher, lifetime.MainWindow);
  }

  /// <summary>A bootstrap over throwaway storage: the real session factory and catalog, and fakes
  ///     for the audit store, the retention sweep and the key protector.</summary>
  /// <param name="storage">The throwaway database.</param>
  /// <param name="openRouterKey">The configured key, or null for an unconfigured provider.</param>
  private static DesktopBootstrap Bootstrap(TestAppDatabase storage, string? openRouterKey)
  {
    AgentSettings settings = new(
        new OpenRouterSettings(openRouterKey, new Uri(OpenRouterSettings.DefaultBaseUrl)),
        new AgentRunOptions(null, 2));
    return new DesktopBootstrap(
        new AgentSessionFactory(settings, storage.Database),
        settings,
        Providers.OpenRouter,
        new MemoryPreferences(),
        new IdentityProtector(),
        new SessionCatalogQueryHandler(new TestFixtures.StubStore()),
        CommitStyle.Conventional,
        new SilentEvents(),
        WatchdogOptions.Default,
        CompactionHandoff: false,
        _ => new StubSignIn(),
        new NoRetention(),
        new FakeRecentWorkspaceStore(),
        PathToolsText: null,
        RetentionWindowText: null,
        GlobalSessionFiles: [],
        GlobalSkillDirectories: []);
  }

  private sealed class MemoryPreferences : IAppPreferenceStore
  {
    private readonly Dictionary<string, string> _stored = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      _stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_stored.Remove(key));
  }

  private sealed class IdentityProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => apiKey;

    public string? Unprotect(string storedValue) => storedValue;
  }

  private sealed class SilentEvents : IWatchdogEventStore
  {
    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
        => Task.FromResult(Result.Success("recorded"));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(WatchdogEventKind kind,
        int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));
  }

  private sealed class NoRetention : IRetentionService
  {
    public Task<Result<RetentionRun?>> RunOnceAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<RetentionRun?>(null));
  }

  private sealed class StubSignIn : IProviderSignIn
  {
    public Task<Result<string>> SignInAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotExercised",
            "the sign-in flow is not exercised by this fixture.")));
  }

  /// <summary>Runs the dispatcher until a click's teardown has landed, bounded by the test
  ///     ceiling so a click that never lands fails instead of hanging.</summary>
  /// <param name="condition">The outcome to wait for.</param>
  private static void PumpUntil(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (!condition() && DateTimeOffset.UtcNow < deadline)
    {
      Dispatcher.UIThread.RunJobs();
      Thread.Sleep(5);
    }

    Dispatcher.UIThread.RunJobs();
    Assert.True(condition(), "the click path never completed.");
  }

  /// <summary>A window over a shell bound to <paramref name="workspaceRoot"/>, or to nothing.</summary>
  private static MainWindow WindowBoundTo(string? workspaceRoot)
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(Session(root)));
    MainViewModel vm = new(create, new MainViewModelOptions { WorkspaceRoot = workspaceRoot });
    return new MainWindow(vm);
  }

  private static AgentSession Session(string root)
    => new(new ServiceCollection().BuildServiceProvider(), AgentId.NewId(), new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: Providers.OpenRouter,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
}
