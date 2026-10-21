using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Persists the root session around a turn loop: appends EVERY message the turn
///     added (user, assistant tool-call messages, tool results, continuation prompts,
///     nudges, turn-failure notices — the full slice from the already-persisted index) so the
///     transcript is a lossless resume source, and marks the row Completed on graceful
///     exit. Failed turns persist too: cancellation is protocol-repaired by the loop and
///     a provider failure appends the loop's "[turn failed]" System line — both leave
///     the transcript valid and self-describing, and dropping them would make resume
///     unfaithful. Persistence failures surface via reportError; the session
///     continues.</summary>
/// <remarks>Spec D9: the root agent also persists every message continuously, at the safe
///     point it is added (see <see cref="RootTranscriptSink"/>). The end-of-turn append is
///     therefore the BACKSTOP, not the primary write: it must append only what has not
///     already landed. A doubled turn corrupts resume — the exact failure continuous
///     persistence exists to prevent — so the skip is mandatory, never an optimization.
///     <para>The baseline is ABSOLUTE and comes from the CONVERSATION, never from a count of
///     what one sink instance happened to write. A sink's own tally is relative: it starts at
///     zero, so it coincides with the conversation index on a fresh session and diverges on
///     a resumed one (the hydrated prefix was persisted by the session that wrote it) and
///     while the sink is inert. The conversation is the only authority on its own length;
///     the sink is asked only how far it has advanced.</para></remarks>
public class RootSessionLifecycle(IAgentStore store, IChildTranscriptStore? transcriptSink = null)
{
  /// <summary>Appends the messages that are not yet persisted, starting at the ABSOLUTE
  ///     conversation index <paramref name="alreadyPersisted"/>. When a sink is wired it can
  ///     only ever have advanced FURTHER than that index (it starts there), so the later of
  ///     the two is the truth: a fresh session, a resumed session, and an inert sink all
  ///     append exactly the unpersisted slice. An index outside the conversation's range is
  ///     reported and skipped rather than silently slicing the wrong messages.
  ///     <para><paramref name="assistantText"/> is the turn's final assistant text, part of
  ///     this operation's contract since the brief named it. It is deliberately NOT persisted
  ///     as a separate row: the assistant message is already in the conversation and lands
  ///     through the slice above, so writing it again would duplicate the turn. It is
  ///     validated (non-null) so a caller that forgets it fails at the boundary.</para></summary>
  public virtual async Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
      int alreadyPersisted, string assistantText, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(assistantText);
    ArgumentNullException.ThrowIfNull(reportError);
    IReadOnlyList<Message> messages = conversation.Messages;
    int persisted = Math.Max(alreadyPersisted, await PersistedIndexAsync().ConfigureAwait(false));
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
  ///     would make the append-slice contract double-count. The sink performs the
  ///     replacement when it can (resetting its baseline with it); when the sink is inert
  ///     or its write failed, THIS path writes the replacement, so a compacted turn is never
  ///     silently lost. Failures surface via reportError; the session continues.</summary>
  public virtual async Task ReplaceTranscriptAsync(AgentId rootId, Conversation conversation, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(reportError);
    if (conversation.Messages.Count == 0)
    {
      reportError("Error [EmptyConversation]: transcript replacement skipped — the conversation is empty.");
      return;
    }

    if (transcriptSink is RootTranscriptSink rootSink)
    {
      if (await rootSink.ReplaceAsync(conversation.Messages).ConfigureAwait(false))
      {
        return;
      }
    }
    else if (transcriptSink is not null)
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

  /// <summary>The sink's absolute persisted index, or 0 when no sink is wired (legacy
  ///     wiring: the caller's count is the whole truth).</summary>
  private async Task<int> PersistedIndexAsync() => transcriptSink is null
      ? 0
      : await transcriptSink.FlushedCount().ConfigureAwait(false);
}
