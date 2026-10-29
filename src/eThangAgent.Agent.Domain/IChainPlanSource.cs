using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Where a chain creation reads the plan it would execute (issue #313 D4).
///     <para>WHY A PORT: the plan record lives in the Plan Domain, which the Agent Domain
///     does not reference - and must not, or D6's whole reason for putting the creation
///     action here disappears. The composition root implements this over the real plan
///     store, so the creation can validate against a plan without the Agent Domain
///     learning what a plan is.</para>
///     <para>A READ, NEVER A WRITE. Creation reads the plan and writes only the chain; a
///     source that also wrote would be a second writer of the record it is validating
///     against.</para></summary>
public interface IChainPlanSource
{
  /// <summary>The plan facts a creation needs, or a typed failure when the plan cannot be
  ///     read.
  ///     <para>A MISSING PLAN AND AN UNREADABLE ONE ARE THE SAME ANSWER HERE, and the
  ///     caller renders it as <c>ChainPlanUnreadable</c> naming the underlying message:
  ///     either way nothing can be validated, and the remedy in both cases is to re-read the
  ///     plan rather than to change the declaration.</para></summary>
  /// <param name="planId">The plan id.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainPlanFacts>> GetPlanAsync(int planId, CancellationToken ct = default);
}

/// <summary>What a chain creation needs to know about a plan (issue #313).
///     <para>THE POSITIONS ARE THE POINT. Chains bind to plan steps BY POSITION, so
///     validating a declaration means asking which positions exist - the one fact the
///     Agent Domain cannot derive and does not want to learn how to.</para></summary>
/// <param name="PlanId">The plan id.</param>
/// <param name="Version">The plan's CURRENT version, recorded as the version the chain
///     started at (D8: a later revision is visible at brief time but never retroactive).</param>
/// <param name="Title">The plan's title, used as the chain's title.</param>
/// <param name="WorkspaceId">The workspace the plan belongs to, which the chain inherits so
///     the two records are scoped identically.</param>
/// <param name="StepPositions">Every position the plan has, in order.</param>
public sealed record ChainPlanFacts(int PlanId, int Version, string Title, string WorkspaceId,
    IReadOnlyList<int> StepPositions);
