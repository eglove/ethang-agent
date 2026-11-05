namespace eThangAgent.Lsp.ACL;

/// <summary>Finds the workspace's solution file - the argument the Roslyn language server
/// needs before it will load a compilation. Only a .slnx or .sln qualifies: the server's
/// solution/open request rejects a project file, and a workspace without a solution is
/// covered by its file-based project system instead.</summary>
public static class LspWorkspaceSolution
{
  private static readonly string[] SolutionExtensions = [".slnx", ".sln"];

  /// <summary>The solution file to load, or null when the workspace has none and the
  /// server's file-based project system is the only model available.</summary>
  public static string? Find(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    if (!Directory.Exists(workspaceRoot))
    {
      return null;
    }

    string[] files;
    try
    {
      files = Directory.GetFiles(workspaceRoot);
    }
#pragma warning disable CA1031 // Named decision: an unreadable workspace has no solution file, never a crash.
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return null;
    }
#pragma warning restore CA1031

    foreach (string extension in SolutionExtensions)
    {
      string? match = files.FirstOrDefault(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase));
      if (match is not null)
      {
        return match;
      }
    }

    return null;
  }
}
