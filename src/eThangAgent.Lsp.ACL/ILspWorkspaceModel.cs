namespace eThangAgent.Lsp.ACL;

/// <summary>The server's model of one workspace: whether it has a compilation to answer
/// from, and what has to be sent before a query can be answered. Roslyn produces no
/// diagnostic - push or pull - for a workspace it has not loaded, so a query that skips
/// this step gets an empty item list that reads as a clean file.</summary>
public interface ILspWorkspaceModel
{
  /// <summary>True when a query answered now reflects the workspace: either no solution
  /// file exists (the server's file-based project system covers opened documents) or the
  /// server reported its project load complete.</summary>
  bool IsLoaded { get; }

  /// <summary>Loads the workspace into the server - once - and opens the document at
  /// <paramref name="uri"/> with its current disk text, so the query that follows has a
  /// compilation to answer from.</summary>
  Task PrepareAsync(string uri, CancellationToken ct = default);
}
