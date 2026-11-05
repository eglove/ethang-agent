using System.Text;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #354 production wiring (review step 5 denials): transport sink,
// notification routing, pull execution, union merge, per-doc result-id,
// Degraded/Unavailable production, restart query gate.
public class LspProductionWiringTests
{
  private static string PushJson(string uri, string code, int sl, int sc, int el, int ec, string message)
    => "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":" + sl + ",\"character\":" + sc + "},\"end\":{\"line\":" + el + ",\"character\":" + ec + "}},\"severity\":1,\"code\":\"" + code + "\",\"message\":\"" + message + "\"}]}";

  [Fact]
  public void DiagnosticsStore_UnionMerge_KeepsDistinctPushAndPull()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/Bad.cs";
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "push error"));
    store.OnPullDiagnostics(uri, PushJson(uri, "CS1002", 1, 0, 1, 5, "pull error"));
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Equal(2, result.Diagnostics.Count);
    Assert.Contains(result.Diagnostics, d => d.Code == "CS0029");
    Assert.Contains(result.Diagnostics, d => d.Code == "CS1002");
  }

  [Fact]
  public void DiagnosticsStore_ResultId_IsPerDocument()
  {
    LspDiagnosticStore store = new();
    string uriA = "file:///C:/ws/A.cs";
    string uriB = "file:///C:/ws/B.cs";
    string withIdA = "{\"uri\":\"" + uriA + "\",\"resultId\":\"id-A\",\"diagnostics\":[]}";
    string withIdB = "{\"uri\":\"" + uriB + "\",\"resultId\":\"id-B\",\"diagnostics\":[]}";
    store.OnPullDiagnostics(uriA, withIdA);
    store.OnPullDiagnostics(uriB, withIdB);
    Assert.Equal("id-A", store.GetResultId(uriA));
    Assert.Equal("id-B", store.GetResultId(uriB));
    string paramsA = LspDiagnosticStore.BuildPullParams(uriA);
    Assert.Contains(uriA, paramsA, StringComparison.Ordinal);
    Assert.DoesNotContain(uriB, paramsA, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TransportDocumentSync_DidOpenChangeClose_SendsNotifications()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspTransportDocumentSync sink = new(transport);
    await sink.DidOpenAsync("file:///C:/ws/a.cs", "csharp", 1, "class A {}");
    await sink.DidChangeAsync("file:///C:/ws/a.cs", 2, "class A { int x; }");
    await sink.DidCloseAsync("file:///C:/ws/a.cs");
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("textDocument/didOpen", written);
    Assert.Contains("textDocument/didChange", written);
    Assert.Contains("textDocument/didClose", written);
    Assert.Contains("class A {}", written);
  }

  [Fact]
  public void NotificationRouter_PublishDiagnostics_RoutesToStore()
  {
    LspDiagnosticStore store = new();
    LspNotificationRouter router = new(store);
    string uri = "file:///C:/ws/Bad.cs";
    string notif = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":" + PushJson(uri, "CS0029", 0, 38, 0, 52, "err") + "}";
    router.HandleNotification(notif);
    LspDiagnosticsResult result = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Single(result.Diagnostics);
  }

  [Fact]
  public async Task DiagnosticsService_PullExecutesRequest_AndMerges()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/Bad.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + PushJson(uri, "CS0029", 0, 38, 0, 52, "err") + "}");
    LspDiagnosticStore store = new();
    LspCapabilities caps = new(false, false, false, false, false, false, false, DiagnosticPullProvider: true, RenameProvider: false);
    LspDiagnosticsService service = new(transport, store, caps);
    LspDiagnosticsResult result = await service.PullAsync(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Single(result.Diagnostics);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("textDocument/diagnostic", written);
  }

  [Fact]
  public async Task DiagnosticsService_PullFailureWithPush_ReturnsDegraded()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/Bad.cs";
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "push err"));
    LspCapabilities caps = new(false, false, false, false, false, false, false, DiagnosticPullProvider: true, RenameProvider: false);
    LspDiagnosticsService service = new(transport, store, caps);
    LspDiagnosticsResult result = await service.PullAsync(uri);
    Assert.Equal(LspDiagnosticsState.Degraded, result.State);
    Assert.Single(result.Diagnostics);
  }

  [Fact]
  public async Task DiagnosticsService_NoServer_ReturnsUnavailable()
  {
    LspDiagnosticsService service = new(null, new LspDiagnosticStore());
    LspDiagnosticsResult result = await service.PullAsync("file:///C:/ws/a.cs");
    Assert.Equal(LspDiagnosticsState.Unavailable, result.State);
    Assert.Empty(result.Diagnostics);
  }

  [Fact]
  public async Task DiagnosticsService_QueryGate_BlocksUntilReopenCompletes()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/a.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + PushJson(uri, "CS0029", 0, 38, 0, 52, "err") + "}");
    LspDiagnosticStore store = new();
    LspCapabilities caps = new(false, false, false, false, false, false, false, DiagnosticPullProvider: true, RenameProvider: false);
    LspDiagnosticsService service = new(transport, store, caps);
    service.BeginRestart();
    Task<LspDiagnosticsResult> query = service.PullAsync(uri);
    Assert.False(query.IsCompleted);
    await service.CompleteRestartAsync();
    LspDiagnosticsResult result = await query;
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
  }

  [Fact]
  public void DiagnosticsStore_ReadyEmpty_OnlyWhenServerReported()
  {
    LspDiagnosticStore store = new();
    string uri = "file:///C:/ws/clean.cs";
    LspDiagnosticsResult before = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Starting, before.State);
    store.OnPublishDiagnostics("{\"uri\":\"" + uri + "\",\"diagnostics\":[]}");
    LspDiagnosticsResult after = store.Get(uri);
    Assert.Equal(LspDiagnosticsState.Ready, after.State);
    Assert.Empty(after.Diagnostics);
  }
}
