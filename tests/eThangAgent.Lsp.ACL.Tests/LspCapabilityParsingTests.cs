using eThangAgent.Lsp.ACL;

// JSON002 fires only in the format/IDE host; the pragma pair is the repo's named pattern.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #365 defect 3: the capability record is built from the initialize result, and an
// argument left off that construction silently defaults to false - which is how pull
// diagnostics stayed unreachable while the server advertised them.
public class LspCapabilityParsingTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Initialize_AdvertisedDiagnosticProvider_EnablesPull()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"capabilities\":{\"diagnosticProvider\":{\"interFileDependencies\":true,\"workspaceDiagnostics\":false}}}}");
    LspClient client = new(transport, new LspServerConfig("csharp", "srv", [], [".cs"]));
    _ = await client.InitializeAsync("file:///ws", Ct);
    Assert.True(client.Capabilities.DiagnosticPullProvider);
    Assert.True(client.Capabilities.Supports("textDocument/diagnostic"));
  }

  [Fact]
  public async Task Initialize_NoDiagnosticProvider_LeavesPullUnavailable()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"capabilities\":{\"hoverProvider\":true}}}");
    LspClient client = new(transport, new LspServerConfig("csharp", "srv", [], [".cs"]));
    _ = await client.InitializeAsync("file:///ws", Ct);
    Assert.False(client.Capabilities.DiagnosticPullProvider);
  }

  [Fact]
  public async Task Initialize_AdvertisedRenameProvider_EnablesRename()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"capabilities\":{\"renameProvider\":true}}}");
    LspClient client = new(transport, new LspServerConfig("csharp", "srv", [], [".cs"]));
    _ = await client.InitializeAsync("file:///ws", Ct);
    Assert.True(client.Capabilities.RenameProvider);
    Assert.True(client.Capabilities.Supports("textDocument/rename"));
  }

  [Fact]
  public async Task Initialize_NoRenameProvider_LeavesRenameUnsupported()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"capabilities\":{\"hoverProvider\":true}}}");
    LspClient client = new(transport, new LspServerConfig("csharp", "srv", [], [".cs"]));
    _ = await client.InitializeAsync("file:///ws", Ct);
    Assert.False(client.Capabilities.RenameProvider);
    Assert.False(client.Capabilities.Supports("textDocument/rename"));
  }

  [Fact]
  public void Supports_UnknownMethod_IsNotAssumed() =>
    Assert.False(LspCapabilities.None.Supports("textDocument/someFutureThing"));
  [Fact]
  public async Task DiagnosticsService_DynamicallyRegisteredPull_IsAttempted()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"client/registerCapability\",\"params\":{\"registrations\":[{\"id\":\"a\",\"method\":\"textDocument/diagnostic\",\"registerOptions\":{}}]}}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":4,\"result\":{\"kind\":\"full\",\"items\":[]}}");
    _ = await transport.ReadResponseAsync(Ct);
    LspDiagnosticsService service = new(transport, new LspDiagnosticStore(), LspCapabilities.None);
    _ = await service.PullAsync("file:///C:/ws/a.cs", Ct);
    Assert.Contains("textDocument/diagnostic", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
  }
}

#pragma warning restore JSON002
