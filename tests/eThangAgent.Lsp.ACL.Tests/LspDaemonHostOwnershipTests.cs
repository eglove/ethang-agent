using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>A host owns the daemon it starts, so a disposed host must start nothing: a
///     daemon spawned after disposal is a child process no part of the app can reach
///     again, and it outlives the session that caused it.</summary>
public class LspDaemonHostOwnershipTests
{
  [Fact]
  public async Task EnsureConnected_AfterDispose_StartsNoDaemon()
  {
    string root = TestTempRoot.CreateDirectory("lsp-owned").FullName;
    string workspaceId = "owned-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
      int exeResolutions = 0;
      LspDaemonHost host = new(workspaceId, root, "app-1", () =>
      {
        exeResolutions++;
        return Path.Combine(root, "absent", "eThangAgent.Lsp.Host.exe");
      });

      host.Dispose();
      LspPipeClient? client = await host.EnsureConnectedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Null(client);
      Assert.Equal(0, exeResolutions);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Dispose_IsSafeToCallTwice_AndLeavesNoClient()
  {
    string root = TestTempRoot.CreateDirectory("lsp-twice").FullName;
    string workspaceId = "twice-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
      LspDaemonHost host = new(workspaceId, root, "app-1",
          () => Path.Combine(root, "absent", "eThangAgent.Lsp.Host.exe"));

      host.Dispose();
      host.Dispose();
      await host.DisposeAsync().ConfigureAwait(true);

      Assert.Null(host.Client);
      Assert.Null(host.DaemonProcessId);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
