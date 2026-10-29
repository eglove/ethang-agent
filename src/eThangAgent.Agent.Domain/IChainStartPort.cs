using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Where a newly created chain's FIRST step gets its session (issue #313 D1/D5).
///     <para>WHY A PORT: the Agent Domain decides that a chain exists and which step runs
///     first; only the shell can start a session, because a session is a tab cell, a
///     container, and a host process. The application layer asks through this seam, so the
///     decision stays testable with a fake and the shell never re-derives the rule.</para>
///     <para>THE CREATING SESSION IS NOT THE CHAIN'S OWNER (D5). This port starts step 1 in
///     the workspace the plan belongs to, NOT as a continuation of the session that created
///     the chain: a session that closes immediately after creating a chain must not affect
///     it, which is what makes 'plan, then stop' a legitimate way to work.</para>
///     <para>A REFUSAL IS A VALUE, never an exception: every failure is a typed
///     <see cref="DomainError"/> the caller renders as a tool result.</para></summary>
public interface IChainStartPort
{
  /// <summary>Starts the session for <paramref name="assignment"/> and returns its id, or a
  ///     typed failure.
  ///     <para>The assignment names the chain, the plan, and the step position; the caller
  ///     has ALREADY claimed that step for the session it is about to start, so a failed
  ///     launch must be released by the caller rather than leaked.</para></summary>
  /// <param name="assignment">The chain step the new session is started for.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<string>> StartFirstStepAsync(ChainStepAssignment assignment, CancellationToken ct = default);
}
