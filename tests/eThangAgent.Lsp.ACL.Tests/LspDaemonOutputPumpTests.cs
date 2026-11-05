using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The daemon's stdout is the only channel its install work is visible on, and
///     <see cref="LspDaemonHost"/> used to drain it into nothing. The pump is what turns
///     those lines into the observable state the status bar reads.</summary>
public class LspDaemonOutputPumpTests
{
  [Fact]
  public async Task EveryAnnouncementInTheStream_ReachesTheMonitor()
  {
    LspServerStatusMonitor monitor = new();
    List<LspServerInstallState> seen = [];
    monitor.Changed += (_, changed) => seen.Add(changed.State);
    StringReader output = new(
        "daemon-starting ethang-lsp-0000\n"
        + "server-resolving ws-abc\n"
        + "server-ready 5.13.0-1.26514.10\n"
        + "daemon-ready ws-abc\n");

    await LspDaemonOutputPump.DrainAsync(output, monitor, TestContext.Current.CancellationToken);

    Assert.Equal(
        [LspServerInstallState.Downloading, LspServerInstallState.Ready],
        seen);
    Assert.Equal("5.13.0-1.26514.10", monitor.Version);
  }

  [Fact]
  public async Task AnUnavailableAnnouncement_ReachesTheMonitor()
  {
    LspServerStatusMonitor monitor = new();
    StringReader output = new("server-unavailable\n");

    await LspDaemonOutputPump.DrainAsync(output, monitor, TestContext.Current.CancellationToken);

    Assert.Equal(LspServerInstallState.Unavailable, monitor.State);
  }

  [Fact]
  public async Task AStreamOfChatterOnly_LeavesTheMonitorUnknown()
  {
    LspServerStatusMonitor monitor = new();
    StringReader output = new("daemon-starting x\ndaemon-ready x\n");

    await LspDaemonOutputPump.DrainAsync(output, monitor, TestContext.Current.CancellationToken);

    Assert.Equal(LspServerInstallState.Unknown, monitor.State);
  }

  [Fact]
  public async Task AnEmptyStream_CompletesWithoutAnnouncing()
  {
    LspServerStatusMonitor monitor = new();

    await LspDaemonOutputPump.DrainAsync(
        new StringReader(string.Empty), monitor, TestContext.Current.CancellationToken);

    Assert.Equal(LspServerInstallState.Unknown, monitor.State);
  }
}
