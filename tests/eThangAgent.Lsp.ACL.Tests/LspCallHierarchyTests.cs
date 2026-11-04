using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

public class LspCallHierarchyTests
{
  private static string DocSym()
  {
    JsonObject sym = new() { ["name"] = "Foo", ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 5 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 8 } }, ["selectionRange"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 5 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 8 } } };
    return JsonSerializer.Serialize(new[] { sym });
  }

  private static string Prep(string uri)
  {
    JsonObject item = new() { ["name"] = "Foo", ["kind"] = 6, ["uri"] = uri, ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 5 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 8 } }, ["selectionRange"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 5 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 8 } } };
    return JsonSerializer.Serialize(new[] { item });
  }

  private static string Incoming(string uri, int line, int ch, string name)
  {
    JsonObject from = new() { ["name"] = name, ["kind"] = 6, ["uri"] = uri, ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = line, ["character"] = ch }, ["end"] = new JsonObject { ["line"] = line, ["character"] = ch + name.Length } }, ["selectionRange"] = new JsonObject { ["start"] = new JsonObject { ["line"] = line, ["character"] = ch }, ["end"] = new JsonObject { ["line"] = line, ["character"] = ch + name.Length } } };
    JsonObject item = new() { ["from"] = from };
    return JsonSerializer.Serialize(new[] { item });
  }

  private static string Outgoing(string uri, int line, int ch, string name)
  {
    JsonObject to = new() { ["name"] = name, ["kind"] = 6, ["uri"] = uri, ["range"] = RangeJson(line, ch, ch + name.Length), ["selectionRange"] = RangeJson(line, ch, ch + name.Length) };
    JsonObject item = new() { ["to"] = to };
    return JsonSerializer.Serialize(new[] { item });
  }

  private static JsonObject RangeJson(int line, int ch, int endCh)
    => new() { ["start"] = new JsonObject { ["line"] = line, ["character"] = ch }, ["end"] = new JsonObject { ["line"] = line, ["character"] = endCh } };

  private static string Resp(int id, string resultJson) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = JsonNode.Parse(resultJson) }.ToJsonString();

  [Fact]
  public async Task CallHierarchyIncoming_ReturnsCallers_WithFileAndLine()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest(Resp(1, DocSym()));
    transport.EnqueueIncomingForTest(Resp(2, Prep(uri)));
    transport.EnqueueIncomingForTest(Resp(3, Incoming(uri, 10, 2, "Caller")));
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, true, true));
    LspSymbolResult result = await service.CallHierarchyIncomingAsync(uri, "Foo", 0, 1);
    Assert.Equal(LspSymbolState.Ready, result.State);
    LspSymbolSite site = Assert.Single(result.Sites);
    Assert.Equal(10, site.Line);
    Assert.Equal(2, site.Character);
    Assert.Equal("Caller", site.SymbolName);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("callHierarchy/prepare", written);
    Assert.Contains("callHierarchy/incoming", written);
  }

  [Fact]
  public async Task CallHierarchyOutgoing_ReturnsCallees()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest(Resp(1, DocSym()));
    transport.EnqueueIncomingForTest(Resp(2, Prep(uri)));
    transport.EnqueueIncomingForTest(Resp(3, Outgoing(uri, 20, 1, "Callee")));
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, true, true));
    LspSymbolResult result = await service.CallHierarchyOutgoingAsync(uri, "Foo", 0, 1);
    Assert.Equal(LspSymbolState.Ready, result.State);
    LspSymbolSite site = Assert.Single(result.Sites);
    Assert.Equal("Callee", site.SymbolName);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("callHierarchy/outgoing", written);
  }

  [Fact]
  public async Task CallHierarchy_WithoutCapability_ReturnsUnsupported_WithoutWireCall()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspSymbolService service = new(transport, LspCapabilities.None);
    LspSymbolResult result = await service.CallHierarchyIncomingAsync("file:///C:/ws/A.cs", "Foo", 0, 1);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.DoesNotContain("callHierarchy/prepare", written);
  }

  [Fact]
  public async Task CallHierarchy_DepthTwo_ExpandsSecondLevel()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest(Resp(1, DocSym()));
    transport.EnqueueIncomingForTest(Resp(2, Prep(uri)));
    transport.EnqueueIncomingForTest(Resp(3, Incoming(uri, 10, 2, "Caller")));
    transport.EnqueueIncomingForTest(Resp(4, Incoming(uri, 30, 0, "GrandCaller")));
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, true, true));
    LspSymbolResult result = await service.CallHierarchyIncomingAsync(uri, "Foo", 0, 2);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Equal(2, result.Sites.Count);
  }

  [Fact]
  public async Task CallHierarchy_DepthInvalid_ReturnsUnsupported()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, true, true));
    LspSymbolResult result = await service.CallHierarchyIncomingAsync("file:///C:/ws/A.cs", "Foo", 0, 3);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
  }
}
