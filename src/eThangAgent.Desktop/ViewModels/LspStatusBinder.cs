using eThangAgent.Lsp.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Carries the daemon's language-server announcements onto a
///     <see cref="StatusViewModel"/>.
///     <para>WHY A BINDER AND NOT A BINDING: the monitor raises on the daemon's stdout pump
///     thread, and the status bar's segment is set through a method that marshals. The
///     subscription has to be disposed with the session too, and a bare event handler has
///     nowhere to hang that.</para>
///     <para>IT SEEDS ON ATTACH: a session opened after the one-time download finished must
///     still show a ready server, and the monitor has no replay - the seed is the replay.
///     </para></summary>
internal sealed class LspStatusBinder : IDisposable
{
  private readonly LspServerStatusMonitor _monitor;
  private readonly StatusViewModel _status;
  private bool _disposed;

  private LspStatusBinder(LspServerStatusMonitor monitor, StatusViewModel status)
  {
    _monitor = monitor;
    _status = status;
  }

  /// <summary>Follows <paramref name="monitor"/> onto <paramref name="status"/>, seeding
  ///     whatever it has already announced. Null when there is no monitor to follow (a
  ///     container without the language-server seam), so a caller can pass the result
  ///     straight through.</summary>
  public static LspStatusBinder? Attach(LspServerStatusMonitor? monitor, StatusViewModel status)
  {
    ArgumentNullException.ThrowIfNull(status);
    if (monitor is null)
    {
      return null;
    }

    LspStatusBinder binder = new(monitor, status);
    status.SetServerStatus(monitor.State, monitor.Version);
    monitor.Changed += binder.OnChanged;
    return binder;
  }

  private void OnChanged(object? sender, LspServerStatusChangedEventArgs changed)
  {
    if (_disposed)
    {
      return;
    }

    _status.SetServerStatus(changed.State, changed.Version);
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _monitor.Changed -= OnChanged;
  }
}
