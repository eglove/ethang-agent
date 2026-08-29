using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>
///     A validated list of workspace-relative paths to stage before a commit.
///     Every entry must be a non-empty relative path with no '..' segment.
///     Entries are stored verbatim — path separators, case, and spelling are
///     git's business, not ours. Validation is pure: no I/O, no git.
/// </summary>
public sealed record CommitFilePaths(IReadOnlyList<string> Paths)
{
  private const string ParamName = "files";
  private const string RuleTail =
      "Every entry must be a non-empty relative path (no drive, no leading slash, no '..' segment).";

  /// <summary>Validates the entries in rule order: null → empty → per-entry.
  ///     Per-entry order: non-empty → relative → no '..' segment. Each violation
  ///     is its own <c>InvalidParameterValue</c> error naming the offending entry.
  ///     Traversal is rejected by segment — 'a..b' and 'foo/..bar' are legal names.
  ///     Invariants hold for every Success value: non-null, non-empty entries;
  ///     every path non-empty and relative.</summary>
  public static Result<CommitFilePaths> Create(IReadOnlyList<string>? paths)
  {
    if (paths is null)
    {
      return Result.Failure<CommitFilePaths>(Missing());
    }

    if (paths.Count == 0)
    {
      return Result.Failure<CommitFilePaths>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{ParamName}' must not be empty when provided; omit the key to commit the index as-is. {RuleTail}"));
    }

    foreach (string p in paths)
    {
      DomainError? violation = Validate(p);
      if (violation is not null)
      {
        return Result.Failure<CommitFilePaths>(violation);
      }
    }

    return Result.Success(new CommitFilePaths(paths));
  }

  /// <summary>Per-entry rules, each its own error.</summary>
  private static DomainError? Validate(string p) =>
      string.IsNullOrWhiteSpace(p) ? Blank(p) : WholeTree(p) ?? KindViolation(p);

  private static DomainError? KindViolation(string p) =>
      IsAbsolute(p) ? Absolute(p) : Traversal(p);

  private static DomainError Missing() =>
      new(ToolErrorCodes.InvalidParameterValue,
          $"'{ParamName}' must be a non-empty array of relative paths. {RuleTail}");

  /// <summary>The whole-tree pathspec forms: 'git add -- .' stages everything,
  ///     so '.' and './' are rejected as too broad to be an intentional selection.</summary>
  private static DomainError? WholeTree(string p) =>
      p is "." or "./" ? RootSpec(p) : null;

  private static DomainError RootSpec(string p) =>
      new(ToolErrorCodes.InvalidParameterValue,
          $"'{ParamName}' must name specific paths, but got the whole-tree form '{p}'. {RuleTail}");
  private static DomainError Blank(string p) =>
      new(ToolErrorCodes.InvalidParameterValue,
          $"'{ParamName}' entries must be non-empty strings, but got '{p}'. {RuleTail}");

  private static DomainError Absolute(string p) =>
      new(ToolErrorCodes.InvalidParameterValue,
          $"'{ParamName}' entries must be relative paths, but got absolute '{p}'. {RuleTail}");

  private static DomainError Traversing(string p) =>
      new(ToolErrorCodes.InvalidParameterValue,
          $"'{ParamName}' entries must not contain a '..' segment, but got '{p}'. {RuleTail}");

  /// <summary>Drive, UNC, and rooted-slash checks. Path.IsPathRooted is NOT used:
  ///     '/a.cs' is rooted but not drive-absolute, and 'C:x' is drive-relative —
  ///     all rejected as out-of-workspace by hand.</summary>
  private static bool IsAbsolute(string p) =>
      p.StartsWith('/') || p.StartsWith('\\') ||
      (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]));

  /// <summary>Exact '..' segment check; 'a..b' is a legal file name.</summary>
  private static DomainError? Traversal(string p) =>
      p.Split('/').Any(segment => segment == "..") ? Traversing(p) : null;
}
