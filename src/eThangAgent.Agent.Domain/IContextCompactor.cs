using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Replaces the older part of a conversation with an LLM-written summary.
///     Implemented by <see cref="DefaultContextCompactor"/>; fakes drive agent tests.</summary>
public interface IContextCompactor
{
  Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default);

  /// <summary>True when the compactor decided the next compaction should be a
  ///     SESSION HANDOFF (issue #234): the turn ends, a new session is launched with the
  ///     summary as its seed, and the old session completes gracefully. The consumer
  ///     (Agent.cs) checks this after a successful CompactAsync and acts on it.</summary>
  bool HandoffRequested { get; }
}

/// <summary>What one compaction did.</summary>
public sealed record CompactionOutcome(int MessagesEvicted, int MessagesKept, TokenUsage? SummarizerUsage);
