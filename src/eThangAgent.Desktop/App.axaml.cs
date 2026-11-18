using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using eThangAgent.Windows.ACL;
using Markdig;
using MarkView.Avalonia;
using TextMateSharp.Grammars;

namespace eThangAgent.Desktop;

internal class App : Application
{
#pragma warning disable S1144, CA1823, IDE0052 // by-design: the initializer IS the registration
  // App-lifetime subscription, registered once at type init and never disposed
  // by design: this handler IS the single link-routing gate for every
  // MarkdownViewer the app ever creates (transcript blocks, future surfaces).
  private static readonly IDisposable LinkHandlerSubscription =
      MarkdownViewer.LinkClickedEvent.AddClassHandler<MarkdownViewer>((_, e) => MarkdownLinkLauncher.TryOpen(e.Url));
#pragma warning restore S1144, CA1823, IDE0052

  // The one bootstrap for the process: it owns the app database, the preference store and the
  // session factory, so a second instance would open the database twice. Every workspace session
  // this process runs is built over this same bootstrap.
  private DesktopBootstrap? _boot;

  private IClassicDesktopStyleApplicationLifetime? _desktop;

  // The workspace session currently open, or null while the launcher is showing.
  private DesktopSession? _session;

  // The gate that makes this process the owner of one workspace's name, or null when no
  // workspace is open.
  private SingleInstanceGate? _workspaceGate;

  public override void Initialize() => AvaloniaXamlLoader.Load(this);

  public override void OnFrameworkInitializationCompleted()
  {
    // Transcript markdown rendering is MarkView-backed: defaults are set once so
    // every MarkdownViewer (transcript blocks and future surfaces) inherits the
    // same pipeline and highlighting. Registered outside the desktop-lifetime
    // gate so the headless test app exercises the identical wiring.
    MarkdownViewerDefaults.Pipeline = new MarkdownPipelineBuilder()
        .UseSupportedExtensions()
        .Build();
    MarkdownViewerDefaults.Extensions.AddTextMateHighlighting(ThemeName.DarkPlus, ThemeName.LightPlus);

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
      _desktop = desktop;

      // No window exists until bootstrap finishes; transient helper windows closing
      // must not trip Avalonia's default "shutdown when the last window closes"
      // behavior mid-startup.
      DesktopHost.DeferShutdownDuringStartup(desktop);

      // Startup is two phases: config load + key recovery + session-factory construction on a
      // background thread (no Avalonia controls), then the launcher on the UI thread. No
      // workspace is chosen at startup: the app always stops at the launcher, and no API key is
      // required until an agent runs. Infrastructure failures surface as an error dialog and a
      // non-zero exit, and the launcher is never shown after a failed bootstrap.
      _ = Task.Run(async () =>
      {
        try
        {
          DesktopBootstrap boot = await DesktopHost.PrepareAsync();
          await Dispatcher.UIThread.InvokeAsync(() =>
                {
                  _boot = boot;
                  ShowLauncher();
                });
        }
        // Named decision (CA1031): startup is a fault boundary — ANY failure must
        // surface in the error dialog, never kill the process silently.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
        {
          // Never exit silently: surface ANY bootstrap failure in a visible dialog.
          await Console.Error.WriteLineAsync(ex.ToString());
          await DesktopHost.ShowErrorAndExitAsync(desktop,
                    "eThang Agent failed to start: " + ex.Message);
        }
#pragma warning restore CA1031
      });
    }

