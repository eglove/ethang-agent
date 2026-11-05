using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The host owns the daemon process, so it owns the only reader of the daemon's
///     stdout. Its monitor is the state a status bar binds: present from construction (a
///     reader can subscribe before any daemon exists) and unchanged until the daemon says
///     something.</summary>
public class LspDaemonHostStatusTests
{
  [Fact]
  public void TheHostExposesItsMonitor_WhichStartsUnknown()
  {
    using LspDaemonHost host = new("ws-status", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");

    Assert.NotNull(host.ServerStatus);
    Assert.Equal(LspServerInstallState.Unknown, host.ServerStatus.State);
  }

  [Fact]
  public void TwoHosts_NeverShareAMonitor()
  {
    using LspDaemonHost first = new("ws-a", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");
    using LspDaemonHost second = new("ws-b", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");

    Assert.NotSame(first.ServerStatus, second.ServerStatus);
  }
}
