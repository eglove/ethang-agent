using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Incremental persistence seam for one child run's transcript. Every
///     conversation mutation flows here at the loop safe point where it happens, so an
///     interrupt or crash leaves the run's history persisted up to the last safe point.
///     Implementations must NEVER observe the run's cancellation token: persistence
///     must complete after the run is cancelled, which is exactly when it matters.</summary>
public interface IChildTranscriptStore
{
  /// <summary>Persists one message immediately. Fire-and-forget safe: the loop never
  ///     waits on it.</summary>
  Task AppendAsync(Message message);

  /// <summary>Persists any pending messages. A no-op when everything already landed.</summary>
  Task FlushAsync();

  /// <summary>Number of conversation messages already persisted (the append baseline
  ///     the terminal path uses to avoid re-appending).</summary>
  Task<int> FlushedCount();

  /// <summary>Replaces the whole persisted transcript (compaction/shrink survival) and
  ///     resets the flushed baseline to the replacement's length.</summary>
  Task ReplaceAsync(IReadOnlyList<Message> messages);

  /// <summary>Reports that the caller persisted this message itself, so the sink must never
  ///     write it again. The end-of-turn backstop calls this for every message it appends:
  ///     a bounded flush can leave a message queued while the reported count sits below it,
  ///     and without this hand-off the sink would write that message a second time.
  ///     <para>Default: no-op returning false — a sink that cannot have queued the message
  ///     has nothing to retract. Implementations that queue messages MUST override it.</para>
  ///     <returns>True when the message was actually queued here and has been dropped.</returns></summary>
  bool NotePersistedByBackstop(Message message) => false;

  /// <summary>Replaces the transcript and reports whether THIS sink wrote it. The end-of-turn
  ///     path needs the answer: a sink that cannot write (inert, or a failed store write) must
  ///     not swallow a compacted turn, so the caller writes the replacement itself.
  ///     <para>Default: delegate to <see cref="ReplaceAsync"/> and report true — a sink that
  ///     does not distinguish the two has always been treated as having done the write.</para></summary>
  async Task<bool> TryReplaceAsync(IReadOnlyList<Message> messages)
  {
    await ReplaceAsync(messages).ConfigureAwait(false);
    return true;
  }

  /// <summary>Reports that the caller replaced the whole persisted transcript itself, so the
  ///     sink's baseline must agree with the replacement's length. The end-of-turn path
  ///     falls through to writing the replacement when the sink cannot (an inert sink, or a
  ///     failed write); without this hand-off the sink's baseline would keep describing the
  ///     PRE-compaction conversation and the next turn would skip its whole slice.
  ///     <para>Default: no-op — a sink with no baseline has nothing to correct.</para></summary>
  void NoteReplacedByBackstop(IReadOnlyList<Message> messages)
  {
  }
}
