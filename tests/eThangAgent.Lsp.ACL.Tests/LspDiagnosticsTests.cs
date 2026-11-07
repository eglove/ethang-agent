using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

// JSON002 fires only in the format/IDE host; the pragma pair is the repo's named pattern.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #354: document sync and pull diagnostics. Push (publishDiagnostics)
// is primary for C# per #355 capability matrix (OmniSharp is push-only);
// pull (textDocument/diagnostic + previous-result-id) is secondary.
[Trait("Category", "Process")]
public class LspDiagnosticsTests
{
  private static string NewTree() => TestTempRoot.CreateDirectory("lspdiag").FullName;

  private static void Write(string root, string relative, string content)
  {
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  private sealed class FakeSync : ILspDocumentSync
  {
    public List<string> Calls { get; } = [];
    public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("open:" + uri + ":" + version + ":" + text);
      return Task.CompletedTask;
    }
    public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("change:" + uri + ":" + version + ":" + text);
      return Task.CompletedTask;
    }
    public Task DidCloseAsync(string uri, CancellationToken ct = default)
    {
      Calls.Add("close:" + uri);
      return Task.CompletedTask;
    }
  }

  private static string PushJson(string uri, string code, int sl, int sc, int el, int ec, string message)
    => "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":" + sl + ",\"character\":" + sc + "},\"end\":{\"line\":" + el + ",\"character\":" + ec + "}},\"severity\":1,\"code\":\"" + code + "\",\"message\":\"" + message + "\"}]}";

  [Fact]
  public void DiagnosticsState_EmptyIsNeverClean()
  {
    LspDiagnosticsResult ready = new(LspDiagnosticsState.Ready, []);
    Assert.Equal(LspDiagnosticsState.Ready, ready.State);
    Assert.Empty(ready.Diagnostics);
    LspDiagnosticsResult starting = new(LspDiagnosticsState.Starting, []);
    Assert.NotEqual(LspDiagnosticsState.Ready, starting.State);
    LspDiagnosticsResult degraded = new(LspDiagnosticsState.Degraded, []);
    Assert.NotEqual(LspDiagnosticsState.Ready, degraded.State);
    LspDiagnosticsResult unavailable = new(LspDiagnosticsState.Unavailable, []);
    Assert.NotEqual(LspDiagnosticsState.Ready, unavailable.State);
  }

  [Fact]
  public void DiagnosticsStore_PushOnlyServer_ReturnsViaNotification()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type"));
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    LspDiagnostic diag = Assert.Single(result.Diagnostics);
    Assert.Equal("CS0029", diag.Code);
    Assert.Equal(0, diag.StartLine);
    Assert.Equal(38, diag.StartCharacter);
    Assert.Equal(0, diag.EndLine);
    Assert.Equal(52, diag.EndCharacter);
    Assert.Contains("Cannot implicitly convert", diag.Message);
  }

  [Fact]
  public void DiagnosticsStore_PullAndPush_DedupByIdentity()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type"));
    store.OnPullDiagnostics(uri, PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type"));
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Single(result.Diagnostics);
  }

  [Fact]
  public void DiagnosticsStore_DistinctDiagnostics_UnionMergeKeepsBoth()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type"));
    store.OnPullDiagnostics(uri, PushJson(uri, "CS1002", 1, 0, 1, 5, "; expected"));
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(2, result.Diagnostics.Count);
    Assert.Contains(result.Diagnostics, d => d.Code == "CS0029");
    Assert.Contains(result.Diagnostics, d => d.Code == "CS1002");
  }

  [Fact]
  public void DiagnosticsStore_ColdServer_ReportsStarting()
  {
    LspDiagnosticStore store = new();
    LspDiagnosticsResult result = store.Get("file:///C:/ws/Bad.cs");
    Assert.Equal(LspDiagnosticsState.Starting, result.State);
    Assert.Empty(result.Diagnostics);
  }

  [Fact]
  public void DiagnosticsStore_PullEmptyResult_KeepsPushDiagnostics()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type"));
    store.OnPullDiagnostics(uri, "{\"uri\":\"" + uri + "\",\"diagnostics\":[]}");
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    LspDiagnostic only = Assert.Single(result.Diagnostics);
    Assert.Equal("CS0029", only.Code);
  }

  /// <summary>A pull response carries its diagnostics under "items", not
  ///     "diagnostics" - the shape a push notification uses. Reading only the push name
  ///     silently drops every pulled diagnostic (issue #365 defect 3).</summary>
  [Fact]
  public void DiagnosticsStore_PullResponse_ParsesItems()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    string pulled = "{\"kind\":\"full\",\"resultId\":\"DocumentPullDiagnosticsHandler(category: ):1\",\"items\":[{\"range\":{\"start\":{\"line\":2,\"character\":31},\"end\":{\"line\":2,\"character\":43}},\"severity\":1,\"code\":\"CS0029\",\"message\":\"Cannot implicitly convert type 'string' to 'int'\"}]}";
    store.OnPullDiagnostics(uri, pulled);
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    LspDiagnostic only = Assert.Single(result.Diagnostics);
    Assert.Equal("CS0029", only.Code);
    Assert.Equal(2, only.StartLine);
  }

  /// <summary>The server's result id names the diagnostic KIND, not the document content:
  ///     echoing it back as previousResultId makes the server answer "unchanged" for a file
  ///     that has since changed, and the answer then carries no items at all.</summary>
  [Fact]
  public void DiagnosticsStore_PullParams_OmitPreviousResultId()
  {
    string uri = "file:///C:/ws/Bad.cs";
    string pullParams = LspDiagnosticStore.BuildPullParams(uri);
    Assert.Contains(uri, pullParams, StringComparison.Ordinal);
    Assert.DoesNotContain("previousResultId", pullParams, StringComparison.Ordinal);
  }

  [Fact]
  public void DiagnosticsStore_PushEmpty_ClearsFile()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type"));
    store.OnPublishDiagnostics("{\"uri\":\"" + uri + "\",\"diagnostics\":[]}");
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Empty(result.Diagnostics);
  }

  [Fact]
  public async Task DiagnosticsSync_WatcherChange_ReflectedBeforeNextQuery()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v1");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Write(root, "a.cs", "v2");
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 2, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Assert.Equal(2, sync.Calls.Count);
      Assert.StartsWith("change:", sync.Calls[1]);
      Assert.Contains("v2", sync.Calls[1]);
      Assert.Equal(2, forwarder.LastAppliedGeneration);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task DiagnosticsSync_Restart_ReopensBeforeQuery()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v1");
      FakeSync first = new();
      LspDocumentSync before = new("ws-1", root, "csharp", [".cs"], first);
      await before.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Write(root, "a.cs", "v2-after-restart");
      FakeSync second = new();
      LspDocumentSync after = new("ws-1", root, "csharp", [".cs"], second);
      await after.ReopenAllAsync(before.OpenDocuments, TestContext.Current.CancellationToken);
      string reopened = Assert.Single(second.Calls);
      Assert.StartsWith("open:", reopened);
      Assert.Contains("v2-after-restart", reopened);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task DiagnosticsSync_PipeOnly_DidChangeWithoutPubSub()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v1");
      string ws = "ws-diagpipe";
      string pipe = LspPipeNames.ForWorkspaceId(ws);
      FakeSync sync = new();
      LspDocumentSync forwarder = new(ws, root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement(ws, ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Write(root, "a.cs", "v2-pipe");
      using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
      server.AttachForwarder(forwarder);
      using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
      Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
      await Task.Delay(100, cts.Token);
      using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
      await client.SendWatcherAnnouncementAsync(new WatcherAnnouncement(ws, ["a.cs"], [], 2, DateTimeOffset.UtcNow), cts.Token);
      await Task.Delay(500, cts.Token);
      Assert.Equal(2, sync.Calls.Count);
      Assert.StartsWith("change:", sync.Calls[1]);
      Assert.Contains("v2-pipe", sync.Calls[1]);
      client.Dispose();
      cts.Cancel();
      try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Transport_PublishDiagnosticsNotification_IsCaptured()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/Bad.cs";
    string notif = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":" + PushJson(uri, "CS0029", 0, 38, 0, 52, "Cannot implicitly convert type") + "}";
    transport.EnqueueIncomingForTest(notif);
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}");
    LspDiagnosticStore store = new();
    string? response = await transport.ReadResponseAsync(TestContext.Current.CancellationToken);
    Assert.Contains("\"ok\":true", response);
    Assert.Empty(store.Get(uri).Diagnostics);
  }
}

#pragma warning restore JSON002
