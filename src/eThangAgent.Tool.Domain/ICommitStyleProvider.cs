using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Seam for resolving the user-selected commit style at git_commit
///     execution time. The style is app-scoped host state — never a model-facing
///     parameter — so implementations read the current choice live, letting a
///     user's setting change take effect on the next commit of an open session.
///     Implementations are expected to translate an unset preference into the
///     documented default (<see cref="CommitStyle.Conventional"/>) and an
///     unrecognized stored value into a typed <c>Result</c> failure, which the
///     tool surfaces verbatim instead of silently falling back.</summary>
public interface ICommitStyleProvider
{
  /// <summary>Resolves the style to apply to the next commit.</summary>
  Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default);
}
