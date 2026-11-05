using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The monitor is what carries the daemon's announcements to a reader that never
///     sees the daemon process. It starts Unknown (nothing announced yet, so nothing shows),
///     latches each announcement, and tells every subscriber what changed.</summary>
public class LspServerStatusMonitorTests
{
  [Fact]
  public void BeforeAnyAnnouncement_StateIsUnknown()
  {
    LspServerStatusMonitor monitor = new();

    Assert.Equal(LspServerInstallState.Unknown, monitor.State);
    Assert.Equal(string.Empty, monitor.Version);
  }

  [Fact]
  public void Report_RaisesChangedWithTheNewState()
  {
    LspServerStatusMonitor monitor = new();
    List<LspServerInstallState> seen = [];
    monitor.Changed += (_, changed) => seen.Add(changed.State);

    monitor.Report(LspServerInstallState.Downloading, string.Empty);

    Assert.Equal(LspServerInstallState.Downloading, monitor.State);
    Assert.Equal([LspServerInstallState.Downloading], seen);
  }

  [Fact]
  public void Report_CarriesTheReadyVersion()
  {
    LspServerStatusMonitor monitor = new();

    monitor.Report(LspServerInstallState.Ready, "5.13.0-1.26514.10");

    Assert.Equal(LspServerInstallState.Ready, monitor.State);
    Assert.Equal("5.13.0-1.26514.10", monitor.Version);
  }

  [Fact]
  public void RepeatingTheSameState_DoesNotRaiseAgain()
  {
    LspServerStatusMonitor monitor = new();
    List<LspServerInstallState> seen = [];
    monitor.Changed += (_, changed) => seen.Add(changed.State);

    monitor.Report(LspServerInstallState.Downloading, string.Empty);
    monitor.Report(LspServerInstallState.Downloading, string.Empty);

    Assert.Equal([LspServerInstallState.Downloading], seen);
  }

  [Fact]
  public void ASubscriberThatThrows_DoesNotStopTheOthers()
  {
    LspServerStatusMonitor monitor = new();
    List<LspServerInstallState> seen = [];
    monitor.Changed += (_, _) => throw new InvalidOperationException("subscriber fault");
    monitor.Changed += (_, changed) => seen.Add(changed.State);

    monitor.Report(LspServerInstallState.Unavailable, string.Empty);

    Assert.Equal([LspServerInstallState.Unavailable], seen);
    Assert.Equal(LspServerInstallState.Unavailable, monitor.State);
  }

  [Fact]
  public void AnnouncedLine_ReachesTheMonitor()
  {
    LspServerStatusMonitor monitor = new();

    Assert.True(monitor.Announce("server-ready 5.13.0-1.26514.10"));

    Assert.Equal(LspServerInstallState.Ready, monitor.State);
    Assert.Equal("5.13.0-1.26514.10", monitor.Version);
  }

  [Fact]
  public void LifecycleChatter_ChangesNothing()
  {
    LspServerStatusMonitor monitor = new();

    Assert.False(monitor.Announce("daemon-starting ethang-lsp-0000"));

    Assert.Equal(LspServerInstallState.Unknown, monitor.State);
  }
}
