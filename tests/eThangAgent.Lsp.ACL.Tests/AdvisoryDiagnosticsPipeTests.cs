using eThangAgent.Lsp.ACL;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #356: advisory diagnostics over the daemon pipe. The daemon answers
// lsp.diagnostics from its attached diagnostics service; the client maps state
// and diagnostics back; a null service yields Unavailable, never an exception.
public class AdvisoryDiagnosticsPipeTests
{
  [Fact]
  public async Task DiagnosticsQuery_ReturnsScopedDiagnosticsOverPipe()
  {
    string ws = "ws-advpipe";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    LspDiagnosticStore store = new();
    string uri = LspUriMapper.ToUri("C:\\ws\\a.cs");
    store.OnPublishDiagnostics("{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":1,\"character\":0},\"end\":{\"line\":1,\"character\":5}},\"severity\":1,\"code\":\"CS0029\",\"message\":\"Cannot implicitly convert type\"}]}");
    LspDiagnosticsService service = new(null, store);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachDiagnosticsService(service);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    AdvisoryDiagnosticsResult result = await client.GetDiagnosticsAsync("C:\\ws\\a.cs", cts.Token);
    Assert.Equal(AdvisoryDiagnosticsState.Ready, result.State);
    AdvisoryDiagnostic diag = Assert.Single(result.Diagnostics);
    Assert.Equal(2, diag.StartLine);
    Assert.Equal("CS0029", diag.Code);
    Assert.Contains("Cannot implicitly convert", diag.Message);
    client.Dispose();
    cts.Cancel();
    try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
  }

  [Fact]
  public async Task DiagnosticsQuery_NoService_ReturnsUnavailable()
  {
    string ws = "ws-advpipe-none";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    AdvisoryDiagnosticsResult result = await client.GetDiagnosticsAsync("C:\\ws\\a.cs", cts.Token);
    Assert.Equal(AdvisoryDiagnosticsState.Unavailable, result.State);
    Assert.Empty(result.Diagnostics);
    client.Dispose();
    cts.Cancel();
    try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
  }

  [Fact]
  public async Task DiagnosticsQuery_ColdServer_ReturnsStarting()
  {
    string ws = "ws-advpipe-cold";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    LspDiagnosticsService service = new(null, new LspDiagnosticStore());
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    server.AttachDiagnosticsService(service);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    AdvisoryDiagnosticsResult result = await client.GetDiagnosticsAsync("C:\\ws\\a.cs", cts.Token);
    Assert.Equal(AdvisoryDiagnosticsState.Unavailable, result.State);
    Assert.Empty(result.Diagnostics);
    client.Dispose();
    cts.Cancel();
    try { await serve; } catch (OperationCanceledException) { Assert.True(true); }
  }

  [Fact]
  public async Task AdvisoryAccess_NullClient_ReturnsUnavailable()
  {
    LspAdvisoryDiagnosticsAccess access = new(() => null, "ws-1", "app-1");
    AdvisoryDiagnosticsResult result = await access.GetAsync("C:\\ws\\a.cs", TestContext.Current.CancellationToken);
    Assert.Equal(AdvisoryDiagnosticsState.Unavailable, result.State);
    Assert.Empty(result.Diagnostics);
  }
}
