using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

// Named decision (CA1849): enumeration here is local-disk directory walking; sync APIs
// inside the async wrapper keep the code simple without meaningful thread blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
namespace eThangAgent.FileSystem.ACL;

/// <summary>File-name search over a directory root for the find_files tool: Win32 glob
///     matching (EnumerationOptions.MatchType.Simple, * and ?), recursive by default,
///     with the standard exclusions (VCS internals, build output, dependency folders,
///     worktrees) and a hard result cap so a flooded tree cannot flood the transcript.</summary>
public sealed class DirectFileSearchAccess(int resultCap = 5000) : IFileSearchAccess
{
  /// <summary>Directory names never searched inside (exact, case-insensitive).</summary>
  private static readonly string[] ExcludedDirectoryNames =
  [
    ".git", ".svn", ".hg",
    "bin", "obj", "node_modules", "packages",
    ".worktrees", ".venv", "__pycache__", ".idea", ".vs", ".vscode",
  ];

  private readonly int _resultCap = resultCap;

  public Task<Result<IReadOnlyList<string>>> EnumerateFilesAsync(
      string root, string searchPattern, bool recurse, CancellationToken ct = default)
  {
    if (!Directory.Exists(root))
    {
      return Task.FromResult(Result.Failure<IReadOnlyList<string>>(
          new DomainError("DirectoryNotFound", $"Directory not found: {root}")));
    }

    List<string> matches = [];
    EnumerationOptions options = new()
    {
      MatchType = MatchType.Simple,
      IgnoreInaccessible = true,
      RecurseSubdirectories = false,
    };

    try
    {
      if (!recurse)
      {
        foreach (string path in Directory.EnumerateFiles(root, searchPattern, options))
        {
          matches.Add(path);
          if (matches.Count >= _resultCap)
          {
            break;
          }
        }

        return Task.FromResult(Result.Success<IReadOnlyList<string>>(matches));
      }

      // Recursive walk with exclusion pruning: EnumerateFiles with RecurseSubdirectories
      // cannot skip subtrees, so walk directories breadth-first and prune at each level.
      Walk(root, searchPattern, matches);
      return Task.FromResult(Result.Success<IReadOnlyList<string>>(matches));
    }
    catch (IOException ex)
    {
      return Task.FromResult(Result.Failure<IReadOnlyList<string>>(
          new DomainError("SearchFailed", ex.Message)));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Task.FromResult(Result.Failure<IReadOnlyList<string>>(
          new DomainError("SearchFailed", ex.Message)));
    }
  }

  private void Walk(string directory, string searchPattern, List<string> matches)
  {
    if (matches.Count >= _resultCap)
    {
      return;
    }

    foreach (string path in Directory.EnumerateFiles(directory, searchPattern, new EnumerationOptions
    {
      MatchType = MatchType.Simple,
      IgnoreInaccessible = true,
      RecurseSubdirectories = false,
    }))
    {
      matches.Add(path);
      if (matches.Count >= _resultCap)
      {
        return;
      }
    }

    foreach (string sub in Directory.EnumerateDirectories(directory, "*"))
    {
      if (ExcludedDirectoryNames.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
      {
        continue;
      }

      Walk(sub, searchPattern, matches);
      if (matches.Count >= _resultCap)
      {
        return;
      }
    }
  }
}
