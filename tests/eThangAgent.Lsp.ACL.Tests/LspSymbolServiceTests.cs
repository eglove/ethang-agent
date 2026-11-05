using System.Text;
using System.Text.Json.Nodes;
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
  public async Task Hover_RendersContents_NotBarePosition()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    JsonObject hover = new()
    {
      ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "class Foo" },
      ["range"] = new JsonObject
      {
        ["start"] = new JsonObject { ["line"] = 0, ["character"] = 5 },
        ["end"] = new JsonObject { ["line"] = 0, ["character"] = 8 },
      },
    };
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":" + hover + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.HoverAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Contains("class Foo", result.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnavailableTransport_ReturnsUnavailable()
  {
    LspSymbolService service = new(null, LspCapabilities.None);
    LspSymbolResult result = await service.DefinitionAsync("file:///C:/ws/A.cs", "Foo", 0);
    Assert.Equal(LspSymbolState.Unavailable, result.State);
  }

  [Fact]
  public void ResolveSymbol_MatchesMemberByName_WhenNameCarriesSignature()
  {
    string json = DocSymbolsJsonMulti(("GetStatus(AgentId, CancellationToken) : Task<Result<AgentRecord>>", 13, 36));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "GetStatus", 0);
    Assert.True(r.Found);
    Assert.Equal(13, r.Line);
    Assert.Equal(36, r.Character);
  }

  [Fact]
  public void ResolveSymbol_MatchesFieldByName_WhenNameCarriesTypeAnnotation()
  {
    string json = DocSymbolsJsonMulti(("_store : IAgentStore", 10, 32));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "_store", 0);
    Assert.True(r.Found);
    Assert.Equal(10, r.Line);
  }

  [Fact]
  public void ResolveSymbol_DoesNotMatchLongerName()
  {
    string json = DocSymbolsJsonMulti(("FooBar", 0, 5));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "Foo", 0);
    Assert.False(r.Found);
  }

  [Fact]
  public void ResolveSymbol_AcceptsSignatureAsQuery()
  {
    string json = DocSymbolsJsonMulti(("GetStatus(AgentId, CancellationToken) : Task<Result<AgentRecord>>", 13, 36));
    LspSymbolResolution r = LspSymbolService.ResolveSymbol(json, "GetStatus(AgentId, CancellationToken)", 0);
    Assert.True(r.Found);
  }

  [Fact]
  public async Task DocumentSymbols_SitesCarryTheDocumentUri()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.DocumentSymbolsAsync(uri);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Equal(uri, Assert.Single(result.Sites).Uri);
  }

  [Fact]
  public async Task WorkspaceSymbols_ParsesNestedLocation()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    string payload = "[{\"name\":\"Baz\",\"kind\":12,\"location\":" + LocationJson(uri, 4, 6) + "}]";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + payload + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.WorkspaceSymbolsAsync("Baz");
    Assert.Equal(LspSymbolState.Ready, result.State);
    LspSymbolSite site = Assert.Single(result.Sites);
    Assert.Equal(uri, site.Uri);
    Assert.Equal(4, site.Line);
    Assert.Equal(6, site.Character);
  }

  [Fact]
  public async Task WorkspaceSymbols_EmptyResult_IsNotReady()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonArray() }.ToJsonString());
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.WorkspaceSymbolsAsync("Nothing");
    Assert.NotEqual(LspSymbolState.Ready, result.State);
    Assert.False(string.IsNullOrWhiteSpace(result.Message));
  }

  private sealed class ScriptedWorkspaceModel(bool loaded) : ILspWorkspaceModel
  {
    public bool IsLoaded { get; private set; } = loaded;
    public int PrepareCalls { get; private set; }
    public int EnsureLoadedCalls { get; private set; }

    public Task PrepareAsync(string uri, CancellationToken ct = default)
    {
      PrepareCalls++;
      IsLoaded = true;
      return Task.CompletedTask;
    }

    public Task<bool> EnsureLoadedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
      EnsureLoadedCalls++;
      return Task.FromResult(IsLoaded);
    }
  }

  [Fact]
  public async Task References_UnloadedWorkspace_ReturnsStarting_NotAnEmptyReady()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false), new ScriptedWorkspaceModel(false));
    LspSymbolResult result = await service.ReferencesAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Starting, result.State);
    Assert.Empty(result.Sites);
    string written = Encoding.UTF8.GetString(output.ToArray());
    Assert.DoesNotContain("textDocument/references", written);
  }

  [Fact]
  public async Task References_LoadedWorkspace_AnswersNormally()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":[" + LocationJson(uri, 0, 5) + "]}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false), new ScriptedWorkspaceModel(true));
    LspSymbolResult result = await service.ReferencesAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Single(result.Sites);
  }

  [Fact]
  public async Task WorkspaceSymbols_UnloadedWorkspace_ReturnsStarting()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false), new ScriptedWorkspaceModel(false));
    LspSymbolResult result = await service.WorkspaceSymbolsAsync("Foo");
    Assert.Equal(LspSymbolState.Starting, result.State);
  }

  [Fact]
  public async Task DocumentSymbols_UnloadedWorkspace_ReturnsStarting()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false), new ScriptedWorkspaceModel(false));
    LspSymbolResult result = await service.DocumentSymbolsAsync("file:///C:/ws/A.cs");
    Assert.Equal(LspSymbolState.Starting, result.State);
  }

  [Fact]
  public async Task LoadWorkspace_UnloadedModel_ReturnsStarting()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    ScriptedWorkspaceModel model = new(false);
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false), model);
    LspSymbolResult result = await service.LoadWorkspaceAsync();
    Assert.Equal(LspSymbolState.Starting, result.State);
    Assert.Equal(1, model.EnsureLoadedCalls);
  }

  [Fact]
  public async Task LoadWorkspace_LoadedModel_ReturnsReady()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    ScriptedWorkspaceModel model = new(true);
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false), model);
    LspSymbolResult result = await service.LoadWorkspaceAsync();
    Assert.Equal(LspSymbolState.Ready, result.State);
  }

  [Fact]
  public async Task LoadWorkspace_NoTransport_ReturnsUnavailable()
  {
    LspSymbolService service = new(null, LspCapabilities.None);
    LspSymbolResult result = await service.LoadWorkspaceAsync();
    Assert.Equal(LspSymbolState.Unavailable, result.State);
  }

  [Fact]
  public async Task Definition_OccurrenceOutOfRange_NamesTheOccurrence_NotReferencedOnly()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5), ("Foo", 3, 2)) + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.DefinitionAsync(uri, "Foo", 9);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
    Assert.Contains("occurrence 9", result.Message, StringComparison.Ordinal);
    Assert.Contains("2", result.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("only referenced", result.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Definition_SymbolAbsentFromFile_StillSaysReferencedOnly()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.DefinitionAsync(uri, "Bar", 0);
    Assert.Equal(LspSymbolState.Unsupported, result.State);
    Assert.Contains("only referenced", result.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Hover_SiteCarriesTheRequestUri_BecauseHoverHasNoLocation()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    JsonObject hover = new()
    {
      ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "class Foo" },
      ["range"] = new JsonObject
      {
        ["start"] = new JsonObject { ["line"] = 7, ["character"] = 5 },
        ["end"] = new JsonObject { ["line"] = 7, ["character"] = 8 },
      },
    };
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":" + hover + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.HoverAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Ready, result.State);
    LspSymbolSite site = Assert.Single(result.Sites);
    Assert.Equal(uri, site.Uri);
    Assert.Equal(7, site.Line);
    Assert.Equal(5, site.Character);
    Assert.Equal("Foo", site.SymbolName);
  }

  [Fact]
  public async Task References_IdenticalSites_AreCollapsed()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    string uri = "file:///C:/ws/A.cs";
    string dup = LocationJson(uri, 3, 22);
    string refs = "[" + dup + "," + dup + "," + LocationJson(uri, 9, 4) + "]";
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + DocSymbolsJsonMulti(("Foo", 0, 5)) + "}");
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":" + refs + "}");
    LspSymbolService service = new(transport, new LspCapabilities(true, true, true, true, true, false, true, false));
    LspSymbolResult result = await service.ReferencesAsync(uri, "Foo", 0);
    Assert.Equal(LspSymbolState.Ready, result.State);
    Assert.Equal(2, result.Sites.Count);
    Assert.Equal(3, result.Sites[0].Line);
    Assert.Equal(9, result.Sites[1].Line);
  }
}
