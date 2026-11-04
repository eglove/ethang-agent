using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Lsp;

/// <summary>The LSP seam (issue #353): the Tool Domain's provider-neutral view over a
/// language server. The ACL owns the wire (stdio JSON-RPC, Content-Length framing);
/// the domain never learns transports exist. Expected failures flow as typed
/// <see cref="DomainError"/> values — never exceptions — and the client never blocks
/// indefinitely: a server that never responds yields a labeled Timeout failure.</summary>
public interface ILspAccess
{
  /// <summary>Performs the initialize handshake and records server capabilities.</summary>
  Task<Result<LspCapabilities>> InitializeAsync(Uri workspaceUri, CancellationToken ct = default);

  /// <summary>Sends one request after consulting the capability record; an
  /// unadvertised capability fails with Unsupported rather than hitting the wire.</summary>
  Task<Result<string>> SendRequestAsync(string method, string paramsJson, CancellationToken ct = default);

  /// <summary>Sends a notification (no response expected).</summary>
  Task SendNotificationAsync(string method, string paramsJson, CancellationToken ct = default);
}

/// <summary>The capability record returned by initialize, consulted before requests.</summary>
public sealed record LspCapabilities(
    bool HoverProvider,
    bool DefinitionProvider,
    bool ReferencesProvider,
    bool DocumentSymbolProvider,
    bool WorkspaceSymbolProvider,
    bool CallHierarchyProvider,
    bool ImplementationProvider);

/// <summary>Per-language server configuration: the executable that must already exist
/// (installation is out of scope), its arguments, and the file extensions it claims.</summary>
public sealed record LspServerConfig(string LanguageId, string Command, IReadOnlyList<string> Arguments, IReadOnlyList<string> Extensions);
