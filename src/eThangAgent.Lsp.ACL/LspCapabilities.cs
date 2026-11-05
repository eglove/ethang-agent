namespace eThangAgent.Lsp.ACL;

/// <summary>The capability record from initialize, consulted before any request.</summary>
public sealed record LspCapabilities(
    bool HoverProvider,
    bool DefinitionProvider,
    bool ReferencesProvider,
    bool DocumentSymbolProvider,
    bool WorkspaceSymbolProvider,
    bool CallHierarchyProvider,
    bool ImplementationProvider,
    bool DiagnosticPullProvider)
{
  public static LspCapabilities None => new(false, false, false, false, false, false, false, false);

  public bool Supports(string method) => method switch
  {
    "textDocument/hover" => HoverProvider,
    "textDocument/definition" => DefinitionProvider,
    "textDocument/references" => ReferencesProvider,
    "textDocument/documentSymbol" => DocumentSymbolProvider,
    "workspace/symbol" => WorkspaceSymbolProvider,
    "textDocument/prepareCallHierarchy" => CallHierarchyProvider,
    "textDocument/implementation" => ImplementationProvider,
    "textDocument/diagnostic" => DiagnosticPullProvider,
    _ => true,
  };
}
