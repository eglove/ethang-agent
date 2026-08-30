using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Persists the root session around a turn loop: appends EVERY message the turn
///     added (user, assistant tool-call messages, tool results, continuation prompts,
///     nudges — the full slice from messageCountBefore) so the transcript is a lossless
///     resume source, and marks the row Completed on graceful exit. Failed turns persist
///     too: cancellation is protocol-repaired by the loop and a provider failure carries
///     just the user message — both leave the transcript valid, and dropping them would
///     make resume unfaithful. Persistence failures surface via reportError; the session
///     continues.</summary>
public class RootSessionLifecycle(IAgentStore store)
{
  public virtual async Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
      int messageCountBefore, Result<string> result, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(result);
    ArgumentNullException.ThrowIfNull(reportError);
    // messageCountBefore below zero (or beyond the end) would silently slice the wrong
    // range; a turn that appended nothing still has its count within bounds.
    if (messageCountBefore < 0 || messageCountBefore > conversation.Messages.Count)
    {
      reportError($"Error [InvalidSlice]: turn persistence skipped — message count {messageCountBefore} outside conversation range 0..{conversation.Messages.Count}.");
      return;
    }

    foreach (Message message in conversation.Messages.Skip(messageCountBefore))
    {
      Result<string> appended = await store.AppendMessageAsync(rootId, message).ConfigureAwait(false);
      if (!appended.IsSuccess)
      {
        reportError($"Error [{appended.Error.Code}]: {appended.Error.Message}");
      }
    }
  }

  /// <summary>Replaces the whole persisted transcript with the conversation's current
  ///     messages — the persistence path of a compacted turn, where a mid-turn shrink
  ///     would make the append-slice contract double-count. Failures surface via
  ///     reportError; the session continues.</summary>
  public virtual async Task ReplaceTranscriptAsync(AgentId rootId, Conversation conversation, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(reportError);
    if (conversation.Messages.Count == 0)
    {
      reportError("Error [EmptyConversation]: transcript replacement skipped — the conversation is empty.");
      return;
    }

    Result<string> replaced = await store.ReplaceTranscriptAsync(rootId, conversation.Messages).ConfigureAwait(false);
    if (!replaced.IsSuccess)
    {
      reportError($"Error [{replaced.Error.Code}]: {replaced.Error.Message}");
    }
  }

  public virtual async Task CompleteAsync(AgentId rootId, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(reportError);
    Result<AgentRecord> record = await store.GetAsync(rootId).ConfigureAwait(false);
    if (!record.IsSuccess)
    {
      reportError(record.IsSuccess
          ? $"Error [NotFound]: root session {rootId} was not found."
          : $"Error [{record.Error.Code}]: {record.Error.Message}");
      return;
    }

    Result<string> updated = await store.UpdateAsync(record.Value with
    {
      Status = AgentStatus.Completed,
      CompletedAt = DateTimeOffset.UtcNow,
    }).ConfigureAwait(false);
    if (!updated.IsSuccess)
    {
      reportError($"Error [{updated.Error.Code}]: {updated.Error.Message}");
    }
  }
}
