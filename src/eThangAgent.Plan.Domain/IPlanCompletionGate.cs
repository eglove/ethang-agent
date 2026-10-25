namespace eThangAgent.PlanDomain;

/// <summary>The completion gate as the Plan Domain sees it (issue #283 Part 2).
///     <para>WHY A PORT HERE: the gate itself lives in the Tool Domain (it compares
///     workspace snapshots), and the Plan Domain must not depend on that machinery to
///     ask one question. This interface is the question: 'may this workspace's work be
///     declared complete?'. The composition root adapts the real gate to it.</para>
///     <para>The gate is consulted ONLY for a transition to Completed. Abandoned is
///     never gated - dropping half-finished work is the honest exit, and a gate that
///     trapped a user in a plan they want to stop would be worse than no gate.</para></summary>
public interface IPlanCompletionGate
{
  /// <summary>True when completion may proceed. False carries the refusal sentence,
  ///     which names what happened and the exact remedy.</summary>
  Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default);
}
