namespace eThangAgent.Lsp.ACL;

/// <summary>One language-server status change: the state the daemon announced and, for a
///     ready announcement, the version it named.</summary>
public sealed class LspServerStatusChangedEventArgs(LspServerInstallState state, string version)
    : EventArgs
{
  /// <summary>The state the daemon announced.</summary>
  public LspServerInstallState State { get; } = state;

  /// <summary>The announced version, or empty when the announcement named none.</summary>
  public string Version { get; } = version;
}
