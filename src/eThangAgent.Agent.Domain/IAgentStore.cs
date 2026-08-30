using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Persistence seam for spawned agents and their transcripts. Owned by the Agent Domain; implemented by storage ACLs.</summary>
public interface IAgentStore
{
  Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default);

  Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default);

  Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default);

  Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default);

  /// <summary>Atomically replaces an agent's whole persisted transcript (compaction
  ///     survival): the previous rows go and the given messages become the transcript.
  ///     An empty replacement is rejected — the transcript never becomes empty.</summary>
  Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default);

  Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default);

  Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default);

  /// <summary>Every persisted agent record in creation order (CreatedAt ascending) —
  ///     the corpus source for memory recall and session listing.</summary>
  Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default);
}
