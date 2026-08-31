using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Between-turn durability for undelivered mailbox messages (FR-C5). Messages
///     land here only when a run settles with a non-empty box and are re-enqueued at the
///     next start; delivery itself is always pushed by the runtime — this store is never
///     polled for discovery (A1). Fakes in tests; SQLite in production.</summary>
public interface IMailboxStore
{
  /// <summary>Replaces any previously persisted batch for the agent with these messages.</summary>
  Task<Result<string>> PersistUndeliveredAsync(AgentId id, IReadOnlyList<PendingMessage> messages, CancellationToken ct = default);

  /// <summary>Loads the persisted batch (empty when none); used at run start to rehydrate.</summary>
  Task<Result<IReadOnlyList<PendingMessage>>> LoadUndeliveredAsync(AgentId id, CancellationToken ct = default);

  /// <summary>Drops the persisted batch — after the run drained it successfully.</summary>
  Task<Result<string>> ClearAsync(AgentId id, CancellationToken ct = default);
}
