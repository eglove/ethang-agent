using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A validated git worktree name: lowercase ASCII letters, digits, and
///     hyphens (^[a-z0-9-]+$), 1..64 characters, never trimmed — the raw input must
///     already be in shape. Matching is culture-invariant, so a name is identical
///     on every machine. Gate every worktree path and branch this capability
///     derives through <see cref="Create"/>.</summary>
public sealed partial record WorktreeName
{
  /// <summary>Maximum allowed length of a worktree name.</summary>
  public const int MaxLength = 64;

  /// <summary>The validated name, verbatim.</summary>
  public string Value { get; }

  private WorktreeName(string value) => Value = value;

  /// <summary>Validates <paramref name="raw"/> as a worktree name: 1..64 characters
  ///     matching ^[a-z0-9-]+$, culture-invariant, with no trimming. The only way to
  ///     obtain a <see cref="WorktreeName"/> is through a success here; any non-conforming
  ///     value is an <c>InvalidName</c> error whose message names the rule and the pattern.
  ///     Null is a caller error (<c>ArgumentNullException</c>), not an InvalidName case.</summary>
  public static Result<WorktreeName> Create(string? raw)
  {
    ArgumentNullException.ThrowIfNull(raw);
    return raw.Length is < 1 or > MaxLength || !Shape().IsMatch(raw)
        ? Result.Failure<WorktreeName>(Err(raw))
        : Result.Success(new WorktreeName(raw));
  }

  public override string ToString() => Value;

  [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
  private static partial Regex Shape();

  private static DomainError Err(string raw)
  {
    string shown = raw.Length <= 80 ? raw : raw[..80] + "…";
    return new DomainError("InvalidName",
        $"'{shown}' is not a valid worktree name; use 1..{MaxLength} characters of " +
        "lowercase letters, digits, and hyphens (^[a-z0-9-]+$), with no trimming.");
  }
}
