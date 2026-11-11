namespace eThangAgent.Lsp.ACL;

/// <summary>Locates a workspace's build manifest wherever the project keeps it.
///     <para>A workspace root is a DIRECTORY, not a layout: a project may sit at the root,
///     beside a solution in <c>src/</c>, or anywhere else its authors chose. Looking only at
///     the top level makes every other layout invisible, and an invisible project is a
///     workspace the harness decides holds no code - so no server starts and every query is
///     silent for a reason the user cannot see.</para>
///     <para>The walk is bounded in both directions: a fixed directory budget and a fixed
///     depth, because a workspace is arbitrary user content and an unbounded walk over a
///     huge tree would cost the first edit more than the diagnostics are worth.</para></summary>
internal static class WorkspaceManifest
{
  /// <summary>Directories that never hold the manifest a workspace should be loaded from.
  ///     Build output and dependency caches carry their own project files, and loading one
  ///     of those loads a fragment of the workspace instead of the workspace.</summary>
  private static readonly HashSet<string> ExcludedDirectories =
  [
    with(StringComparer.OrdinalIgnoreCase),
    ".git", ".svn", ".hg", "bin", "obj", "node_modules", "packages",
    ".worktrees", ".venv", "venv", "__pycache__", ".idea", ".vs", ".vscode", ".nuget",
  ];

  /// <summary>How many directories one search may visit.</summary>
  private const int MaxDirectories = 4096;

  /// <summary>The deepest a manifest may sit below the workspace root.</summary>
  private const int MaxDepth = 8;

  /// <summary>Finds the shallowest file under <paramref name="root"/> that
  ///     <paramref name="matches"/>, returned as an absolute path, or null when none exists.
  ///     <para>Breadth-first, so a manifest at the root always wins over one buried deeper,
  ///     and the order among equal-depth candidates is the order the file system reports
  ///     them. A directory that cannot be read is skipped rather than failing the search:
  ///     one unreadable corner of a workspace must not hide the project beside it.</para></summary>
  internal static string? Find(string root, Func<string, bool> matches)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    ArgumentNullException.ThrowIfNull(matches);
    if (!Directory.Exists(root))
    {
      return null;
    }

    Queue<(string Directory, int Depth)> pending = new();
    pending.Enqueue((root, 0));
    int visited = 0;
    while (pending.Count > 0 && visited < MaxDirectories)
    {
      (string directory, int depth) = pending.Dequeue();
      visited++;

      string[] files;
      try
      {
        files = Directory.GetFiles(directory);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        continue;
      }

      foreach (string file in files)
      {
        if (matches(file))
        {
          return file;
        }
      }

      if (depth >= MaxDepth)
      {
        continue;
      }

      string[] children;
      try
      {
        children = Directory.GetDirectories(directory);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        continue;
      }

      foreach (string child in children)
      {
        if (!ExcludedDirectories.Contains(Path.GetFileName(child)))
        {
          pending.Enqueue((child, depth + 1));
        }
      }
    }

    return null;
  }
}
