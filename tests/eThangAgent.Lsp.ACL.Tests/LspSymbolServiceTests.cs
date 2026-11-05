using System.Text;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Symbol navigation service tests (issue #357): document-symbol resolution,
/// capability gate, deterministic multi-occurrence, referenced-only unsupported.</summary>
public class LspSymbolServiceTests
{
  private static string LocationJson(string uri, int line, int ch)
    => "{\"uri\":\"" + uri + "\",\"range\":{\"start\":{\"line\":" + line + ",\"character\":" + ch + "},\"end\":{\"line\":" + line + ",\"character\":" + (ch + 3) + "}}}";

  private static string DocSymbolsJsonMulti(params (string Name, int Line, int Char)[] symbols)
  {
    StringBuilder sb = new();
    sb.Append('[');
    for (int i = 0; i < symbols.Length; i++)
    {
      if (i > 0) sb.Append(',');
      sb.Append("{\"name\":\"" + symbols[i].Name + "\",\"range\":{\"start\":{\"line\":" + symbols[i].Line + ",\"character\":" + symbols[i].Char + "},\"end\":{\"line\":" + symbols[i].Line + ",\"character\":" + (symbols[i].Char + symbols[i].Name.Length) + "}},\"selectionRange\":{\"start\":{\"line\":" + symbols[i].Line + ",\"character\":" + symbols[i].Char + "},\"end\":{\"line\":" + symbols[i].Line + ",\"character\":" + (symbols[i].Char + symbols[i].Name.Length) + "}}}");
    }
    sb.Append(']');
    return sb.ToString();
  }

  [Fact]
  public void ResolveSymbol_FindsFirstOccurrence_Deterministically()
  {
    string json = DocSymbolsJsonMulti(("Foo", 0, 5), ("Foo", 3, 2), ("Bar", 5, 0));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "Foo", 0);
    Assert.True(r.Found);
    Assert.Equal(0, r.Line);
    Assert.Equal(5, r.Character);
    Assert.Equal(2, r.TotalOccurrences);
  }

  [Fact]
  public void ResolveSymbol_SecondOccurrence_PicksSecond()
  {
    string json = DocSymbolsJsonMulti(("Foo", 0, 5), ("Foo", 3, 2));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "Foo", 1);
    Assert.True(r.Found);
    Assert.Equal(3, r.Line);
    Assert.Equal(2, r.Character);
  }

  [Fact]
  public void ResolveSymbol_ReferencedOnly_NotFound_NeverGuessed()
  {
    string json = DocSymbolsJsonMulti(("Foo", 0, 5));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "Bar", 0);
    Assert.False(r.Found);
    Assert.Equal(-1, r.Line);
  }

  [Fact]
  public void ResolveSymbol_OccurrenceOutOfRange_NotFound()
  {
    string json = DocSymbolsJsonMulti(("Foo", 0, 5));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "Foo", 5);
    Assert.False(r.Found);
  }

  [Fact]
  public async Task References_ReturnsAllSites_WithFileAndLine()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    string docSym = DocSymbolsJsonMulti(("Foo", 0, 5));
    string refs = "[" + LocationJson(uri, 0, 5) + "," + LocationJson(uri, 10, 2) + "]";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + docSym + "}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":" + refs + "}");
    LspCapabilities caps = new(true, true, true, true, true, false, true, false);
    LspSymbolService service = new(transport, caps);
    LspSymbolResult result = await service.ReferencesAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Equal(2, result.Sites.Count);
    Assert.Equal(0, result.OccurrenceUsed);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.Contains("textDocument/documentSymbol", written);
    Assert.Contains("textDocument/references", written);
  }

  [Fact]
  public async Task Definition_WithoutCapability_ReturnsUnsupported_WithoutWireCall()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspSymbolService service = new(transport, LspCapabilities.None);
    LspSymbolResult result = await service.DefinitionAsync("file:///C:/ws/A.cs", "Foo", 0);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.DoesNotContain("textDocument/definition", written);
  }

  [Fact]
  public async Task Definition_WithCapability_ReturnsLocation()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    string docSym = DocSymbolsJsonMulti(("Foo", 0, 5));
    string def = LocationJson(uri, 0, 5);
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + docSym + "}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":" + def + "}");
    LspCapabilities caps = new(true, true, true, true, true, false, true, false);
    LspSymbolService service = new(transport, caps);
    LspSymbolResult result = await service.DefinitionAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Single(result.Sites);
  }

  [Fact]
  public async Task References_ReferencedOnlySymbol_ReturnsUnsupported()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    string docSym = DocSymbolsJsonMulti(("Foo", 0, 5));
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + docSym + "}");
    LspCapabilities caps = new(true, true, true, true, true, false, true, false);
    LspSymbolService service = new(transport, caps);
    LspSymbolResult result = await service.ReferencesAsync(uri, "Bar", 0);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.DoesNotContain("textDocument/references", written);
  }

  [Fact]
  public async Task Hover_WithoutCapability_ReturnsUnsupported()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspSymbolService service = new(transport, LspCapabilities.None);
    LspSymbolResult result = await service.HoverAsync("file:///C:/ws/A.cs", "Foo", 0);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
  }

  [Fact]
  public async Task UnavailableTransport_ReturnsUnavailable()
  {
    LspSymbolService service = new(null, LspCapabilities.None);
    LspSymbolResult result = await service.DefinitionAsync("file:///C:/ws/A.cs", "Foo", 0);
    Assert.Equal(LspSymbolState.Unavailable, result.State);
  }
}
