using eThangAgent.Lsp.ACL;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #361: the app-side daemon seam. Composition used to wire the advisory source to
// a null client, so no edit could ever reach a daemon; these pin the connection contract
// (join an existing daemon, start one when the pipe is silent, Unavailable when neither
// works) and the daemon's single connection loop, which must serve a watcher
// announcement AND a diagnostics query on the SAME connection.
public class LspDaemonConnectionTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Connect_JoinsExistingDaemon_WithoutStartingOne()
  {
    string ws = "conn-join-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false), cts.Token);
    await Task.Delay(150, Ct).ConfigureAwait(true);

    bool started = false;
    await using LspDaemonConnection connection = (await LspDaemonConnection.ConnectAsync(
        ws, Path.GetTempPath(), "app-1",
        () =>
        {
          started = true;
          throw new InvalidOperationException("must not start a daemon when one is listening");
        }, Ct).ConfigureAwait(true))!;

    Assert.NotNull(connection.Client);
    Assert.Null(connection.DaemonProcessId);
    Assert.False(started);
    await cts.CancelAsync().ConfigureAwait(true);
    await serve.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(true);
  }

  [Fact]
  public async Task Connect_NoDaemonAndNoStartableExe_ReturnsNull()
  {
    string ws = "conn-none-" + Guid.NewGuid().ToString("N");
    LspDaemonConnection? connection = await LspDaemonConnection.ConnectAsync(
        ws, Path.GetTempPath(), "app-1",
        () => throw new InvalidOperationException("no daemon executable here"), Ct).ConfigureAwait(true);

    Assert.Null(connection);
  }

  [Fact]
  public async Task Daemon_ServesAnnouncementAndQuery_OnOneConnection()
  {
    string ws = "conn-mux-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    string root = Directory.CreateTempSubdirectory("lsp-mux").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", Ct).ConfigureAwait(true);
      LspDiagnosticStore store = new();
      string uri = LspUriMapper.ToUri(Path.Combine(root, "a.cs"));
      store.OnPublishDiagnostics(
          "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":5}},\"severity\":1,\"code\":\"CS1002\",\"message\":\"missing semicolon\"}]}");
      RecordingSync sync = new();
      LspDocumentSync forwarder = new(ws, root, "csharp", [".cs"], sync);
      using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
      server.AttachForwarder(forwarder);
      server.AttachDiagnosticsService(new LspDiagnosticsService(null, store));
      using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
      Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false), cts.Token);
      await Task.Delay(150, Ct).ConfigureAwait(true);

      using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", Ct).ConfigureAwait(true);
      await client.SendWatcherAnnouncementAsync(
          new WatcherAnnouncement(ws, ["a.cs"], [], 1, DateTimeOffset.UtcNow), Ct).ConfigureAwait(true);
      await Task.Delay(300, Ct).ConfigureAwait(true);
      AdvisoryDiagnosticsResult result = await client.GetDiagnosticsAsync(Path.Combine(root, "a.cs"), Ct).ConfigureAwait(true);

      Assert.Single(sync.Calls);
      Assert.Equal(AdvisoryDiagnosticsState.Ready, result.State);
      Assert.Equal("CS1002", Assert.Single(result.Diagnostics).Code);
      await cts.CancelAsync().ConfigureAwait(true);
      await serve.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(true);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class RecordingSync : ILspDocumentSync
  {
    public List<string> Calls { get; } = [];

    public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("open:" + uri);
      return Task.CompletedTask;
    }

    public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("change:" + uri);
      return Task.CompletedTask;
    }

    public Task DidCloseAsync(string uri, CancellationToken ct = default)
    {
      Calls.Add("close:" + uri);
      return Task.CompletedTask;
    }
  }
}
