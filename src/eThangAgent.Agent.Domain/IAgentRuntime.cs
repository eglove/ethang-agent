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

  /// <summary>Push-delivers a steering message into the child's mailbox. Fails NotRunning
  ///     for unknown/finished ids and MailboxFull when the box is at capacity — the
  ///     failure flows to the SENDER as a tool result (P3). Delivery to self is rejected
  ///     at the tool's validation, before this seam.</summary>
  Result<bool> Deliver(AgentId id, PendingMessage message);

  /// <summary>Interrupts an agent and ALL its running descendants (FR-C6): deepest-first
  ///     cancellation so parents observe children settling before their own repair.
  ///     Unknown ids are a no-op. The parent-control path for tree teardown.</summary>
  void InterruptSubtree(AgentId rootOfSubtree);

  /// <summary>Interrupts active child runs by cancelling the token each run executes under.
  /// With no id, every active run owned by this runtime is interrupted; with an id, only that
  /// run. Unknown ids are a no-op — interruption is best-effort by design.</summary>
  void Interrupt(AgentId? childId = null);
}
