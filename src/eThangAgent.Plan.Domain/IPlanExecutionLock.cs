namespace eThangAgent.PlanDomain;

/// <summary>What a chain executing a plan holds, as the Plan Domain sees it.
///     <para>THE PLAN DOMAIN ASKS; THE CHAIN RECORD ANSWERS. The answer lives on Agent-domain
///     surface, so this port is the whole of the dependency - the same shape
///     <see cref="IPlanStepOwnershipGate"/> takes.</para>
///     <para>THE ANSWER IS THE CHAIN'S IDENTITY AND ITS REACH, never a boolean. A refusal names
///     the chain holding the plan, and the furthest position it holds is what separates an
///     append - which renumbers nothing - from an insert inside the range.</para></summary>
public interface IPlanExecutionLock
{
  /// <summary>What the chain executing <paramref name="planId"/> holds, or null when no chain
  ///     is executing it. An unreadable chain store answers null.</summary>
  /// <param name="planId">The plan being edited.</param>
  /// <param name="ct">Cancellation.</param>
  Task<PlanExecutionLock?> ExecutionAsync(int planId, CancellationToken ct = default);
}

/// <summary>The chain executing a plan, and the furthest plan position it holds.
///     <para>THE FURTHEST POSITION IS THE BOUNDARY, not the count: a chain built over a subset
///     of the plan holds positions that need not be contiguous. A new step landing at a
///     position greater than <see cref="LastPosition"/> shifts no claim the chain made.</para>
///     </summary>
/// <param name="ChainId">The chain executing the plan.</param>
/// <param name="LastPosition">The highest plan step position the chain holds.</param>
public sealed record PlanExecutionLock(int ChainId, int LastPosition);
