using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>One running server's diagnostics query, addressed by document URI. The restart
///     gate is part of the seam: a query issued while documents are being re-opened would be
///     answered against a model the server has not finished rebuilding.</summary>
public interface ILspDiagnosticsQuery
{
  Task<LspDiagnosticsResult> PullAsync(string uri, CancellationToken ct = default);

  /// <summary>Holds queries until the documents are re-opened.</summary>
  void BeginRestart();

  /// <summary>Releases the queries held by <see cref="BeginRestart"/>.</summary>
  Task CompleteRestartAsync();
}

/// <summary>One running server's symbol query, addressed by document URI. The action names
///     the operation the same way the daemon's lsp.symbol request does, so the pipe and a
///     direct caller reach the same dispatch.</summary>
public interface ILspSymbolQuery
{
  Task<LspSymbolResult> ExecuteAsync(
      string action, string uri, string symbolName, int occurrence, int depth, string newName,
      CancellationToken ct = default);
}
