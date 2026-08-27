using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Resolves tool-supplied paths against the workspace root and refuses
/// anything that resolves outside it. Segment-aware: a sibling directory whose name
/// merely shares a prefix with the root is correctly rejected. The root is normalized
/// once at construction — trailing separators stripped — and containment compares
/// case-insensitively (Windows paths are case-insensitive) so it never differs from a
/// candidate by casing or separators: hosts that hand over roots
/// with a trailing separator (folder pickers do) must not flip every equal-root
/// resolution into a false PathOutsideWorkspace.</summary>
public sealed class WorkspacePathResolver : IPathResolver
{
  private readonly string _root;

  public WorkspacePathResolver(string root)
  {
    if (string.IsNullOrWhiteSpace(root))
    {
      throw new ArgumentException("Workspace root must be a non-empty path.", nameof(root));
    }

    _root = NormalizeRoot(root);
  }

  public Result<string> Resolve(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return Result.Failure<string>(new DomainError("InvalidPath",
          "'path' must be a non-empty string."));
    }

    string candidate = Path.IsPathRooted(path) ? path : Path.Combine(_root, path);
    string full;
    try
    {
      full = Path.GetFullPath(candidate);
    }
    catch (Exception ex) when (
        ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return Result.Failure<string>(new DomainError("InvalidPath",
          $"'path' could not be resolved: {ex.Message}"));
    }

    return !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(full, _root, StringComparison.OrdinalIgnoreCase)
      ? Result.Failure<string>(new DomainError("PathOutsideWorkspace",
          $"'{path}' resolves to '{full}', which is outside the workspace '{_root}'. " +
          "Use a path inside the workspace."))
      : Result.Success(full);
  }

  /// <summary>Canonical form of the workspace root: fully qualified with any trailing
  ///     separators removed, so candidate comparisons never differ by separator or case.</summary>
  private static string NormalizeRoot(string root)
  {
    string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    // A drive root ("C:\") would trim to "C:" which no longer refers to the drive
    // root; restore the separator so it stays meaningful.
    return full.Length == 2 && full[1] == ':' ? full + Path.DirectorySeparatorChar : full;
  }
}
