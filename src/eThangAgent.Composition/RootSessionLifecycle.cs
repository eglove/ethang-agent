using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Persists the root session around a turn loop: appends EVERY message the turn
///     added (user, assistant tool-call messages, tool results, continuation prompts,
///     nudges, turn-failure notices — the full slice from the already-persisted count) so the
///     transcript is a lossless resume source, and marks the row Completed on graceful
///     exit. Failed turns persist too: cancellation is protocol-repaired by the loop and
///     a provider failure appends the loop's "[turn failed]" System line — both leave
///     the transcript valid and self-describing, and dropping them would make resume
///     unfaithful. Persistence failures surface via reportError; the session
///     continues.</summary>
/// <remarks>Spec D9: the root agent also persists every message continuously, at the safe
///     point it is added (see <see cref="RootTranscriptSink"/>). The end-of-turn append is
///     therefore the BACKSTOP, not the primary write: it must append only what the sink has
///     not already written. A doubled turn corrupts resume — the exact failure continuous
///     persistence exists to prevent — so the skip is mandatory, never an optimization.</remarks>
public class RootSessionLifecycle(IAgentStore store, IChildTranscriptStore? transcriptSink = null)
{
  /// <summary>Appends the messages the continuous sink has NOT already persisted, starting
  ///     at <paramref name="alreadyPersisted"/>. The count comes from the sink's own flushed
  ///     baseline when one is wired (the sink is the authority on what it wrote); the
  ///     caller's value covers the legacy no-sink wiring. A count outside the conversation's
  ///     range is reported and skipped rather than silently slicing the wrong messages.</summary>
  public virtual async Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
      int alreadyPersisted, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(reportError);
    IReadOnlyList<Message> messages = conversation.Messages;
    int persisted = transcriptSink is null
        ? alreadyPersisted
        : await transcriptSink.FlushedCount().ConfigureAwait(false);
    if (persisted < 0 || persisted > messages.Count)
    {
      reportError($"Error [InvalidSlice]: turn persistence skipped — message count {persisted} outside conversation range 0..{messages.Count}.");
      return;
    }

    for (int i = persisted; i < messages.Count; i++)
    {
      Result<string> appended = await store.AppendMessageAsync(rootId, messages[i], CancellationToken.None)
          .ConfigureAwait(false);
      if (!appended.IsSuccess)
      {
        reportError($"Error [{appended.Error.Code}]: {appended.Error.Message}");
      }
    }
  }

  /// <summary>Replaces the whole persisted transcript with the conversation's current
  ///     messages — the persistence path of a compacted turn, where a mid-turn shrink
  ///     would make the append-slice contract double-count. The sink's baseline is reset
  ///     to the replacement's length in the same step, so the next turn's append starts
  ///     from the truth. Failures surface via reportError; the session continues.</summary>
  public virtual async Task ReplaceTranscriptAsync(AgentId rootId, Conversation conversation, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(reportError);
    if (conversation.Messages.Count == 0)
    {
      reportError("Error [EmptyConversation]: transcript replacement skipped — the conversation is empty.");
      return;
    }

    if (transcriptSink is not null)
    {
      await transcriptSink.ReplaceAsync(conversation.Messages).ConfigureAwait(false);
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
