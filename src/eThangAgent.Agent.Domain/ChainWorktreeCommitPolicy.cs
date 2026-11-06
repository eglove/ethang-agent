using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The named refusal a completion earns when its worktree is uncommitted.</summary>
public static class ChainWorktreeCommitCode
{
  /// <summary>The step's worktree holds changes nobody committed, so the reconciler's merge
  ///     cannot land them.</summary>
  public const string WorktreeDirty = "ChainWorktreeDirty";
}

/// <summary>Whether a completion may settle a step whose session ran in its own worktree.
///     PURE: the caller reads the checkout and this decides.
///     <para>A REFUSAL, NOT A PARK: the member's session is still alive and can commit, so the
///     refusal reaches it as a tool result and costs one retry instead of a whole session.</para>
///     <para>A STEP WITH NO WORKTREE IS NEVER REFUSED. A sequential step runs in the workspace
///     checkout, which the member does not own, so its state is not this rule's question.</para></summary>
public static class ChainWorktreeCommitPolicy
{
  private const string Remedy =
      " The harness merges your branch, so your work must be committed. "
      + "Run `git add -A` and `git commit -m \"<what you did>\"` in that worktree, "
      + "then mark the step done again.";

  /// <summary>The refusal <paramref name="step"/> earns, or null when its completion may settle.
  /// </summary>
  /// <param name="step">The step the completion names.</param>
  /// <param name="worktreeIsDirty">Whether the checkout at the step's worktree holds
  ///     uncommitted changes.</param>
  public static DomainError? RefusalFor(ChainStep step, bool worktreeIsDirty)
  {
    ArgumentNullException.ThrowIfNull(step);
    return step.WorktreePath is { } path && worktreeIsDirty
      ? new DomainError(ChainWorktreeCommitCode.WorktreeDirty, Message(step.Position, path))
      : null;
  }

  private static string Message(int position, string path) =>
      string.Create(CultureInfo.InvariantCulture,
          $"chain step {position} is finished, but its worktree at {path} has uncommitted changes.")
      + Remedy;
}
