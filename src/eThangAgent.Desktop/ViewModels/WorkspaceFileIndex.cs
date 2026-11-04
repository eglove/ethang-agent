using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The cached workspace file catalog the reference popup searches: one enumeration
///     stored as workspace-relative paths with '/' separators, ranked on demand by a
///     case-insensitive substring match over the path. <see cref="Search"/> reads the cache
///     synchronously; <see cref="RefreshAsync"/> is the only member that touches the file
///     system.</summary>
internal sealed class WorkspaceFileIndex(IFileSearchAccess search, string workspaceRoot)
{
  private readonly IFileSearchAccess _search = search ?? throw new ArgumentNullException(nameof(search));
  private readonly string _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
  private IReadOnlyList<string> _paths = [];

  /// <summary>The ranked options for <paramref name="query"/>, at most <paramref name="limit"/> rows.
  ///     An empty cache yields an empty list.</summary>
  public IReadOnlyList<ReferenceOption> Search(string query, int limit = 50)
  {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
    return _paths.Count == 0
        ? []
        :
        [
      .. _paths
          .Select(path => (Path: path, Rank: RankOf(path, query)))
          .Where(candidate => candidate.Rank >= 0)
          .OrderBy(candidate => candidate.Rank)
          .ThenBy(candidate => candidate.Path.Length)
          .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
          .Take(limit)
          .Select(candidate => new ReferenceOption(
              candidate.Path, string.Empty, ReferenceKind.File, "@" + candidate.Path)),
    ];
  }

  /// <summary>Re-enumerates the workspace and replaces the cache. A failed enumeration leaves
  ///     the previous cache in place.</summary>
  public async Task RefreshAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<string>> result =
        await _search.EnumerateFilesAsync(_workspaceRoot, "*", recurse: true, ct);
    if (!result.IsSuccess)
    {
      return;
    }

    _paths =
    [
      .. result.Value.Select(path =>
          Path.GetRelativePath(_workspaceRoot, path).Replace('\\', '/')),
    ];
  }

  /// <summary>0 exact file name, 1 file name prefix, 2 file name substring, 3 path substring,
  ///     -1 no match.</summary>
  private static int RankOf(string path, string query)
  {
    int separator = path.LastIndexOf('/');
    string fileName = separator < 0 ? path : path[(separator + 1)..];
    return path switch
    {
      _ when fileName.Equals(query, StringComparison.OrdinalIgnoreCase) => 0,
      _ when fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase) => 1,
      _ when fileName.Contains(query, StringComparison.OrdinalIgnoreCase) => 2,
      _ when path.Contains(query, StringComparison.OrdinalIgnoreCase) => 3,
      _ => -1,
    };
  }
}
