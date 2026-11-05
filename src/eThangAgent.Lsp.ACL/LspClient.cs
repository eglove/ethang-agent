using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>The LSP client (issue #353): initialize handshake + initialized
/// notification + capability record consulted before requests. Implements the
/// domain seam <c>ILspAccess</c> via the ACL transport.</summary>
public sealed class LspClient
{
  private readonly LspJsonRpcTransport _transport;
  private readonly LspServerConfig _config;
  private bool _initialized;

  public LspClient(LspJsonRpcTransport transport, LspServerConfig config)
  {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _config = config ?? throw new ArgumentNullException(nameof(config));
    Capabilities = LspCapabilities.None;
  }

  /// <summary>The capabilities this client advertises. A server decides what to register
  /// from this document: a server that sees no textDocument.diagnostic support registers
  /// no diagnostic handler, so pull diagnostics stay unreachable however the request is
  /// framed.</summary>
  internal const string ClientCapabilitiesJson =
      "{\"workspace\":{\"workspaceFolders\":true,\"configuration\":true,\"workspaceEdit\":{\"documentChanges\":true}},"
      + "\"textDocument\":{\"synchronization\":{\"didSave\":true},"
      + "\"publishDiagnostics\":{\"relatedInformation\":true,\"versionSupport\":true},"
      + "\"diagnostic\":{\"dynamicRegistration\":true,\"relatedDocumentSupport\":true},"
      + "\"definition\":{\"linkSupport\":true},\"references\":{},"
      + "\"hover\":{\"contentFormat\":[\"markdown\",\"plaintext\"]},"
      + "\"documentSymbol\":{\"hierarchicalDocumentSymbolSupport\":true},"
      + "\"callHierarchy\":{},\"implementation\":{}}}";

  public LspCapabilities Capabilities { get; private set; }
  public LspServerConfig Config => _config;

  public async Task<LspRequestResult> InitializeAsync(string workspaceUri, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceUri);
    string paramsJson = "{\"processId\":null,\"rootUri\":\"" + workspaceUri
        + "\",\"capabilities\":" + ClientCapabilitiesJson
        + ",\"workspaceFolders\":[{\"uri\":\"" + workspaceUri + "\",\"name\":\"workspace\"}]}";
    LspRequestResult result = await _transport.SendRequestAsync("initialize", paramsJson, ct).ConfigureAwait(false);
    if (!result.IsSuccess) return result;
    Capabilities = ParseCapabilities(result.ResultJson);
    await _transport.SendNotificationAsync("initialized", "{}", ct).ConfigureAwait(false);
    _initialized = true;
    return result;
  }

  public async Task<LspRequestResult> SendRequestAsync(string method, string paramsJson, CancellationToken ct = default)
  {
    if (_initialized && !Capabilities.Supports(method) && !IsDynamicallyRegistered(method))
    {
      return LspRequestResult.Unsupported(method);
    }
    return await _transport.SendRequestAsync(method, paramsJson, ct).ConfigureAwait(false);
  }

  /// <summary>A method the server registered after initialize is available even when the
  /// static capability record does not advertise it.</summary>
  private bool IsDynamicallyRegistered(string method)
  {
    foreach (string registered in _transport.DynamicRegistrations)
    {
      if (string.Equals(registered, method, StringComparison.Ordinal))
      {
        return true;
      }
    }
    return false;
  }

  private static LspCapabilities ParseCapabilities(string? resultJson)
  {
    if (string.IsNullOrWhiteSpace(resultJson)) return LspCapabilities.None;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(resultJson);
      if (!doc.RootElement.TryGetProperty("capabilities", out JsonElement caps)) return LspCapabilities.None;
      bool Get(string name) => caps.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Object));
      return new LspCapabilities(
        Get("hoverProvider"),
        Get("definitionProvider"),
        Get("referencesProvider"),
        Get("documentSymbolProvider"),
        Get("workspaceSymbolProvider"),
        Get("callHierarchyProvider"),
        Get("implementationProvider"),
        Get("diagnosticProvider"));
    }
    catch (JsonException) { return LspCapabilities.None; }
  }
}
