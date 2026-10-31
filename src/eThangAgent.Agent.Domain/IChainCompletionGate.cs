namespace eThangAgent.AgentDomain;

/// <summary>The completion gate as the Agent Domain sees it (issue #304 D1).
///     <para>WHY A PORT HERE, exactly like the Plan Domain's <c>IPlanCompletionGate</c>: the
///     gate itself lives in the Tool Domain (it compares workspace snapshots) and reads the
///     State Domain's keys, and the Agent Domain must not depend on either to ask one
///     question. This interface is the question: 'may this workspace's work be declared
///     complete?'. The composition root adapts the real gate to it.</para>
///     <para>THE SAME QUESTION, NOT A SECOND ONE (D1). A step claiming Done and a chain
///     claiming Completed are both completion claims, so both ask this - one implementation,
///     one command set, one answer. A chain-level variant would drift from the step-level one
///     the first time the gate's wording changed.</para>
///     <para>CONSULTED ONLY FOR A COMPLETION CLAIM. <see cref="ChainStatus.Abandoned"/> is
///     never gated: it is the named exit every refusal points at, and a gate that trapped a
///     user in a chain they want to stop would be worse than no gate.</para></summary>
public interface IChainCompletionGate
{
  /// <summary>True when completion may proceed. False carries the refusal sentence, which
  ///     names what happened and the exact remedy.</summary>
  /// <param name="ct">Cancellation.</param>
  Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default);
}
