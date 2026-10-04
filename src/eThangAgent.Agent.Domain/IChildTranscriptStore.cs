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
}
