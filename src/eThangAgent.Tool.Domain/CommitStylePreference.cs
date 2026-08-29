using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Pure resolution semantics for a stored commit-style value: unset
///     (null or whitespace-only) resolves to the documented Conventional default;
///     an exact ordinal name round-trips (no trimming — near-misses are corrupt
///     data, not approximations); anything else is a typed
///     <c>InvalidStoredStyle</c> error — a corrupt stored value is information,
///     never a silent fallback.</summary>
public static class CommitStylePreference
{
  public static Result<CommitStyle> Resolve(string? stored)
    => string.IsNullOrWhiteSpace(stored)
      ? Result.Success(CommitStyle.Conventional)
      : stored switch
      {
        nameof(CommitStyle.Conventional) => Result.Success(CommitStyle.Conventional),
        nameof(CommitStyle.Gitmoji) => Result.Success(CommitStyle.Gitmoji),
        nameof(CommitStyle.None) => Result.Success(CommitStyle.None),
        _ => Result.Failure<CommitStyle>(new DomainError("InvalidStoredStyle",
            $"stored '{stored}' is not a valid commit style; expected exactly one of: " +
            "Conventional, Gitmoji, None")),
      };
}
