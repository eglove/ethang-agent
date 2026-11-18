using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using eThangAgent.Windows.ACL;

namespace eThangAgent.Desktop;

/// <summary>What the process shows and in which order: the launcher while no workspace is open,
///     and one workspace's main window while one is.
///     <para>ONE WORKSPACE PER PROCESS. The host holds at most one <see cref="DesktopSession"/>,
///     and opening a second workspace while one is open is refused rather than replacing it. A
///     launch for a workspace another process already owns is signalled and ignored, so two
///     windows can never write one workspace's history.</para>
///     <para>THE HOST OWNS THE WINDOW SEQUENCE, not the application object: the same steps run
///     whether a workspace is chosen from the launcher or a launch signals an open one, and the
///     sequence is what a test drives. Every member runs on the UI thread.</para></summary>
internal sealed class WorkspaceHost : IDisposable
{
  private readonly IClassicDesktopStyleApplicationLifetime _desktop;
  private SingleInstanceGate? _gate;
  private bool _disposed;

  /// <param name="desktop">The lifetime the windows are registered with.</param>
  /// <param name="bootstrap">The process's prepared bootstrap: one app database, one preference
  ///     store, one session factory. It is replaced, never rebuilt, when settings change.</param>
  public WorkspaceHost(IClassicDesktopStyleApplicationLifetime desktop, DesktopBootstrap bootstrap)
  {
    ArgumentNullException.ThrowIfNull(desktop);
    ArgumentNullException.ThrowIfNull(bootstrap);
    _desktop = desktop;
    Bootstrap = bootstrap;
  }

  /// <summary>The process's bootstrap. Assigning a new one carries the settings a save applied,
  ///     so the next workspace's agents read them.</summary>
  public DesktopBootstrap Bootstrap { get; set; }

  /// <summary>The launcher window currently shown, or null once a workspace is open.</summary>
  public LauncherWindow? Launcher { get; private set; }

  /// <summary>The open workspace's lifetime objects, or null while the launcher is showing.
  ///     <para>The window's own close runs the shell's teardown, so a session is retired by the
  ///     window it belongs to; the host releases only this process's claim on the workspace's
  ///     name.</para></summary>
  public DesktopSession? Session { get; private set; }

  /// <summary>Raised when the launcher's Settings entry is used, before any workspace exists.</summary>
  public event EventHandler? SettingsRequested;

  /// <summary>Shows the launcher over this process's bootstrap. Closing it with no workspace open
  ///     is what ends the process, so shutdown is window-close driven while it is up.</summary>
  public void ShowLauncher()
  {
    Dispatcher.UIThread.VerifyAccess();
    ObjectDisposedException.ThrowIf(_disposed, this);

    LauncherWindow launcher = DesktopHost.CreateLauncherWindow(Bootstrap);
    LauncherViewModel vm = (LauncherViewModel)launcher.DataContext!;
    vm.WorkspaceChosen += async (_, handoff) => await OpenWorkspaceAsync(handoff).ConfigureAwait(true);
    vm.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    DesktopHost.EnableWindowCloseShutdown(_desktop);
    _desktop.MainWindow = launcher;
    Launcher = launcher;
    launcher.Show();
  }

  /// <summary>Opens one workspace: claims its instance name, records it as recent, closes the
  ///     launcher and shows the main window bound to that root.
  ///     <para>Returns false when nothing was opened - a root another process owns, a name this
  ///     process could not claim, or a workspace already open here - and leaves the launcher
  ///     showing, which is what makes the request ignorable rather than a second window.</para>
  ///     </summary>
  /// <param name="handoff">The workspace the launcher chose.</param>
  public async Task<bool> OpenWorkspaceAsync(WorkspaceHandoff handoff)
  {
    ArgumentNullException.ThrowIfNull(handoff);
    Dispatcher.UIThread.VerifyAccess();
    ObjectDisposedException.ThrowIf(_disposed, this);

    string root = handoff.WorkspaceRoot;
    if (Session is { } open)
    {
      await Console.Error.WriteLineAsync(
          $"workspace '{open.WorkspaceRoot}' is already open in this process; '{root}' was not opened.")
          .ConfigureAwait(true);
      return false;
    }

    if (WorkspaceInstanceHandoff.TrySignalExisting(root))
    {
      await Console.Error.WriteLineAsync(
          $"workspace '{root}' is already open in another process; that window was raised.")
          .ConfigureAwait(true);
      return false;
    }

    SingleInstanceGate? gate = WorkspaceInstanceHandoff.Acquire(root,
        signalled => Dispatcher.UIThread.Post(() => RaiseFor(signalled)));
    if (gate is null)
    {
      await Console.Error.WriteLineAsync(
          $"workspace '{root}' is already open in another process; no second window was opened.")
          .ConfigureAwait(true);
      return false;
    }

    _gate = gate;
    Result<bool> recorded = await Bootstrap.Recents.RecordAsync(root).ConfigureAwait(true);
    if (!recorded.IsSuccess)
    {
      await Console.Error.WriteLineAsync(
          $"the recent-workspace list could not record '{root}': {recorded.Error.Message}")
          .ConfigureAwait(true);
    }

    // THE HANDOFF GAP: the launcher closes before the main window exists, so shutdown stays
    // explicit across it and is restored once the main window is up.
    DesktopHost.DeferShutdownDuringStartup(_desktop);
    Window? previous = _desktop.MainWindow;
    DesktopSession session = DesktopHost.CreateMainWindow(Bootstrap, root);
    Session = session;
    Launcher = null;
    session.Window.CloseWorkspaceRequested +=
        async (_, _) => await CloseWorkspaceAsync().ConfigureAwait(true);
    _desktop.MainWindow = session.Window;
    session.Window.Show();
    DesktopHost.EnableWindowCloseShutdown(_desktop);
    previous?.Close();
    return true;
  }

  /// <summary>Closes the open workspace: every agent is torn down, the workspace's loops stop, the
  ///     main window closes and the launcher comes back over the SAME bootstrap - no second
  ///     database is opened and no loop is started twice. The process keeps running.
  ///     <para>Returns false when no workspace was open.</para></summary>
  public async Task<bool> CloseWorkspaceAsync()
  {
    Dispatcher.UIThread.VerifyAccess();
    if (Session is not { } session)
    {
      return false;
    }

    Session = null;
    await session.CloseWorkspaceAsync().ConfigureAwait(true);
    await session.DisposeAsync().ConfigureAwait(true);
    _gate?.Dispose();
    _gate = null;

    // THE SAME GAP IN REVERSE: the main window closes before the launcher exists, so shutdown
    // stays explicit across it and is restored once the launcher is up.
    DesktopHost.DeferShutdownDuringStartup(_desktop);
    session.Window.Close();
    ShowLauncher();
    return true;
  }

  /// <summary>Raises this process's window when it serves the workspace a launch asked for. False
  ///     when it serves a different workspace or none, and the request is then reported and
  ///     ignored.</summary>
  /// <param name="signalledRoot">The workspace the signalling launch named.</param>
  public bool RaiseFor(string signalledRoot)
  {
    if (Session is { } session && session.RaiseFor(signalledRoot))
    {
      return true;
    }

    _ = Console.Error.WriteLineAsync(
        $"a launch asked for workspace '{signalledRoot}', which is not open in this process; ignored.");
    return false;
  }

  /// <summary>Releases this process's claim on the open workspace's name. Windows are left to the
  ///     lifetime: disposal is what a host that is giving up does, not what closing a workspace
  ///     does.</summary>
  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _gate?.Dispose();
    _gate = null;
  }
}
