using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The daemon's routing over its own pipe: a request for a path a running server
///     claims reaches that server, and one for a path no server claims is answered empty
///     rather than as an error. The servers are recorders, so the route is what is observed.
///     </summary>
[Trait("Category", "Process")]
public class LspServerSetPipeTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private static LspServerLaunch Launch(string languageId) =>
      new(languageId, "C:\\servers\\" + languageId + ".exe", "1.0.0");

  private sealed class FakeStarter : ILspServerStarter
  {
    public Task<LspServerConnection> StartAsync(LspServerLaunch launch, string workspaceRoot, CancellationToken ct)
    {
      MemoryStream input = new();
      MemoryStream output = new();
      LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
      LspClient client = new(transport, new LspServerConfig(
          launch.LanguageId, launch.ServerPath, [], LspServerCatalog.ForLanguage(launch.LanguageId)!.Extensions));
      return Task.FromResult(new LspServerConnection(transport, client));
    }
  }

  private sealed class FakeDiagnostics(string languageId) : ILspDiagnosticsQuery
  {
    public Task<LspDiagnosticsResult> PullAsync(string uri, CancellationToken ct = default)
      => Task.FromResult(new LspDiagnosticsResult(
          LspDiagnosticsState.Ready,
          [new LspDiagnostic(uri, 0, 0, 0, 1, languageId, "reported by " + languageId, 1)]));

    public void BeginRestart()
    {
    }

    public Task CompleteRestartAsync() => Task.CompletedTask;
  }

  private sealed class FakeSymbols(string languageId) : ILspSymbolQuery
  {
    public Task<LspSymbolResult> ExecuteAsync(
        string action, string uri, string symbolName, int occurrence, int depth, string newName,
        CancellationToken ct = default)
      => Task.FromResult(new LspSymbolResult(
          LspSymbolState.Ready, [new LspSymbolSite(uri, 1, 2, languageId)], occurrence, 1));
  }

  /// <summary>One C# server whose services record what they were asked, so a request that
  ///     reaches it is observable and a request that does not is empty.</summary>
  private static async Task<LspServerSet> StartRecordingSetAsync(string workspaceId)
  {
    string root = TestTempRoot.CreateDirectory("lspsetpipe").FullName;
    LspServerSet started = await LspServerSet.StartAsync(
        [Launch("csharp")], workspaceId, root, new FakeStarter(), Ct);
    LspRunningServer server = started.Servers[0];
    List<LspRunningServer> replaced = [];
    replaced.Add(new LspRunningServer(
        server.Row, server.Launch, server.Connection, server.Documents,
        new FakeDiagnostics(server.LanguageId),
        new FakeSymbols(server.LanguageId)));
    return LspServerSet.FromServers(replaced);
  }

  [Fact]
  public async Task Daemon_RoutesDiagnosticsOverThePipe_ByExtension()
  {
    string ws = "ws-set-pipe-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspServerSet set = await StartRecordingSetAsync(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachServers(set);
    using CancellationTokenSource cts = new(TestCeiling.Value);
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
    await Task.Delay(150, Ct);

    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", Ct);
    AdvisoryDiagnosticsResult claimed = await client.GetDiagnosticsAsync("C:\\ws\\a.cs", Ct);
    AdvisoryDiagnosticsResult unclaimed = await client.GetDiagnosticsAsync("C:\\ws\\a.rs", Ct);

    Assert.Equal("csharp", Assert.Single(claimed.Diagnostics).Code);
    Assert.Empty(unclaimed.Diagnostics);

    await cts.CancelAsync();
    await serve.WaitAsync(TestCeiling.Value, CancellationToken.None);
  }

  [Fact]
  public async Task Daemon_RoutesSymbolsOverThePipe_AndAnswersUnclaimedPathsEmpty()
  {
    string ws = "ws-set-symbol-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspServerSet set = await StartRecordingSetAsync(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachServers(set);
    using CancellationTokenSource cts = new(TestCeiling.Value);
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
    await Task.Delay(150, Ct);

    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", Ct);
    LspSymbolResult claimed = await client.SymbolAsync(
        "definition", LspUriMapper.ToUri("C:\\ws\\a.cs"), "Foo", 0, 1, string.Empty, Ct);
    LspSymbolResult unclaimed = await client.SymbolAsync(
        "definition", LspUriMapper.ToUri("C:\\ws\\a.rs"), "Foo", 0, 1, string.Empty, Ct);

    Assert.Equal("csharp", Assert.Single(claimed.Sites).SymbolName);
    Assert.Empty(unclaimed.Sites);
    Assert.Equal(LspSymbolState.Ready, unclaimed.State);

    await cts.CancelAsync();
    await serve.WaitAsync(TestCeiling.Value, CancellationToken.None);
  }
}
