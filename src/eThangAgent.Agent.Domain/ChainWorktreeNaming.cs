using System.Globalization;

namespace eThangAgent.AgentDomain;

/// <summary>The worktree name a group member's session is provisioned under: the chain it
///     belongs to and the step it executes, so two members of one chain never share a
///     checkout and a re-provisioned member lands on the same path it had before.
///     <para>THE SHAPE IS THE WORKTREE RULE'S, not a second one: lowercase letters, digits,
///     and hyphens only, which is what <c>WorktreeName</c> accepts.</para></summary>
public static class ChainWorktreeNaming
{
  /// <summary>The worktree name for <paramref name="position"/> of
  ///     <paramref name="chainId"/>.</summary>
  /// <param name="chainId">The chain the step belongs to; at least 1.</param>
  /// <param name="position">The step's 1-based position; at least 1.</param>
  public static string For(int chainId, int position)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(chainId, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    return string.Create(CultureInfo.InvariantCulture, $"chain-{chainId}-step-{position}");
  }
}
