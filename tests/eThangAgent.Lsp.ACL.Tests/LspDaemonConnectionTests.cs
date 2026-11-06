using System.Text;
using System.Text.Json.Nodes;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
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
    await serve.WaitAsync(TestCeiling.Value, CancellationToken.None).ConfigureAwait(true);
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
      // An edit-time query is not watcher-driven, so the announcement that moved the
      // forwarder's generation must not turn the query into a stale answer.
      AdvisoryDiagnosticsResult result = await client.GetDiagnosticsAsync(Path.Combine(root, "a.cs"), Ct).ConfigureAwait(true);

      Assert.Single(sync.Calls);
      Assert.Equal(AdvisoryDiagnosticsState.Ready, result.State);
      Assert.Equal("CS1002", Assert.Single(result.Diagnostics).Code);
      await cts.CancelAsync().ConfigureAwait(true);
      await serve.WaitAsync(TestCeiling.Value, CancellationToken.None).ConfigureAwait(true);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>A rename crosses the pipe as its own action and carries the new name: the
  ///     daemon routes it to the symbol service, which puts newName on the language-server
  ///     wire, and the computed edit set comes back in the reply.</summary>
  [Fact]
  public async Task Rename_Command_CarriesTheNewNameOnTheWire()
  {
    string ws = "ws-rename-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    string uri = LspUriMapper.ToUri("C:\\ws\\A.cs");
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(ResponseJson(1, DocumentSymbolsJson()));
    transport.EnqueueIncomingForTest(ResponseJson(2, WorkspaceEditJson(uri)));
    LspCapabilities capabilities = LspCapabilities.None with { DocumentSymbolProvider = true, RenameProvider = true };
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachSymbolService(new LspSymbolService(transport, capabilities));
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
    await Task.Delay(150, cts.Token);

    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
#pragma warning disable CA2025 // the client outlives the factory task that hands it back
    LspDaemonSymbolAccess access = new(_ => Task.FromResult<LspPipeClient?>(client), _ => true);
#pragma warning restore CA2025
    LspSymbolOutcome outcome = await access.ExecuteAsync(
        new LspSymbolCommand.Rename("C:\\ws\\A.cs", "Foo", 0, "Bar"), cts.Token);

    LspSymbolOutcome.Renamed renamed = Assert.IsType<LspSymbolOutcome.Renamed>(outcome);
    LspRenameEdit edit = Assert.Single(renamed.Edits);
    Assert.Equal("Bar", edit.NewText);
    Assert.Equal("C:\\ws\\A.cs", edit.Path);
    Assert.Equal((5, 8), (edit.Character, edit.EndCharacter));
    Assert.False(renamed.Applied);
    string languageServerWire = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("textDocument/rename", languageServerWire, StringComparison.Ordinal);
    Assert.Contains("\"newName\":\"Bar\"", languageServerWire, StringComparison.Ordinal);

    await cts.CancelAsync();
    await serve.WaitAsync(TestCeiling.Value, CancellationToken.None);
  }

  /// <summary>A rename the server answers with a file move is reported as unsupported: a
  ///     text-edit applier cannot perform it, and a partial application is never offered.</summary>
  [Fact]
  public async Task Rename_WithFileRenames_ReportsUnsupported()
  {
    string ws = "ws-rename-move-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    string uri = LspUriMapper.ToUri("C:\\ws\\A.cs");
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(ResponseJson(1, DocumentSymbolsJson()));
    transport.EnqueueIncomingForTest(ResponseJson(2, FileRenameWorkspaceEditJson(uri)));
    LspCapabilities capabilities = LspCapabilities.None with { DocumentSymbolProvider = true, RenameProvider = true };
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachSymbolService(new LspSymbolService(transport, capabilities));
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
    await Task.Delay(150, cts.Token);

    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
#pragma warning disable CA2025 // the client outlives the factory task that hands it back
    LspDaemonSymbolAccess access = new(_ => Task.FromResult<LspPipeClient?>(client), _ => true);
#pragma warning restore CA2025
    LspSymbolOutcome outcome = await access.ExecuteAsync(
        new LspSymbolCommand.Rename("C:\\ws\\A.cs", "Foo", 0, "Bar"), cts.Token);

    LspSymbolOutcome.Unsupported unsupported = Assert.IsType<LspSymbolOutcome.Unsupported>(outcome);
    Assert.Contains("file", unsupported.Message, StringComparison.OrdinalIgnoreCase);

    await cts.CancelAsync();
    await serve.WaitAsync(TestCeiling.Value, CancellationToken.None);
  }

  private static string ResponseJson(int id, JsonNode result)
    => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

  private static JsonArray DocumentSymbolsJson()
  {
    JsonObject symbol = new()
    {
      ["name"] = "Foo",
      ["range"] = RangeJson(0, 5, 8),
      ["selectionRange"] = RangeJson(0, 5, 8),
    };
    return new JsonArray(symbol);
  }

  private static JsonObject WorkspaceEditJson(string uri)
    => new()
    {
      ["changes"] = new JsonObject
      {
        [uri] = new JsonArray(new JsonObject { ["range"] = RangeJson(0, 5, 8), ["newText"] = "Bar" }),
      },
    };

  private static JsonObject FileRenameWorkspaceEditJson(string uri)
    => new()
    {
      ["documentChanges"] = new JsonArray(
          new JsonObject
          {
            ["kind"] = "rename",
            ["oldUri"] = uri,
            ["newUri"] = uri.Replace("A.cs", "B.cs", StringComparison.Ordinal),
          },
          new JsonObject
          {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 1 },
            ["edits"] = new JsonArray(new JsonObject { ["range"] = RangeJson(0, 5, 8), ["newText"] = "Bar" }),
          }),
    };

  private static JsonObject RangeJson(int line, int start, int end)
    => new()
    {
      ["start"] = new JsonObject { ["line"] = line, ["character"] = start },
      ["end"] = new JsonObject { ["line"] = line, ["character"] = end },
    };

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
