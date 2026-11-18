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

  /// <summary>The transcript from a given sequence onward, with the total count: the read a
  ///     follower performs when what it already holds is the tail it wants to extend.
  ///     <para>ITS COST IS PROPORTIONAL TO WHAT ARRIVED, NEVER TO WHAT IS ON SCREEN. A caller
  ///     that polls must use this rather than <see cref="GetTranscriptAsync"/>, which reads
  ///     every message the transcript holds.</para></summary>
  /// <param name="id">The agent whose transcript is read.</param>
  /// <param name="fromSeq">The first sequence number to return, inclusive.</param>
  /// <param name="ct">Cancellation for the read.</param>
  /// <remarks>IMPLEMENTATIONS THAT CAN ANSWER CHEAPLY MUST. This default derives the tail from
  ///     <see cref="GetTranscriptAsync"/>, so a store that does not override it still answers
  ///     correctly by reading everything - the cost this member exists to remove. The SQL store,
  ///     which is what a follower in the desktop app calls, overrides it.</remarks>
  async Task<Result<TranscriptTail>> GetTranscriptTailAsync(AgentId id, int fromSeq,
      CancellationToken ct = default)
  {
    Result<IReadOnlyList<Message>> transcript = await GetTranscriptAsync(id, ct).ConfigureAwait(false);
    return transcript.IsSuccess
        ? Result.Success(new TranscriptTail([.. transcript.Value.Skip(fromSeq)], transcript.Value.Count))
        : Result.Failure<TranscriptTail>(transcript.Error);
  }

  /// <summary>Every persisted agent record in creation order (CreatedAt ascending) —
  ///     the corpus source for memory recall and session listing.</summary>
  Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default);

  /// <summary>Every persisted agent record bound to one workspace, in creation order
  ///     (CreatedAt ascending). Two spellings of one directory that differ only by case are
  ///     the same workspace, matching <c>WorkspaceIdentity.PathsEqual</c>.</summary>
  /// <param name="workspaceId">The workspace whose rows to read.</param>
  /// <param name="ct">Cancellation for the read.</param>
  /// <remarks>IMPLEMENTATIONS THAT CAN ANSWER CHEAPLY MUST. This default filters
  ///     <see cref="ListAllAsync"/>, so a store that does not override it still answers
  ///     correctly by reading every workspace's rows - the cost this member exists to remove.
  ///     The SQL store, which is what the Sessions catalog calls, overrides it.</remarks>
  async Task<Result<IReadOnlyList<AgentRecord>>> ListByWorkspaceAsync(string workspaceId,
      CancellationToken ct = default)
  {
    Result<IReadOnlyList<AgentRecord>> records = await ListAllAsync(ct).ConfigureAwait(false);
    return records.IsSuccess
        ? Result.Success<IReadOnlyList<AgentRecord>>([.. records.Value
            .Where(record => string.Equals(record.WorkspaceId, workspaceId,
                StringComparison.OrdinalIgnoreCase))])
        : Result.Failure<IReadOnlyList<AgentRecord>>(records.Error);
  }
}
