using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Replaces the older part of a conversation with an LLM-written summary.
///     Implemented by <see cref="DefaultContextCompactor"/>; fakes drive agent tests.</summary>
public interface IContextCompactor
{
  Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default);

  /// <summary>The overflow-recovery shape (issue #234 fix round): the forced compaction
  ///     after a ContextWindowExceeded failure is ALWAYS in place — the handoff is a
  ///     ceiling-path decision only, and a handoff here would re-send the identical
  ///     oversized request into the same overflow. Implementations must bypass the
  ///     handoff decision for this one call.</summary>
  Task<Result<CompactionOutcome>> CompactInPlaceAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default);
}

/// <summary>What one compaction did. A handoff outcome (<see cref="IsHandoff"/>) REPLACES
///     in-place compaction (issue #234): the conversation was NOT compacted — the old
///     transcript stays intact and completes gracefully — and the summary travels here,
///     to seed the new session. The decision rides the outcome, never instance state:
///     the compactor is a shared singleton, and a mutable flag on it was thread-sensitive
///     (Task 4 review finding 1).</summary>
/// <param name="MessagesEvicted">How many messages the compaction evicted (0 for a handoff).</param>
/// <param name="MessagesKept">How many messages the compaction kept (0 for a handoff).</param>
/// <param name="SummarizerUsage">The summarizer call's token usage, when reported.</param>
/// <param name="HandoffSummary">The summary text a handoff carries; null for an ordinary
///     in-place compaction.</param>
/// <param name="SummarizerModelId">The model that actually wrote the summary (issue #23):
///     the resolved writer, which is the SERVING model whenever the summarizer resolver
///     returned nothing. Null only for an outcome that made no summarizer call at all
///     (the tail-compression fallback), so a usage row is never attributed to a model that
///     did not run.</param>
/// <param name="SummarizerProvider">The provider whose price applies to the summarizer
///     call: the writer config's provider, or null when it names none.</param>
public sealed record CompactionOutcome(int MessagesEvicted, int MessagesKept, TokenUsage? SummarizerUsage,
    string? HandoffSummary = null, string? SummarizerModelId = null, string? SummarizerProvider = null)
{
  /// <summary>True when this outcome is a SESSION HANDOFF: the conversation was not
  ///     compacted, and the summary seeds a new session instead.</summary>
  public bool IsHandoff => HandoffSummary is not null;

  /// <summary>The handoff outcome: the conversation is left intact and the summary
  ///     launches a new session as its seed. The summarizer identity travels with it —
  ///     the summarizer was called and billed whether or not the conversation was
  ///     compacted (issue #23: a handoff records its usage row too).</summary>
  public static CompactionOutcome Handoff(string summary, TokenUsage? usage,
      string? summarizerModelId = null, string? summarizerProvider = null)
      => new(0, 0, usage, summary, summarizerModelId, summarizerProvider);
}