    base.OnFrameworkInitializationCompleted();
  }

  /// <summary>Shows the launcher bound to the process's bootstrap. MUST run on the UI thread.
  ///     <para>THE LAUNCHER IS THE ONLY WINDOW THAT EXISTS WITH NO WORKSPACE, so closing it is
  ///     what ends the process; while it is up, shutdown stays explicit so the handoff to a main
  ///     window cannot end the app in the gap between the two windows.</para></summary>
  private void ShowLauncher()
  {
    if (_boot is not { } boot || _desktop is not { } desktop)
    {
      return;
    }

    LauncherWindow launcher = DesktopHost.CreateLauncherWindow(boot);
    LauncherViewModel vm = (LauncherViewModel)launcher.DataContext!;
    vm.WorkspaceChosen += async (_, handoff) => await OpenWorkspaceAsync(handoff).ConfigureAwait(true);
    vm.SettingsRequested += async (_, _) => await ShowSettingsAsync().ConfigureAwait(true);

    // With no workspace open, closing the launcher ends the process.
    DesktopHost.EnableWindowCloseShutdown(desktop);
    desktop.MainWindow = launcher;
    launcher.Show();
  }

  /// <summary>Opens one workspace: claims its instance name, records it as recent, closes the
  ///     launcher and shows the main window bound to that root. A root another process already
  ///     owns is signalled and ignored here.</summary>
  /// <param name="handoff">The workspace the launcher chose.</param>
  private async Task OpenWorkspaceAsync(WorkspaceHandoff handoff)
  {
    ArgumentNullException.ThrowIfNull(handoff);
    if (_boot is not { } boot || _desktop is not { } desktop)
    {
      return;
    }

    string root = handoff.WorkspaceRoot;
    if (WorkspaceInstanceHandoff.TrySignalExisting(root))
    {
      await Console.Error.WriteLineAsync(
          $"workspace '{root}' is already open in another process; that window was raised.").ConfigureAwait(true);
      return;
    }

    SingleInstanceGate? gate = WorkspaceInstanceHandoff.Acquire(root, signalled =>
        Dispatcher.UIThread.Post(() => RaiseForSignalledWorkspace(signalled)));
    if (gate is null)
    {
      await Console.Error.WriteLineAsync(
          $"workspace '{root}' is already open in another process; no second window was opened.")
          .ConfigureAwait(true);
      return;
    }

    _workspaceGate = gate;
    Result<bool> recorded = await boot.Recents.RecordAsync(root).ConfigureAwait(true);
    if (!recorded.IsSuccess)
    {
      await Console.Error.WriteLineAsync(
          $"the recent-workspace list could not record '{root}': {recorded.Error.Message}")
          .ConfigureAwait(true);
    }

    // THE HANDOFF GAP: the launcher closes before the main window exists, so shutdown stays
    // explicit across it and is restored once the main window is up.
    DesktopHost.DeferShutdownDuringStartup(desktop);
    Window? previous = desktop.MainWindow;
    DesktopSession session = DesktopHost.CreateMainWindow(boot, root);
    _session = session;
    session.Window.CloseWorkspaceRequested +=
        async (_, _) => await CloseWorkspaceAsync().ConfigureAwait(true);
    desktop.MainWindow = session.Window;
    session.Window.Show();
    DesktopHost.EnableWindowCloseShutdown(desktop);
    previous?.Close();
  }

  /// <summary>Closes the open workspace: the session tears every agent down and stops its loops,
  ///     the main window closes, and the launcher comes back over the SAME bootstrap — no second
  ///     database is opened and no loop is started twice.</summary>
  private async Task CloseWorkspaceAsync()
  {
    if (_session is not { } session)
    {
      return;
    }

    _session = null;
    await session.CloseWorkspaceAsync().ConfigureAwait(true);
    await session.DisposeAsync().ConfigureAwait(true);
    _workspaceGate?.Dispose();
    _workspaceGate = null;

    // The main window closes only after its teardown, so the process never exits mid-teardown.
    session.Window.Close();
    ShowLauncher();
  }

  /// <summary>A launch in another process asked this one to raise the window for a workspace.
  ///     The window is raised when it is already open for that root; otherwise the request is
  ///     ignored with one stderr line naming the root.</summary>
  /// <param name="signalledRoot">The root the signalling launch asked for.</param>
  private void RaiseForSignalledWorkspace(string signalledRoot)
  {
    if (_session is { } session && session.RaiseFor(signalledRoot))
    {
      return;
    }

    _ = Console.Error.WriteLineAsync(
        $"a launch asked for workspace '{signalledRoot}', which is not open in this process; ignored.");
  }

  /// <summary>Shows the app Settings window from the launcher, before any workspace exists: the
  ///     prefill comes from the bootstrap and a confirmed save lands through the same
  ///     <see cref="SettingsPersistence"/> the shell's save uses.</summary>
  private async Task ShowSettingsAsync()
  {
    if (_boot is not { } boot)
    {
      return;
    }

    if (ActiveWindow() is not { } owner)
    {
      return;
    }

    SettingsWindow dialog = new(DesktopHost.SettingsValuesFrom(boot));
    SettingsUpdate? update = await dialog.ShowDialog<SettingsUpdate?>(owner).ConfigureAwait(true);
    if (update is null)
    {
      return; // user cancelled — no-op
    }

    (string? openRouterKey, string? managementKey) = await new SettingsPersistence(
        boot.Preferences, boot.ApiKeys)
        .ApplyAsync(update, boot.PreferredProviderId).ConfigureAwait(true);

    // The next workspace's agents read the key from the bootstrap's settings, so the save is
    // applied here as well as persisted: a key entered with no workspace open is in force the
    // moment a workspace opens.
    AgentSettings applied = boot.Settings.WithApiKeys(openRouterKey).WithManagementKey(managementKey);
    _boot = boot with
    {
      Settings = applied,
      Sessions = boot.Sessions.WithSettings(applied),
      CommitStyle = update.CommitStyle,
      CompactionHandoff = update.CompactionHandoff,
    };
  }

  /// <summary>The window a dialog should be owned by: the open workspace's window, else the
  ///     launcher.</summary>
  private Window? ActiveWindow() => _session?.Window ?? _desktop?.MainWindow;
}
