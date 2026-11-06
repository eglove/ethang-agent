using eThangAgent.Lsp.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The host owns one connection to the workspace daemon and must stop treating
/// a dead pipe as a live one. A daemon that exits leaves the cached client unusable, and
/// every later query would fail for the life of the process unless the host notices and
/// reconnects.</summary>
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

    private OneShotDaemon(LspDaemonServer server, CancellationTokenSource cts)
      => (_server, _cts) = (server, cts);

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
      CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
      OneShotDaemon daemon = new(server, cts);
#pragma warning restore CA2000 // Dispose objects before losing scope
      _ = Task.Run(async () =>
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
      _server.Dispose();
      _cts.Dispose();
      Thread.Sleep(150);
    }
  }

  [Fact]
  public async Task EnsureConnected_AfterDaemonDies_ReconnectsInsteadOfReturningTheDeadClient()
  {
    string root = Directory.CreateTempSubdirectory("lsp-reconnect").FullName;
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
    string root = Directory.CreateTempSubdirectory("lsp-reconnect2").FullName;
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
    for (int attempt = 0; attempt < 20; attempt++)
    {
      try
      {
        Directory.Delete(root, recursive: true);
        return;
      }
      catch (IOException)
      {
        Thread.Sleep(50);
      }
      catch (UnauthorizedAccessException)
      {
        Thread.Sleep(50);
      }
    }
  }
}
