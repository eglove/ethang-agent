using System.Text;
using System.Text.Json;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspClientTests
{
  [Fact]
  public async Task Framing_RoundTrip_EncodesContentLengthAndDecodesResponse()
  {
    using MemoryStream output = new();
    using MemoryStream input = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";
    byte[] body = Encoding.UTF8.GetBytes(payload);
    await transport.WriteMessageAsync(payload, TestContext.Current.CancellationToken);
    string header = Encoding.ASCII.GetString(output.ToArray());
    Assert.StartsWith("Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), header);
    Assert.Contains("\r\n\r\n", header);
    byte[] framed = output.ToArray();
    using MemoryStream framedInput = new(framed);
    LspJsonRpcTransport reader = new(framedInput, new MemoryStream(), TimeSpan.FromSeconds(2));
    string? decoded = await reader.ReadMessageAsync(TestContext.Current.CancellationToken);
    Assert.Equal(payload, decoded);
  }

  [Fact]
  public async Task ServerRequest_Unimplemented_ReturnsMethodNotFoundAndContinues()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"workspace/configuration\",\"params\":{}}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\",\"params\":{}}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}");
    string? result = await transport.ReadResponseAsync(TestContext.Current.CancellationToken);
    Assert.Contains("\"ok\":true", result);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("-32601", written);
    Assert.Contains("99", written);
  }

  [Fact]
  public async Task NeverResponds_TimesOutWithLabeledResult()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromMilliseconds(50));
    LspRequestResult result = await transport.SendRequestAsync("initialize", "{}", TestContext.Current.CancellationToken);
    Assert.False(result.IsSuccess);
    Assert.Equal("Timeout", result.ErrorCode);
  }

  [Fact]
  public void Utf16Position_ConvertsLineCharacterAndBack()
  {
    string line = "a\uD83D\uDE00b";
    int utf16 = LspPositionConverter.ToUtf16Offset(line, 1);
    Assert.Equal(1, utf16);
    int utf16AfterEmoji = LspPositionConverter.ToUtf16Offset(line, 3);
    Assert.Equal(3, utf16AfterEmoji);
    int charOffset = LspPositionConverter.FromUtf16Offset(line, 3);
    Assert.Equal(3, charOffset);
    (int lineNo, int col) = LspPositionConverter.ToPosition(line, 3);
    Assert.Equal(0, lineNo);
    Assert.Equal(3, col);
  }

  [Fact]
  public void UriMapping_RoundTripsWindowsPath()
  {
    string path = "C:\\Users\\test\\file.cs";
    string uri = LspUriMapper.ToUri(path);
    Assert.StartsWith("file://", uri);
    Assert.Contains("file.cs", uri);
    string back = LspUriMapper.FromUri(uri);
    Assert.Equal(path, back);
  }

  [Fact]
  public void ServerConfig_ClaimsExtensions()
  {
    LspServerConfig config = new("csharp", "omnisharp", [], [".cs"]);
    Assert.True(config.ClaimsExtension(".cs"));
    Assert.False(config.ClaimsExtension(".py"));
  }

  [Fact]
  public async Task Initialize_Handshake_RecordsCapabilitiesAndConsultsBeforeRequest()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"capabilities\":{\"hoverProvider\":true}}}");
    LspClient client = new(transport, new LspServerConfig("csharp", "omnisharp", [], [".cs"]));
    LspRequestResult init = await client.InitializeAsync("file:///C:/ws", TestContext.Current.CancellationToken);
    Assert.True(init.IsSuccess);
    Assert.True(client.Capabilities.HoverProvider);
    Assert.False(client.Capabilities.DefinitionProvider);
    LspRequestResult hover = await client.SendRequestAsync("textDocument/definition", "{}", TestContext.Current.CancellationToken);
    Assert.False(hover.IsSuccess);
    Assert.Equal("Unsupported", hover.ErrorCode);
  }
}
