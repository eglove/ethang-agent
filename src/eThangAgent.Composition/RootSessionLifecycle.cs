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
