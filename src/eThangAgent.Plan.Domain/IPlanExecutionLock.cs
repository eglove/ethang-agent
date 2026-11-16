namespace eThangAgent.PlanDomain;

/// <summary>Which chain, if any, is executing a plan right now - as the Plan Domain sees it.
///     <para>WHY A PORT HERE, exactly like <see cref="IPlanStepOwnershipGate"/>: the answer
///     lives on the CHAIN record, which is Agent-domain surface, and the Plan Domain must not
///     depend on it to ask one question. This interface is the question.</para>
///     <para>WHY THE ANSWER IS AN ID RATHER THAN A BOOLEAN: the refusal has to name the chain
///     holding the plan, because 'this plan is locked' without the name leaves a reader
///     searching for what locked it.</para>
///     <para>WHAT IT PROTECTS: chains bind to plan steps BY POSITION. Inserting, moving, or
///     removing a step renumbers them, so a session holding position N silently starts
///     completing the step that now sits there. A step's TEXT can still be rewritten in place,
///     and a plan no running chain holds is edited exactly as it always was.</para></summary>
public interface IPlanExecutionLock
{
  /// <summary>The id of the chain executing <paramref name="planId"/>, or null when none is.
  ///     <para>A store that cannot be read answers null: refusing every edit on an unreadable
  ///     chain record would make the record's own fault look like a lock.</para></summary>
  /// <param name="planId">The plan being edited.</param>
  /// <param name="ct">Cancellation.</param>
  Task<int?> ExecutingChainAsync(int planId, CancellationToken ct = default);
}
