using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Seam for starting persisted children as independent actors, awaiting their
///     terminal transition, and interrupting runs. Implemented in infrastructure; faked in tests.</summary>
public interface IAgentRuntime
{
  /// <summary>Begins background execution of an already-persisted Running record. Fails with CapReached when at capacity.</summary>
  Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default);

  /// <summary>Await the terminal outcome of one child. Unbounded by design (spec D7): the
  ///     event-driven watchdog guards the child; the user's stop cancels the waiting turn.
  ///     Unknown ids fail NotFound; an already-settled child completes immediately with its
  ///     outcome; a watchdog same-id retry keeps the original await alive.</summary>
  Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default);

  /// <summary>Resumes a SETTLED child (Completed or Failed) on the SAME id with
  ///     'message' as its continuation prompt: the persisted transcript, anchor, and
  ///     grants carry over; Attempts increments. Fails NotRunning for unknown ids,
  ///     live children (steer those with Deliver), and ids this runtime cannot see;
  ///     InvalidMessage for empty text. The resumed run reuses the start pipeline
  ///     (slots, priority queue) and is awaited like any other child (WhenSettledAsync).</summary>
#pragma warning disable CA1716 // Named decision: 'Resume' is the spec's ubiquitous term (the plan's agent.resume contract) and is not a C# keyword; the collision is VB-only.
  Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default);
#pragma warning restore CA1716

  /// <summary>Interrupts one agent's run (FR-C6). The name is kept so every caller of the
  ///     scoped stop keeps one spelling; there is no subtree to walk, because an agent has no
  ///     descendants (issue #334). Unknown ids are a no-op.</summary>
  void InterruptSubtree(AgentId rootOfSubtree);

  /// <summary>Delivers one steering message to a LIVE run this runtime owns (issue #330),
  ///     answering with the host's own typed reason when it cannot.
  ///     <para>WHY THIS LIVES HERE RATHER THAN ONLY ON THE WIRE CHANNEL: an agent can run in a
  ///     host this container never dialled - a host launched by another host - so the only object
  ///     that knows which connection reaches that agent is the runtime that launched it. A caller
  ///     holding one connection would otherwise have to guess, and a guess lands on the wrong
  ///     process.</para>
  ///     <para>THE DEFAULT IS A TYPED REFUSAL, never a silent success: a runtime that cannot
  ///     reach an agent must say so, because the sender is a person who typed a message and
  ///     cannot tell a swallowed steer from a slow agent.</para></summary>
  /// <param name="agentId">The agent to steer.</param>
  /// <param name="text">The message text.</param>
  /// <param name="messageId">The identity the caller pinned its echo under.</param>
  /// <returns>True on acceptance, or the host's typed refusal.</returns>
  Task<Result<bool>> SteerAgentAsync(AgentId agentId, string text, Guid messageId)
      => Task.FromResult(Result.Failure<bool>(new DomainError(RuntimeErrors.SteeringUnavailable,
          "this runtime cannot deliver a steering message to an agent; it holds no connection to one.")));

  /// <summary>Interrupts active child runs by cancelling the token each run executes under.
  /// With no id, every active run owned by this runtime is interrupted; with an id, only that
  /// run. Unknown ids are a no-op — interruption is best-effort by design.</summary>
  void Interrupt(AgentId? agentId = null);
}

