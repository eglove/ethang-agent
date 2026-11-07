using System.Text;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.FileClaims;
using eThangAgent.ToolDomain.Lsp;

// JSON002 fires only in the format/IDE host; the pragma pair is the repo's named pattern.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

// The production legs the review found unwired: the watcher publishes over the pipe,
// the lsp tool reaches the daemon, sibling sessions share one claim table, and a query
// behind the applied generation is labeled Stale.
public class LspProductionPathTests
{
  private sealed class FakeWatcher : IFileWatcher
  {
    private readonly List<Func<FileChangeAnnouncement, Task>> _handlers = [];

    public long Generation { get; private set; }

    public int SubscriberCount
    {
      get { lock (_handlers) { return _handlers.Count; } }
    }

    public IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler)
    {
      lock (_handlers) { _handlers.Add(handler); }
      return new Unsubscriber(this, handler);
    }

    public async Task RaiseAsync(FileChangeAnnouncement announcement)
    {
      Generation = announcement.Generation;
      Func<FileChangeAnnouncement, Task>[] handlers;
      lock (_handlers) { handlers = [.. _handlers]; }
      foreach (Func<FileChangeAnnouncement, Task> handler in handlers)
      {
        await handler(announcement).ConfigureAwait(false);
      }
    }

    private sealed class Unsubscriber(FakeWatcher owner, Func<FileChangeAnnouncement, Task> handler) : IDisposable
    {
      public void Dispose()
      {
        lock (owner._handlers) { _ = owner._handlers.Remove(handler); }
      }
    }
  }

  private sealed class RecordingSync : ILspDocumentSync
  {
    public List<string> Calls { get; } = [];

    public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("open:" + uri + ":" + text);
      return Task.CompletedTask;
    }

    public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("change:" + uri + ":" + text);
      return Task.CompletedTask;
    }

    public Task DidCloseAsync(string uri, CancellationToken ct = default)
    {
      Calls.Add("close:" + uri);
      return Task.CompletedTask;
    }
  }

  [Fact]
  public async Task WatcherForwarder_AnnouncementReachesDaemonOverPipe_AsDidOpen()
  {
    string root = TestTempRoot.CreateDirectory("lspforwarder").FullName;
    string ws = "ws-forwarder-" + Guid.NewGuid().ToString("N");
    try
    {
      File.WriteAllText(Path.Combine(root, "a.cs"), "class A {}");
      string pipe = LspPipeNames.ForWorkspaceId(ws);
      RecordingSync sink = new();
      LspDocumentSync daemonForwarder = new(ws, root, "csharp", [".cs"], sink);
      using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
      server.AttachForwarder(daemonForwarder);
      using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
      Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
      await Task.Delay(150, cts.Token);

      LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
      FakeWatcher watcher = new();
      // The forwarder holds the client, so it is disposed FIRST (below) and the client
      // after it; the ordering is the whole point of not using declarations here.
#pragma warning disable CA2000, CA2025 // dispose order is explicit: forwarder, then client
      LspWatcherForwarder forwarder = new(watcher, ws, _ => Task.FromResult<LspPipeClient?>(client));
#pragma warning restore CA2000, CA2025
      forwarder.Start();
      Assert.True(forwarder.IsForwarding);
      Assert.Equal(1, watcher.SubscriberCount);

      await watcher.RaiseAsync(new FileChangeAnnouncement(["a.cs"], [], 1, DateTimeOffset.UtcNow));
      await Task.Delay(300, cts.Token);

      string call = Assert.Single(sink.Calls);
      Assert.StartsWith("open:", call);
      Assert.Contains("class A {}", call);
      Assert.Equal(1, daemonForwarder.LastAppliedGeneration);

      forwarder.Dispose();
      client.Dispose();
      await cts.CancelAsync();
      try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SymbolAccess_ReachesDaemonOverPipe_AndReturnsDefinition()
  {
    string ws = "ws-symbol-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    string uri = LspUriMapper.ToUri("C:\\ws\\a.cs");
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[{\"name\":\"Foo\",\"kind\":12,\"range\":{\"start\":{\"line\":2,\"character\":2},\"end\":{\"line\":2,\"character\":9}},\"selectionRange\":{\"start\":{\"line\":2,\"character\":2},\"end\":{\"line\":2,\"character\":5}}}]}");
    transport.EnqueueIncomingForTest(
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":[{\"uri\":\"" + uri + "\",\"range\":{\"start\":{\"line\":2,\"character\":2},\"end\":{\"line\":2,\"character\":5}}}]}");
    LspCapabilities caps = new(false, true, false, true, false, false, false, false, false);
    LspSymbolService service = new(transport, caps);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachSymbolService(service);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
    await Task.Delay(150, cts.Token);

    LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    LspDaemonSymbolAccess access = new(_ => Task.FromResult<LspPipeClient?>(client), _ => true);
    LspSymbolOutcome outcome = await access.ExecuteAsync(new LspSymbolCommand.Definition("C:\\ws\\a.cs", "Foo", 0), cts.Token);

    LspSymbolOutcome.Ready ready = Assert.IsType<LspSymbolOutcome.Ready>(outcome);
    ToolDomain.Lsp.LspSymbolSite site = Assert.Single(ready.Sites);
    Assert.Equal(2, site.Line);
    Assert.Equal("Foo", site.SymbolName);

    client.Dispose();
    await cts.CancelAsync();
    try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
  }

  [Fact]
  public async Task SymbolAccess_NoDaemon_ReturnsUnavailable()
  {
    LspDaemonSymbolAccess access = new(_ => Task.FromResult<LspPipeClient?>(null), _ => true);
    LspSymbolOutcome outcome = await access.ExecuteAsync(
        new LspSymbolCommand.Definition("C:\\ws\\a.cs", "Foo", 0), TestContext.Current.CancellationToken);
    LspSymbolOutcome.Unavailable unavailable = Assert.IsType<LspSymbolOutcome.Unavailable>(outcome);
    Assert.Contains("no language server", unavailable.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PipeClaimGate_TwoSessions_ShareOneDaemonTable()
  {
    string ws = "ws-claimgate-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
    await Task.Delay(150, cts.Token);

    LspPipeClient first = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    LspPipeClient second = await LspPipeClient.ConnectAsync(pipe, ws, "app-2", cts.Token);
    LspPipeClaimGate holder = new(_ => Task.FromResult<LspPipeClient?>(first), "session-1");
    LspPipeClaimGate rival = new(_ => Task.FromResult<LspPipeClient?>(second), "session-2");

    Assert.Null(await holder.CheckBeforeWriteAsync("C:\\ws\\a.cs", cts.Token));
    string? refused = await rival.CheckBeforeWriteAsync("C:\\ws\\a.cs", cts.Token);
    Assert.NotNull(refused);
    Assert.Contains("Error [FileClaimHeld]", refused, StringComparison.Ordinal);
    Assert.Contains("session-1", refused, StringComparison.Ordinal);

    first.Dispose();
    second.Dispose();
    await cts.CancelAsync();
    try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
  }

  [Fact]
  public async Task PipeClaimGate_NoDaemon_GrantsTheWrite()
  {
    LspPipeClaimGate gate = new(_ => Task.FromResult<LspPipeClient?>(null), "session-1");
    Assert.Null(await gate.CheckBeforeWriteAsync("C:\\ws\\a.cs", TestContext.Current.CancellationToken));
  }

  /// <summary>An edit-time query is not watcher-driven: the file's own editor has no
  ///     knowledge of the watcher generation, so a watcher announcement must not turn the
  ///     next edit's diagnostics into a permanent "outdated" label (issue #365 defect 1).</summary>
  [Fact]
  public async Task Diagnostics_QueryAfterWatcherAnnouncement_StillReportsRealDiagnostics()
  {
    string ws = "ws-announced-" + Guid.NewGuid().ToString("N");
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    string root = TestTempRoot.CreateDirectory("lsp-announced").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
      string uri = LspUriMapper.ToUri(Path.Combine(root, "a.cs"));
      LspDiagnosticStore store = new();
      store.OnPublishDiagnostics(
          "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":5}},\"severity\":1,\"code\":\"CS1002\",\"message\":\"missing semicolon\"}]}");
      LspDocumentSync forwarder = new(ws, root, "csharp", [".cs"], new RecordingSync());
      using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
      server.AttachForwarder(forwarder);
      server.AttachDiagnosticsService(new LspDiagnosticsService(null, store));
      using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
      Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token), cts.Token);
      await Task.Delay(150, cts.Token);

      using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
      await client.SendWatcherAnnouncementAsync(
          new WatcherAnnouncement(ws, ["a.cs"], [], 7, DateTimeOffset.UtcNow), cts.Token);
      await Task.Delay(300, cts.Token);

      AdvisoryDiagnosticsResult result = await client.GetDiagnosticsAsync(Path.Combine(root, "a.cs"), cts.Token);
      Assert.Equal(AdvisoryDiagnosticsState.Ready, result.State);
      Assert.Equal("CS1002", Assert.Single(result.Diagnostics).Code);

      await cts.CancelAsync();
      try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SymbolAccess_MissingFile_FailsTyped_WithoutReachingTheDaemon()
  {
    bool connected = false;
    LspDaemonSymbolAccess access = new(_ =>
    {
      connected = true;
      return Task.FromResult<LspPipeClient?>(null);
    });
    string missing = Path.Combine(TestTempRoot.Root, "no-such-" + Guid.NewGuid().ToString("N") + ".cs");
    LspSymbolOutcome outcome = await access.ExecuteAsync(
        new LspSymbolCommand.DocumentSymbols(missing), TestContext.Current.CancellationToken);
    LspSymbolOutcome.Unsupported unsupported = Assert.IsType<LspSymbolOutcome.Unsupported>(outcome);
    Assert.Contains("does not exist", unsupported.Message, StringComparison.Ordinal);
    Assert.False(connected, "a missing file must not start or reach a language server");
  }
}

#pragma warning restore JSON002
