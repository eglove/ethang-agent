using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop;

/// <summary>One workspace's lifetime objects: the main window bound to that workspace, the
///     cancellation source every background loop observes, and the loops themselves.
///     <para>A SESSION BEGINS WHEN A WORKSPACE IS CHOSEN AND ENDS WHEN IT IS CLOSED, so the
///     watchdog, the resource governor, the RSS monitor and the retention sweep run for the life
///     of the workspace rather than the life of the process: closing a workspace stops them and
///     opening another starts a fresh set.</para></summary>
internal sealed class DesktopSession : IAsyncDisposable
{
  private readonly CancellationTokenSource _loops;
  private readonly MainViewModel? _vm;
  private int _stopped;

  /// <param name="window">The main window bound to this session's workspace.</param>
  /// <param name="vm">The shell behind the window, or null for a host that composed none.</param>
  /// <param name="loops">The source every background loop observes; the session owns it.</param>
  /// <param name="loopTasks">One task per running loop, for a caller that waits on them.</param>
  internal DesktopSession(MainWindow window, MainViewModel? vm, CancellationTokenSource loops,
      IReadOnlyList<Task> loopTasks)
  {
    ArgumentNullException.ThrowIfNull(window);
    ArgumentNullException.ThrowIfNull(loops);
    ArgumentNullException.ThrowIfNull(loopTasks);
    Window = window;
    _vm = vm;
    _loops = loops;
    Loops = loopTasks;
    WorkspaceRoot = vm?.WorkspaceRoot ?? string.Empty;

    // The window's own close ends the loops too: a window closed without the close-workspace
    // action must not leave a watchdog ticking against nothing.
    window.Closed += (_, _) => StopLoops();
  }

  /// <summary>The main window bound to this session's workspace.</summary>
  public MainWindow Window { get; }

  /// <summary>The workspace this session serves, normalized, or empty when the shell bound none.</summary>
  public string WorkspaceRoot { get; }

  /// <summary>The background loops this session started, one task each.</summary>
  public IReadOnlyList<Task> Loops { get; }

  /// <summary>Whether this session's loops have been asked to stop.</summary>
  public bool LoopsStopped => _loops.IsCancellationRequested;

  /// <summary>Raises this session's window when it serves the workspace a launch asked for.
  ///     False when it serves a different workspace, which the caller ignores.</summary>
  /// <param name="signalledRoot">The workspace the signalling launch named.</param>
  public bool RaiseFor(string signalledRoot)
  {
    if (string.IsNullOrWhiteSpace(signalledRoot)
        || !WorkspaceIdentity.PathsEqual(WorkspaceRoot, signalledRoot))
    {
      return false;
    }

    Window.Activate();
    return true;
  }

  /// <summary>Tears the workspace down: every agent is closed and every host process it started
  ///     is stopped, then the loops are cancelled. The window is left open - the host decides
  ///     what to show next.</summary>
  public async Task CloseWorkspaceAsync()
  {
    if (_vm is { } vm)
    {
      await vm.ShutdownAllAsync().ConfigureAwait(true);
    }

    StopLoops();
  }

  /// <summary>Disposes the session: the loops stop and the source is released.</summary>
  public ValueTask DisposeAsync()
  {
    StopLoops();
    return ValueTask.CompletedTask;
  }

  /// <summary>Cancels and disposes the loop source exactly once. Disposal races a cancel arriving
  ///     on the same close event; the loops observe the token only before the window can close, so
  ///     a late touch cannot occur.</summary>
  private void StopLoops()
  {
    if (Interlocked.Exchange(ref _stopped, 1) != 0)
    {
      return;
    }

    _loops.Cancel();
    _loops.Dispose();
  }
}
