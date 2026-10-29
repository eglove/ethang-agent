namespace eThangAgent.PlanDomain;

/// <summary>Who may mark a plan step Done, as the Plan Domain sees it (issue #301 D3).
///     <para>WHY A PORT HERE, exactly like <see cref="IPlanCompletionGate"/>: the answer lives
///     on the CHAIN record, which is Agent-domain surface, and the Plan Domain must not depend
///     on it to ask one question. This interface is the question: 'may this session complete
///     this step?'. The composition root adapts the chain record to it.</para>
///     <para>THE ORDINARY CASE MUST KEEP WORKING. A session started by hand is not executing a
///     chain step, and it may mark any step of its own plan Done - that is how plans have
///     always been worked. The gate only narrows a session that IS in a chain, where the
///     harness needs the completion to be the step that session was started for.</para>
///     <para>Consulted ONLY for a transition to Done. Pending and InProgress are the session's
///     own bookkeeping, and Skipped is the truthful exit the completion invariant depends on -
///     gating either would make the honest answer unreachable.</para></summary>
public interface IPlanStepOwnershipGate
{
  /// <summary>True when <paramref name="sessionId"/> may mark the step at
  ///     <paramref name="position"/> Done. A refusal carries a named code and the sentence
  ///     explaining it, naming the step the session IS assigned.</summary>
  /// <param name="planId">The plan being updated.</param>
  /// <param name="position">The 1-based step position being completed.</param>
  /// <param name="sessionId">The session reporting the completion.</param>
  /// <param name="ct">Cancellation.</param>
  Task<PlanStepOwnership> EvaluateAsync(int planId, int position, string sessionId,
      CancellationToken ct = default);
}

/// <summary>The gate's answer. <see cref="Allowed"/> is the only shape that needs no code, so
///     a caller cannot render a refusal without saying why.</summary>
public sealed record PlanStepOwnership(bool Allowed, string? Code, string? Message)
{
  /// <summary>The completion may proceed.</summary>
  public static PlanStepOwnership Permit { get; } = new(true, null, null);

  /// <summary>The completion is refused, with the code and sentence to render.</summary>
  public static PlanStepOwnership Refuse(string code, string message)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(code);
    ArgumentException.ThrowIfNullOrWhiteSpace(message);
    return new PlanStepOwnership(false, code, message);
  }
}
