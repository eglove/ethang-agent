using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
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
