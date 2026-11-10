namespace eThangAgent.Lsp.ACL;

/// <summary>One language a workspace holds: how many files it claims, and whether a manifest
///     naming it was found. A language found only by manifest reports a file count of zero.</summary>
public sealed record DetectedLanguage(string LanguageId, int FileCount, bool HasManifest);

/// <summary>Which of the catalog's languages a workspace holds.
///     <para>Detection answers what a repository NEEDS, so a manifest counts even when the
///     workspace holds no file of that language: a Go module with its sources not yet checked
///     out still wants a Go server. The walk is bounded in both directions - a fixed file
///     budget and a fixed elapsed budget - because a workspace is arbitrary user content and
///     an unbounded walk over a huge tree would cost the first edit more than the answers are
///     worth. A walk that stops early reports only what it reached.</para>
///     <para>A file is counted for the FIRST row that claims its extension, so the order of
///     <see cref="LspServerCatalog.All"/> decides an overlap: <c>.js</c> belongs to the
///     TypeScript row, which serves JavaScript, and is not also counted for any later row.</para></summary>
public static class LspWorkspaceLanguages
{
  /// <summary>Directories that never hold the code a workspace should be served from: build
  ///     output and dependency caches carry their own sources, and serving one of those serves
  ///     a fragment of the workspace instead of the workspace.</summary>
  private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
  {
    ".git", ".svn", ".hg", "bin", "obj", "node_modules", "packages",
    ".worktrees", ".venv", "venv", "__pycache__", ".idea", ".vs", ".vscode", ".nuget",
  };

  /// <summary>How many files one walk may visit.</summary>
  private const int MaxFiles = 2000;

  /// <summary>How long one walk may run.</summary>
  private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

  /// <summary>Every language the workspace holds, ordered by manifest presence, then by file
  ///     count, then by language id. A root that does not exist holds nothing.</summary>
  public static IReadOnlyList<DetectedLanguage> Detect(string workspaceRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    if (!Directory.Exists(workspaceRoot))
    {
      return [];
    }

    Dictionary<string, int> counts = new(StringComparer.Ordinal);
    HashSet<string> manifests = new(StringComparer.Ordinal);
    using CancellationTokenSource budget = new(Budget);
    int visited = 0;
    Walk(workspaceRoot, counts, manifests, ref visited, budget.Token);

    List<DetectedLanguage> detected = [];
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      bool hasManifest = manifests.Contains(row.LanguageId);
      int count = counts.TryGetValue(row.LanguageId, out int found) ? found : 0;
      if (hasManifest || count > 0)
      {
        detected.Add(new DetectedLanguage(row.LanguageId, count, hasManifest));
      }
    }

    detected.Sort((left, right) =>
    {
      int byManifest = right.HasManifest.CompareTo(left.HasManifest);
      if (byManifest != 0)
      {
        return byManifest;
      }

      int byCount = right.FileCount.CompareTo(left.FileCount);
      return byCount != 0 ? byCount : string.CompareOrdinal(left.LanguageId, right.LanguageId);
    });
    return detected;
  }

  private static void Walk(
      string directory,
      Dictionary<string, int> counts,
      HashSet<string> manifests,
      ref int visited,
      CancellationToken cancellationToken)
  {
    if (cancellationToken.IsCancellationRequested || visited >= MaxFiles)
    {
      return;
    }

    IEnumerable<string> files;
    try
    {
      files = Directory.EnumerateFiles(directory);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return;
    }

    foreach (string file in files)
    {
      if (cancellationToken.IsCancellationRequested || visited >= MaxFiles)
      {
        return;
      }

      visited++;
      foreach (LspLanguageRow row in LspServerCatalog.All)
      {
        if (row.HasManifestName(file) && manifests.Add(row.LanguageId))
        {
          break;
        }
      }

      foreach (LspLanguageRow row in LspServerCatalog.All)
      {
        if (!row.Claims(file))
        {
          continue;
        }

        counts[row.LanguageId] = counts.TryGetValue(row.LanguageId, out int previous) ? previous + 1 : 1;
        break;
      }
    }

    IEnumerable<string> children;
    try
    {
      children = Directory.EnumerateDirectories(directory);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return;
    }

    foreach (string child in children)
    {
      if (ExcludedDirectories.Contains(Path.GetFileName(child)))
      {
        continue;
      }

      Walk(child, counts, manifests, ref visited, cancellationToken);
    }
  }
}
