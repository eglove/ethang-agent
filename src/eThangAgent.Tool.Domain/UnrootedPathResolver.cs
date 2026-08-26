using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Resolves model-supplied paths without a workspace root: absolute paths pass
///     through verbatim (normalized), relative paths resolve against the process working
///     directory, and no containment rule ever rejects a path. Malformed paths still fail
///     with the same InvalidPath error contract as WorkspacePathResolver.</summary>
public sealed class UnrootedPathResolver : IPathResolver
{
  // Path.GetFullPath no longer validates path characters on modern .NET, so
  // malformed input is detected explicitly against the platform's own rule.
  private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

  public Result<string> Resolve(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return Result.Failure<string>(new DomainError("InvalidPath",
          "'path' must be a non-empty string."));
    }

    if (path.AsSpan().IndexOfAny(InvalidPathChars) >= 0)
    {
      return Result.Failure<string>(new DomainError("InvalidPath",
          "'path' contains characters that are not valid in a path."));
    }

    try
    {
      return Result.Success<string>(Path.GetFullPath(path));
    }
    catch (Exception ex) when (
        ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return Result.Failure<string>(new DomainError("InvalidPath",
          $"'path' could not be resolved: {ex.Message}"));
    }
  }
}
