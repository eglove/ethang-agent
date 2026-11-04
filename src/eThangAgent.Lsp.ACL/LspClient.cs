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

  public LspCapabilities Capabilities { get; private set; }
  public LspServerConfig Config => _config;

  public async Task<LspRequestResult> InitializeAsync(string workspaceUri, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceUri);
    string paramsJson = "{\"processId\":null,\"rootUri\":\"" + workspaceUri + "\",\"capabilities\":{}}";
    LspRequestResult result = await _transport.SendRequestAsync("initialize", paramsJson, ct).ConfigureAwait(false);
    if (!result.IsSuccess) return result;
    Capabilities = ParseCapabilities(result.ResultJson);
    await _transport.SendNotificationAsync("initialized", "{}", ct).ConfigureAwait(false);
    _initialized = true;
    return result;
  }

  public async Task<LspRequestResult> SendRequestAsync(string method, string paramsJson, CancellationToken ct = default)
  {
    if (_initialized && !Capabilities.Supports(method))
    {
      return LspRequestResult.Unsupported(method);
    }
    return await _transport.SendRequestAsync(method, paramsJson, ct).ConfigureAwait(false);
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
        Get("implementationProvider"));
    }
    catch (JsonException) { return LspCapabilities.None; }
  }
}
