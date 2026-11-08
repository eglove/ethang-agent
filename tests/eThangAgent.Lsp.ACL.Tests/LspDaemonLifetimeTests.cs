using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

// A workspace's daemon serves EVERY host in that workspace, so it must outlive the session
// that happened to start it. Before this, whichever container started the daemon killed it
// on exit, destroying the warm language server its siblings were still using and forcing the
// next session to reload the whole solution - which is what surfaced as a permanent
// '[diagnostics] Starting'.
[Trait("Category", "Process")]
public class LspDaemonLifetimeTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>A second connection to a daemon that already has a client must be told NOT to
  ///     stop, so one session's exit never takes the server from another.</summary>
  [Fact]
  public async Task ShutdownIfIdle_WithAnotherClientConnected_Declines()
  {
    string ws = "life-shared-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    using CancellationTokenSource serveCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
    _ = Task.Run(async () => await server.ServeUntilClosedAsync(serveCts.Token).ConfigureAwait(false), serveCts.Token);
    await Task.Delay(150, Ct).ConfigureAwait(true);

    using LspPipeClient keeper = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", Ct).ConfigureAwait(true);
    using LspPipeClient leaving = await LspPipeClient.ConnectAsync(pipe, ws, "app-2", Ct).ConfigureAwait(true);

    bool shutdown = await leaving.ShutdownIfIdleAsync(Ct).WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

    Assert.False(shutdown, "a daemon with another client must not stop");
    await serveCts.CancelAsync().ConfigureAwait(true);
  }

  /// <summary>The last client leaving stops the daemon, so nothing is left running once the
  ///     workspace has no sessions.</summary>
  [Fact]
  public async Task ShutdownIfIdle_AsLastClient_StopsTheDaemon()
  {
    string ws = "life-last-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    using CancellationTokenSource serveCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
    _ = Task.Run(async () => await server.ServeUntilClosedAsync(serveCts.Token).ConfigureAwait(false), serveCts.Token);
    await Task.Delay(150, Ct).ConfigureAwait(true);

    using LspPipeClient only = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", Ct).ConfigureAwait(true);
    bool shutdown = await only.ShutdownIfIdleAsync(Ct).WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

    Assert.True(shutdown, "the last client leaving must stop the daemon");
    await serveCts.CancelAsync().ConfigureAwait(true);
  }
}
