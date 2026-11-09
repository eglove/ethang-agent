using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The host owns one connection to the workspace daemon and must stop treating
/// a dead pipe as a live one. A daemon that exits leaves the cached client unusable, and
/// every later query would fail for the life of the process unless the host notices and
/// reconnects.</summary>
[Trait("Category", "Process")]
public class LspDaemonHostReconnectTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private sealed class NeverWatcher : IFileWatcher
  {
    public long Generation => 0;

    public IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler) => new NoSubscription();

    private sealed class NoSubscription : IDisposable
    {
      public void Dispose()
      {
      }
    }
  }

  /// <summary>A running one-shot daemon plus the switch that stops it. Stopping cancels
  /// the serve loop, which disposes its pipe stream - the same closed pipe a crashed or
  /// killed daemon leaves behind.</summary>
  private sealed class OneShotDaemon : IDisposable
  {
    private readonly LspDaemonServer _server;
    private readonly CancellationTokenSource _cts;
    private readonly Task _serve;

    private OneShotDaemon(LspDaemonServer server, CancellationTokenSource cts, Task serve)
      => (_server, _cts, _serve) = (server, cts, serve);

    // Named decision (CA2000): both disposables are owned by the returned OneShotDaemon,
    // whose Dispose releases them; the analyzer cannot follow ownership through a factory.
#pragma warning disable CA2000 // Dispose objects before losing scope
    /// <summary>Waits until the daemon is actually listening, so a caller never races
    ///     the accept loop it just started.</summary>
    public static async Task<OneShotDaemon> StartAsync(string pipeName, string workspaceId)
    {
      LspDaemonServer server = new(pipeName, workspaceId, () => Task.CompletedTask);
      // A symbol service answers lsp.symbol; without one the daemon stays silent and the
      // caller blocks instead of testing anything about the connection.
      server.AttachSymbolService(new LspSymbolService(null, LspCapabilities.None));
      CancellationTokenSource cts = new(TestCeiling.Value);
      Task serve = Task.Run(async () =>
      {
        try
        {
          await server.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
          // Cancelling or the client hanging up is the expected end of this daemon.
        }
      }, CancellationToken.None);
      OneShotDaemon daemon = new(server, cts, serve);
#pragma warning restore CA2000 // Dispose objects before losing scope
      for (int attempt = 0; attempt < 100; attempt++)
      {
        try
        {
          using LspPipeClient probe = await LspPipeClient.ConnectAsync(pipeName, workspaceId, "probe", Ct).ConfigureAwait(true);
          break;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
          await Task.Delay(50, Ct).ConfigureAwait(true);
        }
      }

      return daemon;
    }

    public void Dispose()
    {
      _cts.Cancel();
      // The serve loop observes the cancel on its own thread; the join is the fact, the
      // duration is only how long teardown may take before it is a leak. The token is the
      // one just cancelled, so the join opts out of it explicitly rather than racing it.
      _ = _serve.Wait(TestCeiling.Value, CancellationToken.None);
      _server.Dispose();
      _cts.Dispose();
    }
  }

  [Fact]
  public async Task EnsureConnected_AfterDaemonDies_ReconnectsInsteadOfReturningTheDeadClient()
  {
    string root = TestTempRoot.CreateDirectory("lsp-reconnect").FullName;
    string workspaceId = "ws-reconnect-" + Guid.NewGuid().ToString("N");
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "public class A { }", Ct).ConfigureAwait(true);

      OneShotDaemon first = await OneShotDaemon.StartAsync(pipeName, workspaceId).ConfigureAwait(true);
      using LspDaemonHost host = new(workspaceId, root, "app-1",
          () => throw new InvalidOperationException("no daemon process to start in this test"),
          _ => new NeverWatcher());

      LspPipeClient? live = await host.EnsureConnectedAsync(Ct).ConfigureAwait(true);
      Assert.NotNull(live);

      // The daemon goes away. A joined connection carries no process handle, so the
      // failure itself is the evidence: the next request on it fails.
      first.Dispose();
      await Task.Delay(200, Ct).ConfigureAwait(true);
      await Assert.ThrowsAnyAsync<Exception>(async () =>
          await live.SymbolAsync("document-symbols", "file:///C:/ws/a.cs", string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true)).ConfigureAwait(true);
      Assert.True(live.IsBroken, "a failed request must mark the client broken");

      // Nothing is listening now, and no process can be started, so the honest answer is
      // null - but the host must have DISCARDED the dead client to get there.
      LspPipeClient? afterDeath = await host.EnsureConnectedAsync(Ct).ConfigureAwait(true);
      Assert.Null(afterDeath);
      Assert.Null(host.Client);
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  [Fact]
  public async Task EnsureConnected_AfterDaemonRestarts_ReturnsAWorkingClient()
  {
    string root = TestTempRoot.CreateDirectory("lsp-reconnect2").FullName;
    string workspaceId = "ws-reconnect2-" + Guid.NewGuid().ToString("N");
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "public class A { }", Ct).ConfigureAwait(true);

      OneShotDaemon first = await OneShotDaemon.StartAsync(pipeName, workspaceId).ConfigureAwait(true);
      using LspDaemonHost host = new(workspaceId, root, "app-1",
          () => throw new InvalidOperationException("no daemon process to start in this test"),
          _ => new NeverWatcher());

      LspPipeClient? live = await host.EnsureConnectedAsync(Ct).ConfigureAwait(true);
      Assert.NotNull(live);

      first.Dispose();
      await Task.Delay(200, Ct).ConfigureAwait(true);
      await Assert.ThrowsAnyAsync<Exception>(async () =>
          await live.SymbolAsync("document-symbols", "file:///C:/ws/a.cs", string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true)).ConfigureAwait(true);

      // A replacement daemon comes up on the same pipe name.
      OneShotDaemon second = await OneShotDaemon.StartAsync(pipeName, workspaceId).ConfigureAwait(true);
      try
      {
        LspPipeClient? reconnected = await host.EnsureConnectedAsync(Ct).ConfigureAwait(true);
        Assert.NotNull(reconnected);
        Assert.NotSame(live, reconnected);

        // The reconnected client must actually carry a round trip, not merely exist:
        // a reply of any state proves the pipe is live and the daemon answered.
        LspSymbolResult result = await reconnected.SymbolAsync("document-symbols", "file:///C:/ws/a.cs", string.Empty, 0, 1, newName: string.Empty, Ct).ConfigureAwait(true);
        Assert.NotNull(result);
      }
      finally
      {
        second.Dispose();
      }
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  private static void DeleteWorkspace(string root)
  {
    // Best effort, and deliberately ONE attempt: a watcher or a just-killed process can still
    // hold a handle here, and debris under the run root is swept by TestTempRoot.Delete at the
    // end of the run. Retrying against a clock would spend the whole ceiling on a delete that
    // is not the thing under test.
    try
    {
      Directory.Delete(root, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
      // Left for the run-root sweep.
    }
  }
}
