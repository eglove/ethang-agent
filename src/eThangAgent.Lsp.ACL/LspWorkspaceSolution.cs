namespace eThangAgent.Lsp.ACL;

/// <summary>Finds the workspace's solution file - the argument the Roslyn language server
/// needs before it will load a compilation. Only a .slnx or .sln qualifies: the server's
/// solution/open request rejects a project file (it never announces the load complete, so
/// the workspace stays Starting forever), and a workspace without a solution is covered by
/// its file-based project system instead.
///     <para>THE SOLUTION IS SEARCHED FOR, not assumed to be at the root: a workspace is
///     whatever directory the user opened, and a project that keeps its solution in
///     <c>src/</c> is ordinary. Looking only at the top level made that layout invisible.</para></summary>
public static class LspWorkspaceSolution
{
  private static readonly HashSet<string> SolutionExtensions =
  [
with(StringComparer.OrdinalIgnoreCase),     ".slnx", ".sln",
  ];

  /// <summary>The solution file to load, or null when the workspace has none and the
  /// server's file-based project system is the only model available.</summary>
  public static string? Find(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    return WorkspaceManifest.Find(workspaceRoot, path => SolutionExtensions.Contains(Path.GetExtension(path)));
  }
}
