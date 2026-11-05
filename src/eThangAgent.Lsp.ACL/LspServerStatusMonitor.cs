namespace eThangAgent.Lsp.ACL;

/// <summary>What the language server is doing, as the daemon announces it, carried to
///     readers that never see the daemon process.
///     <para>THE DAEMON'S STDOUT IS THE ONLY SOURCE: the daemon resolves, downloads, and
///     launches the server in its own process, so no caller can observe that work directly.
///     It announces each step on stdout, <see cref="LspDaemonHost"/> reads those lines, and
///     this monitor is where they land.</para>
///     <para>UNKNOWN IS THE STARTING STATE AND IT RENDERS NOTHING: a workspace with no code
///     never announces at all, and a status bar that claimed 'unavailable' there would be
///     reporting a decision the daemon never made.</para>
///     <para>Subscribers run on the reporting thread, which is the daemon's stdout pump, so
///     they must marshal before touching a UI surface. A subscriber fault is contained: one
///     bad reader must not stop the others from learning what happened.</para></summary>
public sealed class LspServerStatusMonitor
{
  private readonly Lock _gate = new();
  private LspServerInstallState _state = LspServerInstallState.Unknown;
  private string _version = string.Empty;

  /// <summary>The last announced state, or <see cref="LspServerInstallState.Unknown"/> when
  ///     the daemon has announced nothing.</summary>
  public LspServerInstallState State
  {
    get
    {
      lock (_gate)
      {
        return _state;
      }
    }
  }

  /// <summary>The version the ready announcement named, or empty when none has.
  ///     </summary>
  public string Version
  {
    get
    {
      lock (_gate)
      {
        return _version;
      }
    }
  }

  /// <summary>Raised once per state CHANGE, with the new state. A repeated announcement
  ///     raises nothing: the reader has already been told.</summary>
  public event EventHandler<LspServerStatusChangedEventArgs>? Changed;

  /// <summary>Records one announcement and tells subscribers when it moved the state.
  ///     Returns true when it did.</summary>
  public bool Report(LspServerInstallState state, string version)
  {
    ArgumentNullException.ThrowIfNull(version);
    lock (_gate)
    {
      if (_state == state)
      {
        return false;
      }

      _state = state;
      _version = version;
    }

    EventHandler<LspServerStatusChangedEventArgs>? handlers = Changed;
    if (handlers is null)
    {
      return true;
    }

    LspServerStatusChangedEventArgs changed = new(state, version);
    foreach (EventHandler<LspServerStatusChangedEventArgs> handler in handlers.GetInvocationList()
        .Cast<EventHandler<LspServerStatusChangedEventArgs>>())
    {
      try
      {
        handler(this, changed);
      }
      // Named decision (CA1031): a status readout is an observer, and one reader's fault
      // must not silence the rest - the same rule the agent event stream follows.
      catch (Exception)
      {
        // Swallowed deliberately: see the named decision above.
      }
    }

    return true;
  }

  /// <summary>Feeds one line of the daemon's stdout. Returns true when the line was an
  ///     announcement and it moved the state; every other line the daemon writes is
  ///     lifecycle chatter that says nothing about the server.</summary>
  public bool Announce(string? line)
      => LspServerInstallLine.TryParse(line, out LspServerInstallState state, out string detail)
          && Report(state, detail);
}
