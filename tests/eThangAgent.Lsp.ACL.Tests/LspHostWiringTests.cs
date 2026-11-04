using System.Text;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspHostWiringTests
{
  private static string PushJson(string uri, string code, int sl, int sc, int el, int ec, string message)
    => "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":" + sl + ",\"character\":" + sc + "},\"end\":{\"line\":" + el + ",\"character\":" + ec + "}},\"severity\":1,\"code\":\"" + code + "\",\"message\":\"" + message + "\"}]}";

  [Fact]
  public async Task DiagnosticsService_PushOnlyCapability_SuppressesPull_ButReturnsPush()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/Bad.cs";
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(PushJson(uri, "CS0029", 0, 38, 0, 52, "push err"));
    LspDiagnosticsService service = new(transport, store, LspCapabilities.None);
    LspDiagnosticsResult result = await service.PullAsync(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Single(result.Diagnostics);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.DoesNotContain("textDocument/diagnostic", written);
  }

  [Fact]
  public async Task DiagnosticsService_PullCapability_AllowsPullRequest()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/Bad.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + PushJson(uri, "CS0029", 0, 38, 0, 52, "err") + "}");
    LspDiagnosticStore store = new();
    LspCapabilities caps = new(false, false, false, false, false, false, false, DiagnosticPullProvider: true);
    LspDiagnosticsService service = new(transport, store, caps);
    LspDiagnosticsResult result = await service.PullAsync(uri);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("textDocument/diagnostic", written);
  }

  [Fact]
  public async Task DiagnosticsService_QueryGate_BlocksUntilRestartReopenCompletes()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/a.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + PushJson(uri, "CS0029", 0, 38, 0, 52, "err") + "}");
    LspDiagnosticStore store = new();
    LspCapabilities caps = new(false, false, false, false, false, false, false, DiagnosticPullProvider: true);
    LspDiagnosticsService service = new(transport, store, caps);
    service.BeginRestart();
    Task<LspDiagnosticsResult> query = service.PullAsync(uri);
    Assert.False(query.IsCompleted);
    await service.CompleteRestartAsync();
    LspDiagnosticsResult result = await query;
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
  }

  [Fact]
  public void Capabilities_PushOnlyServer_ReportsNoPull()
  {
    LspCapabilities caps = LspCapabilities.None;
    Assert.False(caps.Supports("textDocument/diagnostic"));
  }
}
