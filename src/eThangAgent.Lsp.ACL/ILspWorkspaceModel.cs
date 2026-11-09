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

  /// <summary>True when the server holds a compilation built from the workspace's own
  ///     project - the only state in which a diagnostic about a NAME is possible.
  ///     <para>Without a compilation the server answers from a file-based fallback that
  ///     parses the document and resolves nothing: a missing brace is still reported, but
  ///     an unresolved type, a wrong argument, an unused using and every other semantic
  ///     claim is invisible. A report from that state is not a clean file, it is a report
  ///     about a different question.</para></summary>
  bool HasCompilation { get; }

  /// <summary>Loads the workspace into the server - once - and opens the document at
  /// <paramref name="uri"/> with its current disk text, so the query that follows has a
  /// compilation to answer from.</summary>
  Task PrepareAsync(string uri, CancellationToken ct = default);

  /// <summary>Requests the load if it has not been requested yet and waits up to
  /// <paramref name="timeout"/> for the server's load-complete announcement. Returns the
  /// loaded state, so a caller can tell a finished load from one still running.</summary>
  Task<bool> EnsureLoadedAsync(TimeSpan timeout, CancellationToken ct = default);
}
