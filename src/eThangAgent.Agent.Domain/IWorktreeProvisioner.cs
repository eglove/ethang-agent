using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Provisions a git worktree inside a root, so a child run can be anchored
///     at an isolated checkout instead of the controller's working tree. The seam is
///     domain-owned: the handler names intent (root, validated name), the adapter
///     owns git machinery. Every name handed here must already satisfy the Tool
///     Domain's WorktreeName rule; implementations re-validate and translate their
///     outcomes into typed Result failures - never exceptions for expected failures.
///     Removal stays manual (the worktree tool): a failed child's worktree is
///     evidence, not garbage.</summary>
public interface IWorktreeProvisioner
{
  /// <summary>Creates a worktree named <paramref name="name"/> inside
  ///     <paramref name="repoRoot"/> and reports the provisioned directory.</summary>
  Task<Result<WorktreeProvision>> CreateAsync(string repoRoot, string name, CancellationToken ct = default);
}
