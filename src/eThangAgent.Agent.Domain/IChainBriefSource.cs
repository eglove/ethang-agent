using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Where a chain brief's facts come from (issue #302).
///     <para>WHY A PORT: the facts live in three other domains — the plan record, the chain
///     record, and the workspace's gate commands — and the Agent Domain references none of
///     them. The composition root implements this over the real stores, so the brief can be
///     rendered and tested without the Agent Domain learning what a plan is.</para>
///     <para>EVERY METHOD IS A READ. The brief never writes: the seed is persisted by the
///     session factory, and a source that also wrote would be a second writer of the
///     conversation it is describing.</para></summary>
public interface IChainBriefSource
{
  /// <summary>The chain's own facts: its title, the plan it executes, and the role assigned to
  ///     the step at <paramref name="position"/> (issue #306).</summary>
  /// <param name="chainId">The chain id.</param>
  /// <param name="position">The 1-based step position this brief is for, so the source can
  ///     answer the role without the caller having to pass a whole assignment.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainBriefChain>> GetChainAsync(int chainId, int position, CancellationToken ct = default);

  /// <summary>The plan the chain executes: its goal, its version, and every step.</summary>
  /// <param name="planId">The plan id.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainBriefPlan>> GetPlanAsync(int planId, CancellationToken ct = default);

  /// <summary>Whether the <c>reviewing-plans</c> skill (#217) is installed in this session's
  ///     catalog, so a review brief can name its rubric instead of inventing one.
  ///     <para>A SEPARATE READ FROM THE CHAIN FACTS because it answers a question about the
  ///     SESSION's catalog, not about the record: the same chain step reviewed in a workspace
  ///     with the skill installed and one without must render different briefs, and only the
  ///     composition root knows what this session can load.</para>
  ///     <para>AN IMPLEMENT STEP NEVER ASKS, so a chain with no review step is unaffected.</para></summary>
  /// <param name="ct">Cancellation.</param>
  Task<Result<bool>> GetReviewRubricAvailableAsync(CancellationToken ct = default);

  /// <summary>The workspace's <c>goal/check</c> commands; EMPTY when the workspace declares
  ///     none. An unset gate is an ordinary answer, never a failure — the completion gate
  ///     treats it the same way.</summary>
  /// <param name="ct">Cancellation.</param>
  Task<Result<IReadOnlyList<string>>> GetGateCommandsAsync(CancellationToken ct = default);
}

/// <summary>The chain facts a brief needs.</summary>
/// <param name="ChainId">The chain id.</param>
/// <param name="Title">The chain's title, which also derives its ledger slug.</param>
/// <param name="PlanId">The plan the chain executes.</param>
/// <param name="Predecessors">Every chain step that already ran, in position order.</param>
/// <param name="Role">The role assigned to the step this brief is for (issue #306), read from
///     the chain record. Null when the chain holds no step at that position, which the renderer
///     already refuses — a brief for a step that is gone has nothing to say about its role.</param>
public sealed record ChainBriefChain(
    int ChainId, string Title, int PlanId, IReadOnlyList<ChainBriefPredecessor> Predecessors,
    ChainStepRole? Role = null);

/// <summary>The plan facts a brief needs.</summary>
/// <param name="PlanId">The plan id.</param>
/// <param name="Version">The plan's CURRENT version (the chain records the version it started
///     at; a difference is a revision, which is visible but never retroactive).</param>
/// <param name="Goal">The plan's goal — the model-authored free-text section.</param>
/// <param name="Steps">Every plan step, in position order.</param>
public sealed record ChainBriefPlan(
    int PlanId, int Version, string Goal, IReadOnlyList<ChainBriefPlanStep> Steps);
