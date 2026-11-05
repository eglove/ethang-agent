using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The binder is the one line between the daemon's announcements and the status
///     bar. It seeds whatever the daemon already said (a session opened after the download
///     finished must still show a ready server) and follows every later change.</summary>
public class LspStatusBinderTests
{
  [Fact]
  public void Attaching_SeedsWhateverTheDaemonAlreadyAnnounced()
  {
    LspServerStatusMonitor monitor = new();
    _ = monitor.Report(LspServerInstallState.Ready, "5.13.0-1.26514.10");
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    using LspStatusBinder binder = LspStatusBinder.Attach(monitor, status)!;

    Assert.NotNull(binder);
    Assert.Equal("C# server: ready 5.13.0-1.26514.10", status.ServerDisplay);
  }

  [Fact]
  public void Attaching_ToAnUnknownDaemon_LeavesTheSegmentEmpty()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    using LspStatusBinder binder = LspStatusBinder.Attach(new LspServerStatusMonitor(), status)!;

    Assert.NotNull(binder);
    Assert.Equal("", status.ServerDisplay);
  }

  [Fact]
  public void ALaterAnnouncement_ReachesTheStatusBar()
  {
    LspServerStatusMonitor monitor = new();
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    using LspStatusBinder binder = LspStatusBinder.Attach(monitor, status)!;
    Assert.NotNull(binder);

    _ = monitor.Report(LspServerInstallState.Downloading, string.Empty);

    Assert.Equal("C# server: downloading (64 MB)", status.ServerDisplay);
  }

  [Fact]
  public void Disposing_TheBinder_StopsFollowing()
  {
    LspServerStatusMonitor monitor = new();
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    LspStatusBinder binder = LspStatusBinder.Attach(monitor, status)!;

    binder.Dispose();
    _ = monitor.Report(LspServerInstallState.Unavailable, string.Empty);

    Assert.Equal("", status.ServerDisplay);
  }

  [Fact]
  public void NoMonitor_AttachesNothing()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    Assert.Null(LspStatusBinder.Attach(null, status));
  }
}
