using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Replaces the older part of a conversation with an LLM-written summary.
///     Implemented by <see cref="DefaultContextCompactor"/>; fakes drive agent tests.</summary>
public interface IContextCompactor
{
  Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default);
}

/// <summary>What one compaction did.</summary>
public sealed record CompactionOutcome(int MessagesEvicted, int MessagesKept, TokenUsage? SummarizerUsage);
