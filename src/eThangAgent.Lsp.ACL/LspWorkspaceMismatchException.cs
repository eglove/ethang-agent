namespace eThangAgent.Lsp.ACL;

public sealed class LspWorkspaceMismatchException(string message) : InvalidOperationException(message);
