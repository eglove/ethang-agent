using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

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
  public void TheHost_StartsUnstarted()
  {
    using LspDaemonHost host = new("ws-status", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");

    Assert.False(host.IsStarted);
  }

  [Fact]
  public async Task StartAsync_MarksTheHostStarted_AndIsIdempotent()
  {
    string root = TestTempRoot.CreateDirectory("lsp-start").FullName;
    string workspaceId = "ws-start-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "public class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);

      using LspDaemonHost host = new(workspaceId, root, "app-1",
          () => throw new InvalidOperationException("no daemon process to start in this test"));

      Assert.False(host.IsStarted);
      _ = await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(host.IsStarted);

      _ = await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(host.IsStarted);
    }
    finally
    {
      Directory.Delete(root, true);
    }
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
